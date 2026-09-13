using System.Data;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

/// <summary>The bounded startup classification of one authenticated journal and operation row.</summary>
internal enum GrimoireOfflineTransitionTerminalSuffixOutcome : byte
{
    /// <summary>The exact launch row remains nonterminal and needs ordinary authenticated recovery.</summary>
    Nonterminal = 1,

    /// <summary>The exact terminal row's remaining journal suffix was completed and retired.</summary>
    Completed = 2,
}

/// <summary>
/// Finishes only the authenticated publication suffix whose destructive work and database verdict
/// already completed in a previous process.
/// </summary>
internal interface IGrimoireOfflineTransitionTerminalSuffixFinisher
{
    Task<Result<GrimoireOfflineTransitionTerminalSuffixOutcome>> FinishAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection recoveryConnection,
        GrimoireOfflineTransitionRecoveryEvidence evidence,
        CancellationToken cancellationToken);
}

/// <summary>The startup-only terminal suffix finisher.</summary>
/// <remarks>
/// This type owns no operation store, effect handler, or recovery adopter. Its database surface is
/// one strict read-only row projection and the canonical tuple read. Every durable write is a typed
/// monotonic journal edge, ending in typed retirement.
/// </remarks>
internal sealed class GrimoireOfflineTransitionTerminalSuffixFinisher(
    GrimoireOfflineTransitionLifecycleStore lifecycle,
    IServiceScopeFactory scopeFactory,
    CovenantOperationGate covenantGate,
    GrimoireConnectionAdmissionGate grimoireGate,
    GrimoireOfflineTransitionTerminalSuffixFaultSeam? afterLogicalSuffix = null)
    : IGrimoireOfflineTransitionTerminalSuffixFinisher
{
    private const int MaximumPayloadBytes = 4096;

    private readonly GrimoireOfflineTransitionLifecycleStore _lifecycle =
        lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly CovenantOperationGate _covenantGate =
        covenantGate ?? throw new ArgumentNullException(nameof(covenantGate));

    private readonly GrimoireConnectionAdmissionGate _grimoireGate =
        grimoireGate ?? throw new ArgumentNullException(nameof(grimoireGate));

    private readonly GrimoireOfflineTransitionTerminalSuffixFaultSeam _afterLogicalSuffix =
        afterLogicalSuffix ?? NoObservationAsync;

    public async Task<Result<GrimoireOfflineTransitionTerminalSuffixOutcome>> FinishAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection recoveryConnection,
        GrimoireOfflineTransitionRecoveryEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heldInstallationLock);
        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);
        ArgumentNullException.ThrowIfNull(recoveryConnection);
        ArgumentNullException.ThrowIfNull(evidence);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        Result<GrimoireOfflineTransitionTypedRecoveryState> recovered = await _lifecycle
            .RecoverAsync(heldInstallationLock, guardedDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure
            || recovered.Value is not
            {
                Outcome: GrimoireOfflineTransitionTypedRecoveryOutcome.Authenticated,
                Publication: { } publication,
            }
            || !ExactPublication(publication, evidence))
        {
            return Refusal();
        }

        Result<TerminalOperationSnapshot> row = await TerminalOperationSnapshot
            .ReadAsync(recoveryConnection, evidence.Binding.OperationId, cancellationToken)
            .ConfigureAwait(false);

        if (row.IsFailure)
        {
            return Refusal();
        }

        Result<GrimoireOfflineTransitionLaunchBinding> launch =
            GrimoireOfflineTransitionLaunch.FromCommittedCheckpoint(
                row.Value.CheckpointVersion,
                row.Value.CheckpointPayload);

        if (launch.IsFailure || !ExactLaunch(row.Value, launch.Value, evidence.Binding))
        {
            return Refusal();
        }

        if (!ExactCanonicalCheckpoint(row.Value, launch.Value))
        {
            return Refusal();
        }

        if (!ExactOperationChronology(row.Value))
        {
            return Refusal();
        }

        if (!row.Value.IsTerminal)
        {
            return ExactNonterminal(row.Value)
                ? GrimoireOfflineTransitionTerminalSuffixOutcome.Nonterminal
                : Refusal();
        }

        if (!ExactTerminal(row.Value, publication.Payload)
            || !ExactCatalog(await CovenantErasureInventorySource
                .ReadOfflineTransitionObservedStateAsync(
                    recoveryConnection,
                    transaction: null,
                    cancellationToken)
                .ConfigureAwait(false), launch.Value, publication.Payload.Lifecycle.TerminalIntent))
        {
            return Refusal();
        }

        CovenantDigest winner = GrimoireOfflineTransitionDatabaseReconciler.WinnerDigest(
            evidence.Binding,
            row.Value.Id,
            row.Value.State,
            row.Value.TerminalErrorCode,
            row.Value.Revision);

        GrimoireOfflineTransitionReconciliationEvidence? reconciliation =
            publication.Payload.Lifecycle.ReconciliationEvidence;

        if (publication.Payload.Lifecycle.State
                is not GrimoireOfflineTransitionState.DatabaseReconciliationPending
                    and not GrimoireOfflineTransitionState.RetirementPending
            || reconciliation is null
            || reconciliation.DatabaseTerminalWinnerDigest is { } recordedWinner
                && recordedWinner != winner)
        {
            return Refusal();
        }

        Result<GrimoireOfflineTransitionPhaseSession.ClosingOwner> admitted =
            GrimoireOfflineTransitionPhaseSession.ClosingOwner.ForVerifiedPublication(
                launch.Value,
                publication);

        if (admitted.IsFailure)
        {
            return Refusal();
        }

        if (ProveFreshProcess().IsFailure)
        {
            return Refusal();
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        Result<IGrimoireOfflineTransitionParentReceiptSink?> parent = await ResolveParentAsync(
            scope.ServiceProvider,
            heldInstallationLock,
            launch.Value,
            evidence.Binding.ParentReceiptBindingDigest,
            cancellationToken).ConfigureAwait(false);

        if (parent.IsFailure)
        {
            return Refusal();
        }

        Result verifiedParent = await VerifyRecordedParentAsync(
            evidence.Binding.ParentReceiptBindingDigest,
            reconciliation.Step,
            parent.Value,
            winner,
            cancellationToken).ConfigureAwait(false);

        if (verifiedParent.IsFailure)
        {
            return Refusal();
        }

        GrimoireOfflineTransitionPhaseSession phases = new(
            _lifecycle,
            heldInstallationLock,
            admitted.Value,
            parent.Value);

        Result advanced = await AdvanceRemainingAsync(
            phases,
            winner,
            cancellationToken).ConfigureAwait(false);

        if (advanced.IsFailure)
        {
            return Refusal();
        }

        Result observed = await _afterLogicalSuffix(
            GrimoireOfflineTransitionTerminalSuffixBoundary.RetirementPending,
            cancellationToken).ConfigureAwait(false);

        if (observed.IsFailure)
        {
            return Refusal();
        }

        Result<TerminalOperationSnapshot> reread = await TerminalOperationSnapshot
            .ReadAsync(recoveryConnection, evidence.Binding.OperationId, cancellationToken)
            .ConfigureAwait(false);

        if (reread.IsFailure || !row.Value.ExactlyEquals(reread.Value))
        {
            return Refusal();
        }

        Result retired = await phases.RetireAsync(cancellationToken).ConfigureAwait(false);

        return retired.IsSuccess
            ? GrimoireOfflineTransitionTerminalSuffixOutcome.Completed
            : Refusal();
    }

    private async Task<Result> AdvanceRemainingAsync(
        GrimoireOfflineTransitionPhaseSession phases,
        CovenantDigest winner,
        CancellationToken cancellationToken)
    {
        if (phases.State is GrimoireOfflineTransitionState.RetirementPending)
        {
            return Result.Success();
        }

        if (phases.ReconciliationStep is GrimoireOfflineTransitionReconciliationStep.CandidateVerified)
        {
            Result recorded = await phases.RecordTerminalWinnerAsync(winner, cancellationToken)
                .ConfigureAwait(false);

            if (recorded.IsFailure)
            {
                return recorded;
            }
        }

        if (phases.ReconciliationStep
            is GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner)
        {
            Result<CovenantDigest?> parent = await PublishParentAsync(
                phases,
                winner,
                cancellationToken).ConfigureAwait(false);

            if (parent.IsFailure)
            {
                return Result.Failure(parent.Error);
            }

            Result recorded = await phases.RecordParentReceiptAsync(parent.Value, cancellationToken)
                .ConfigureAwait(false);

            if (recorded.IsFailure)
            {
                return recorded;
            }
        }

        if (phases.ReconciliationStep
            is GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied)
        {
            Result fresh = ProveFreshProcess();

            if (fresh.IsFailure)
            {
                return fresh;
            }

            Result recorded = await phases.RecordLaneClosedAsync(cancellationToken)
                .ConfigureAwait(false);

            if (recorded.IsFailure)
            {
                return recorded;
            }
        }

        if (phases.ReconciliationStep is GrimoireOfflineTransitionReconciliationStep.LaneClosed)
        {
            Result fresh = ProveFreshProcess();

            if (fresh.IsFailure)
            {
                return fresh;
            }

            Result begun = await phases.BeginCovenantDispositionAsync(cancellationToken)
                .ConfigureAwait(false);

            if (begun.IsFailure)
            {
                return begun;
            }
        }

        if (phases.ReconciliationStep
            is GrimoireOfflineTransitionReconciliationStep.CovenantDispositionInFlight)
        {
            Result fresh = ProveFreshProcess();

            if (fresh.IsFailure)
            {
                return fresh;
            }

            Result completed = await phases.CompleteCovenantDispositionAsync(cancellationToken)
                .ConfigureAwait(false);

            if (completed.IsFailure)
            {
                return completed;
            }
        }

        return phases.ReconciliationStep
            is GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified
                ? await phases.PrepareRetirementAsync(cancellationToken).ConfigureAwait(false)
                : Result.Failure(Refusal().Error);
    }

    private Result ProveFreshProcess()
    {
        Result covenant = _covenantGate.ProveFreshTerminalSuffixPostcondition();

        return covenant.IsFailure
            ? covenant
            : _grimoireGate.ProveFreshTerminalSuffixPostcondition();
    }

    private static async Task<Result<IGrimoireOfflineTransitionParentReceiptSink?>> ResolveParentAsync(
        IServiceProvider services,
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionLaunchBinding launch,
        CovenantDigest? committedBinding,
        CancellationToken cancellationToken)
    {
        if (committedBinding is null)
        {
            return Result<IGrimoireOfflineTransitionParentReceiptSink?>.Success(null);
        }

        Result<IGrimoireOfflineTransitionParentReceiptSink?> resolved = await services
            .GetRequiredService<IGrimoireOfflineTransitionParentReceiptResolver>()
            .ResolveAsync(
                heldInstallationLock,
                launch.Kind,
                launch.EffectDigest,
                committedBinding,
                cancellationToken).ConfigureAwait(false);

        return resolved.IsSuccess
            && resolved.Value is { } sink
            && sink.BindingDigest == committedBinding
                ? resolved
                : Refusal<IGrimoireOfflineTransitionParentReceiptSink?>();
    }

    private static async Task<Result<CovenantDigest?>> PublishParentAsync(
        GrimoireOfflineTransitionPhaseSession phases,
        CovenantDigest winner,
        CancellationToken cancellationToken)
    {
        if (phases.Launch.Kind is GrimoireOfflineTransitionKind.CovenantReset
            || phases.Current.Payload.Binding.ParentReceiptBindingDigest is null)
        {
            return phases.ParentReceipt is null
                ? Result<CovenantDigest?>.Success(null)
                : Refusal<CovenantDigest?>();
        }

        if (phases.ParentReceipt is not { } sink)
        {
            return Refusal<CovenantDigest?>();
        }

        Result<CovenantDigest> published = await sink
            .PublishAndRereadAsync(winner, cancellationToken)
            .ConfigureAwait(false);

        return published.IsSuccess
            && published.Value == phases.Current.Payload.Binding.ParentReceiptBindingDigest
                ? Result<CovenantDigest?>.Success(published.Value)
                : Refusal<CovenantDigest?>();
    }

    /// <summary>
    /// Re-proves the exact completed parent winner when the inner journal already claims the receipt
    /// suffix was satisfied. This is verify-only: a later restart may not repair or replace history.
    /// </summary>
    internal static async Task<Result> VerifyRecordedParentAsync(
        CovenantDigest? committedBinding,
        GrimoireOfflineTransitionReconciliationStep step,
        IGrimoireOfflineTransitionParentReceiptSink? parent,
        CovenantDigest winner,
        CancellationToken cancellationToken)
    {
        if (committedBinding is null)
        {
            return parent is null
                ? Result.Success()
                : Result.Failure(Refusal().Error);
        }

        if (step < GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied)
        {
            return Result.Success();
        }

        if (parent is null)
        {
            return Result.Failure(Refusal().Error);
        }

        Result<CovenantDigest> verified = await parent
            .VerifyCompletedAsync(winner, cancellationToken).ConfigureAwait(false);

        return verified.IsSuccess && verified.Value == committedBinding
            ? Result.Success()
            : Result.Failure(Refusal().Error);
    }

    private static bool ExactPublication(
        GrimoireOfflineTransitionTypedPublication publication,
        GrimoireOfflineTransitionRecoveryEvidence evidence) =>
        publication.Raw.Envelope.InstallationId == evidence.InstallationId
        && publication.Raw.Envelope.SlotEpoch == evidence.SlotEpoch
        && publication.Raw.Envelope.Revision == evidence.Revision
        && publication.Raw.EnvelopeDigest == evidence.EnvelopeDigest
        && publication.Payload.Binding == evidence.Binding;

    private static bool ExactLaunch(
        TerminalOperationSnapshot row,
        GrimoireOfflineTransitionLaunchBinding launch,
        GrimoireOfflineTransitionBinding binding) =>
        row.Id == binding.OperationId
        && row.Kind == launch.OperationKind
        && row.RecoveryPolicy == launch.RecoveryPolicy
        && row.CheckpointReference == CovenantResetCheckpointInitiator.CheckpointReference(
            row.Kind,
            row.Id)
        && row.Revision >= 0
        && binding.ExpectedDatabaseOperationRevision <= long.MaxValue
        && row.Revision >= (long)binding.ExpectedDatabaseOperationRevision
        && launch.OperationId == binding.OperationId
        && launch.Kind == binding.Kind
        && launch.EffectDigest == binding.EffectDigest
        && launch.SourceDatasetGeneration == binding.SourceDatasetGeneration
        && launch.TargetDatasetGeneration == binding.TargetDatasetGeneration
        && launch.SourceEpochs == binding.SourceEpochs
        && launch.TargetEpochs == binding.TargetEpochs
        && launch.Digest == binding.DatabaseOperationLaunchBindingDigest;

    private static bool ExactTerminal(
        TerminalOperationSnapshot row,
        IGrimoireOfflineTransitionPayload payload) =>
        row.LeaseOwner is null
        && row.LeaseExpiresAt is null
        && row.StartedAt is { } started
        && row.HeartbeatAt is { } heartbeat
        && row.CompletedAt is { } completed
        && row.CreatedAt <= started
        && started <= heartbeat
        && heartbeat <= completed
        && payload.InFlightPhase is null
        && payload.Lifecycle.TerminalIntent switch
        {
            GrimoireOfflineTransitionTerminalIntent.CommitAndReopen =>
                row.State is LongRunningOperationState.Completed
                && row.TerminalErrorCode is null
                && payload.LastCompletedPhase is CovenantResetPhase.SidecarsVerified,
            GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen =>
                row.State is LongRunningOperationState.Failed
                && string.Equals(
                    row.TerminalErrorCode,
                    GrimoireOfflineTransitionDatabaseReconciler.PreEffectFailureCode,
                    StringComparison.Ordinal)
                && payload.LastCompletedPhase is CovenantResetPhaseMachine.First,
            _ => false,
        };

    private static bool ExactOperationChronology(TerminalOperationSnapshot row) =>
        row.AttemptCount >= 1
        && row.StartedAt is { } started
        && row.HeartbeatAt is { } heartbeat
        && row.CreatedAt <= started
        && started <= heartbeat
        && (row.CompletedAt is null || heartbeat <= row.CompletedAt);

    private static bool ExactNonterminal(TerminalOperationSnapshot row) =>
        row.CompletedAt is null
        && row.State switch
        {
            LongRunningOperationState.Pending
                or LongRunningOperationState.Running
                or LongRunningOperationState.Waiting
                or LongRunningOperationState.Cancelling => row.TerminalErrorCode is null,
            LongRunningOperationState.ReconciliationRequired =>
                row.LeaseOwner is null
                && row.LeaseExpiresAt is null
                && row.TerminalErrorCode is ErrorCodes.Data.ReconciliationFailed
                    or ErrorCodes.Covenant.MaintenanceFailed
                    or ErrorCodes.Covenant.ErasureIncomplete,
            _ => false,
        };

    private static bool ExactCanonicalCheckpoint(
        TerminalOperationSnapshot row,
        GrimoireOfflineTransitionLaunchBinding launch)
    {
        byte[] canonical = launch.Kind is GrimoireOfflineTransitionKind.CovenantReset
            ? CovenantRecoveryCheckpointCodec.Encode(
                CovenantRecoveryCheckpointCodec.DecodeCovenantOfflineTransitionLaunch(
                    row.CheckpointPayload).Value)
            : CovenantRecoveryCheckpointCodec.Encode(
                CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryTransitionLaunch(
                    row.CheckpointPayload).Value);

        return row.CheckpointPayload.AsSpan().SequenceEqual(canonical);
    }

    private static bool ExactCatalog(
        Result<CovenantOfflineTransitionSourceState> catalog,
        GrimoireOfflineTransitionLaunchBinding launch,
        GrimoireOfflineTransitionTerminalIntent intent)
    {
        if (catalog.IsFailure)
        {
            return false;
        }

        GrimoireOfflineTransitionObservedState observed = GrimoireOfflineTransitionLaunch.Classify(
            launch,
            catalog.Value.DatasetGeneration,
            new GrimoireOfflineTransitionEpochTuple(
                catalog.Value.AcceleratorEpoch,
                catalog.Value.KeyReclamationEpoch,
                catalog.Value.EnvelopeKeyEpoch));

        return intent switch
        {
            GrimoireOfflineTransitionTerminalIntent.CommitAndReopen =>
                observed is GrimoireOfflineTransitionObservedState.ExactlyApplied,
            GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen =>
                observed is GrimoireOfflineTransitionObservedState.ExactlyNotApplied,
            _ => false,
        };
    }

    private static Result<GrimoireOfflineTransitionTerminalSuffixOutcome> Refusal() =>
        Refusal<GrimoireOfflineTransitionTerminalSuffixOutcome>();

    private static Result<T> Refusal<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The terminal offline transition could not be verified exactly."));

    private static Task<Result> NoObservationAsync(
        GrimoireOfflineTransitionTerminalSuffixBoundary boundary,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    /// <summary>Every persisted operation column, parsed only from its canonical SQLite encoding.</summary>
    private sealed record TerminalOperationSnapshot(LongRunningOperation Operation)
    {
        internal Guid Id => Operation.Id;
        internal string Kind => Operation.Kind;
        internal LongRunningOperationState State => Operation.State;
        internal LongRunningOperationRecoveryPolicy RecoveryPolicy => Operation.RecoveryPolicy;
        internal DateTimeOffset CreatedAt => Operation.CreatedAt;
        internal DateTimeOffset? StartedAt => Operation.StartedAt;
        internal DateTimeOffset? HeartbeatAt => Operation.HeartbeatAt;
        internal DateTimeOffset? CompletedAt => Operation.CompletedAt;
        internal string? LeaseOwner => Operation.LeaseOwner;
        internal DateTimeOffset? LeaseExpiresAt => Operation.LeaseExpiresAt;
        internal int AttemptCount => Operation.AttemptCount;
        internal int CheckpointVersion => Operation.CheckpointVersion;
        internal byte[] CheckpointPayload => Operation.CheckpointPayload!;
        internal string? CheckpointReference => Operation.CheckpointReference;
        internal string? TerminalErrorCode => Operation.TerminalErrorCode;
        internal long Revision => Operation.Revision;

        internal bool IsTerminal => State is LongRunningOperationState.Completed
            or LongRunningOperationState.Failed
            or LongRunningOperationState.Abandoned;

        internal bool ExactlyEquals(TerminalOperationSnapshot other) =>
            Operation with { CheckpointPayload = null }
                == other.Operation with { CheckpointPayload = null }
            && CheckpointPayload.AsSpan().SequenceEqual(other.CheckpointPayload);

        internal static async Task<Result<TerminalOperationSnapshot>> ReadAsync(
            SqliteConnection connection,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            try
            {
                await using SqliteCommand command = connection.CreateCommand();

                command.CommandText =
                    """
                    SELECT
                        "Id", "Kind", "State", "RecoveryPolicy", "RootOperationId", "ParentOperationId",
                        "SessionId", "RunId", "InferenceRunId", "BudgetReservationId", "IdempotencyClaimId",
                        "CreatedAt", "StartedAt", "HeartbeatAt", "CompletedAt", "LeaseOwner", "LeaseExpiresAt",
                        "AttemptCount", "CheckpointVersion", "CheckpointPayload", "CheckpointReference",
                        "PublicSummary", "TerminalErrorCode", "Revision"
                    FROM "LongRunningOperations"
                    WHERE "Id" = @id
                    LIMIT 2;
                    """;

                _ = command.Parameters.AddWithValue("@id", operationId.ToString("N"));

                await using SqliteDataReader reader = await command
                    .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    || !TryRead(reader, out LongRunningOperation? operation)
                    || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return Refusal<TerminalOperationSnapshot>();
                }

                return new TerminalOperationSnapshot(operation);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Refusal<TerminalOperationSnapshot>();
            }
        }

        private static bool TryRead(SqliteDataReader reader, out LongRunningOperation operation)
        {
            operation = null!;

            if (!TryGuid(reader, 0, "N", out Guid id)
                || reader.GetValue(1) is not string kind
                || kind.Length is < 1 or > 100
                || !TryInt(reader, 2, out int stateCode)
                || !Enum.IsDefined((LongRunningOperationState)stateCode)
                || !TryInt(reader, 3, out int policyCode)
                || !Enum.IsDefined((LongRunningOperationRecoveryPolicy)policyCode)
                || !TryNullableGuid(reader, 4, "N", out Guid? root)
                || !TryNullableGuid(reader, 5, "N", out Guid? parent)
                || !TryNullableGuid(reader, 6, "D", out Guid? session)
                || !TryNullableGuid(reader, 7, "D", out Guid? run)
                || !TryNullableGuid(reader, 8, "D", out Guid? inference)
                || !TryNullableGuid(reader, 9, "N", out Guid? reservation)
                || !TryNullableGuid(reader, 10, "N", out Guid? claim)
                || !TryDate(reader, 11, required: true, out DateTimeOffset? created)
                || !TryDate(reader, 12, required: false, out DateTimeOffset? started)
                || !TryDate(reader, 13, required: false, out DateTimeOffset? heartbeat)
                || !TryDate(reader, 14, required: false, out DateTimeOffset? completed)
                || !TryNullableText(reader, 15, 200, out string? owner)
                || !TryDate(reader, 16, required: false, out DateTimeOffset? lease)
                || !TryInt(reader, 17, out int attempts)
                || attempts < 0
                || !TryInt(reader, 18, out int checkpointVersion)
                || checkpointVersion <= 0
                || reader.GetValue(19) is not byte[] payload
                || payload.Length is < 1 or > MaximumPayloadBytes
                || !TryNullableText(reader, 20, 1024, out string? checkpointReference)
                || checkpointReference is null
                || reader.GetValue(21) is not string summary
                || summary.Length > 1024
                || !TryNullableText(reader, 22, 200, out string? terminalCode)
                || reader.GetValue(23) is not long revision
                || revision < 0)
            {
                return false;
            }

            operation = new LongRunningOperation(
                id,
                kind,
                (LongRunningOperationState)stateCode,
                (LongRunningOperationRecoveryPolicy)policyCode,
                root,
                parent,
                session,
                run,
                inference,
                reservation,
                claim,
                created!.Value,
                started,
                heartbeat,
                completed,
                owner,
                lease,
                attempts,
                checkpointVersion,
                payload,
                checkpointReference,
                summary,
                terminalCode,
                revision);

            return true;
        }

        private static bool TryInt(SqliteDataReader reader, int ordinal, out int value)
        {
            value = 0;

            return reader.GetValue(ordinal) is long raw
                && raw is >= int.MinValue and <= int.MaxValue
                && (value = (int)raw) == raw;
        }

        private static bool TryGuid(
            SqliteDataReader reader,
            int ordinal,
            string format,
            out Guid value)
        {
            value = Guid.Empty;

            if (reader.GetValue(ordinal) is not string raw
                || !Guid.TryParseExact(raw, format, out value)
                || value == Guid.Empty)
            {
                return false;
            }

            string canonical = format == "N"
                ? value.ToString("N")
                : value.ToString("D").ToUpperInvariant();

            return string.Equals(raw, canonical, StringComparison.Ordinal);
        }

        private static bool TryNullableGuid(
            SqliteDataReader reader,
            int ordinal,
            string format,
            out Guid? value)
        {
            value = null;

            if (reader.IsDBNull(ordinal))
            {
                return true;
            }

            if (!TryGuid(reader, ordinal, format, out Guid parsed))
            {
                return false;
            }

            value = parsed;

            return true;
        }

        private static bool TryDate(
            SqliteDataReader reader,
            int ordinal,
            bool required,
            out DateTimeOffset? value)
        {
            value = null;

            if (reader.IsDBNull(ordinal))
            {
                return !required;
            }

            if (reader.GetValue(ordinal) is not string raw
                || !UtcInstantText.TryParse(raw, out DateTimeOffset parsed)
                || !string.Equals(raw, UtcInstantText.Format(parsed), StringComparison.Ordinal))
            {
                return false;
            }

            value = parsed;

            return true;
        }

        private static bool TryNullableText(
            SqliteDataReader reader,
            int ordinal,
            int maximumLength,
            out string? value)
        {
            value = null;

            if (reader.IsDBNull(ordinal))
            {
                return true;
            }

            if (reader.GetValue(ordinal) is not string text
                || text.Length is < 1
                || text.Length > maximumLength)
            {
                return false;
            }

            value = text;

            return true;
        }
    }
}
