using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The authenticated journal reduced to what a recovery pass compares its catalog against.
/// </summary>
/// <remarks>
/// A reduction rather than the publication itself, for the same reason
/// <c>InstallationResetNestedTransitionEvidence</c> takes reduced records: the authority the journal
/// carries is a decision somebody else already made, and a component that took the whole publication
/// could be tempted to re-derive it. What is needed here is the binding, and the three envelope values
/// that say which revision of which slot minted it.
/// </remarks>
internal sealed record GrimoireOfflineTransitionRecoveryEvidence(
    GrimoireOfflineTransitionBinding Binding,
    ulong SlotEpoch,
    ulong Revision,
    CovenantDigest EnvelopeDigest);

/// <summary>
/// Loads and verifies the minimum persisted Covenant authority a crashed transition needs back.
/// </summary>
/// <remarks>
/// It reads and never writes. Everything it touches is state the ordinary bootstrap also reads, over
/// the recovery-only connection that cannot install, create, rekey, or restore — so the pass that
/// decides whether a transition may be resumed cannot change the evidence it is deciding on.
/// </remarks>
internal interface ICovenantRecoveryAuthorityBootstrapper
{

    Task<Result<ICovenantClosedRecoveryHandoff>> LoadAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection recoveryConnection,
        GrimoireOfflineTransitionRecoveryEvidence evidence,
        CancellationToken cancellationToken);

}

/// <summary>
/// The one-use permission to put this process into the closed posture a crashed transition left.
/// </summary>
/// <remarks>
/// An interface because the pass that spends it is a different component from the one that mints it,
/// and the seam between them is where the authority order is enforced rather than assumed. What it
/// exposes is deliberately thin: the operation the journal names, and the one act of spending.
/// </remarks>
internal interface ICovenantClosedRecoveryHandoff
{

    /// <summary>The durable operation the journal names, and the only one this handoff can resume.</summary>
    Guid OperationId { get; }

    Task<Result> ConsumeAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        GrimoireOfflineTransitionRecoveryEvidence evidence,
        SqliteConnection recoveryConnection,
        CancellationToken cancellationToken);

}

/// <summary>The production handoff, minted only by the bootstrapper that verified it.</summary>
/// <remarks>
/// One use, enforced by an interlocked claim rather than by a flag a caller could read and race. A
/// handoff that could be spent twice would let a retry loop adopt a second owner over a scope the
/// first one is still holding, and the gate's refusal there is a thrown exception rather than a
/// result — a shape that turns a recoverable installation into a crashed startup.
///
/// <para>It is bound to the guarded root it was minted under and to the exact journal revision it was
/// verified against. Neither of those can be re-derived at consumption time, and a handoff spent
/// against a journal that has since moved would be publishing authority for a run nobody observed.</para>
/// </remarks>
internal sealed class CovenantClosedRecoveryHandoff : ICovenantClosedRecoveryHandoff
{

    private readonly CovenantOperationGate _gate;

    private readonly CovenantRuntimeGenerationProvider _runtime;

    private readonly CovenantEnvelopeMasterKeyProvider _keys;

    private readonly CovenantAvailability _availability;

    private readonly IHostProcessToolsRuntimePolicy _hostToolsPolicy;

    private readonly ISecretStore _secretStore;

    private readonly string _guardedDirectory;

    private readonly ulong _journalSlotEpoch;

    private readonly ulong _journalRevision;

    private readonly CovenantDigest _journalEnvelopeDigest;

    private int _consumed;

    internal CovenantClosedRecoveryHandoff(
        CovenantOperationGate gate,
        CovenantRuntimeGenerationProvider runtime,
        CovenantEnvelopeMasterKeyProvider keys,
        CovenantAvailability availability,
        IHostProcessToolsRuntimePolicy hostToolsPolicy,
        ISecretStore secretStore,
        string guardedDirectory,
        GrimoireOfflineTransitionRecoveryEvidence evidence,
        Guid operationId,
        string operationKind,
        CovenantExclusiveRecoveryOwner owner,
        GrimoireOfflineTransitionObservedState observedDatabaseState)
    {

        _gate = gate;

        _runtime = runtime;

        _keys = keys;

        _availability = availability;

        _hostToolsPolicy = hostToolsPolicy;

        _secretStore = secretStore;

        _guardedDirectory = guardedDirectory;

        _journalSlotEpoch = evidence.SlotEpoch;

        _journalRevision = evidence.Revision;

        _journalEnvelopeDigest = evidence.EnvelopeDigest;

        OperationId = operationId;

        OperationKind = operationKind;

        Owner = owner;

        ObservedDatabaseState = observedDatabaseState;

    }

    /// <inheritdoc />
    public Guid OperationId { get; }

    /// <summary>The durable ledger kind that operation is filed under.</summary>
    internal string OperationKind { get; }

    /// <summary>The exclusive owner reconstructed from the launch this journal is bound to.</summary>
    internal CovenantExclusiveRecoveryOwner Owner { get; }

    /// <summary>Whether the catalog is at the launch's source tuple or at its preselected target.</summary>
    internal GrimoireOfflineTransitionObservedState ObservedDatabaseState { get; }

    /// <summary>
    /// Spends the handoff, leaving the gate closed around the recovered owner.
    /// </summary>
    /// <remarks>
    /// It publishes the persisted availability and authority facts and adopts the durable recovery
    /// owner, and it publishes no database readiness and mints no token a later caller could reuse.
    /// The facts are present precisely so that the one exclusive lease can be resumed; present facts
    /// behind a gate the adopted owner has closed are not availability.
    /// </remarks>
    public async Task<Result> ConsumeAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        GrimoireOfflineTransitionRecoveryEvidence evidence,
        SqliteConnection recoveryConnection,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(evidence);

        ArgumentNullException.ThrowIfNull(recoveryConnection);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        if (!string.Equals(guardedDirectory, _guardedDirectory, StringComparison.Ordinal)
            || evidence.SlotEpoch != _journalSlotEpoch
            || evidence.Revision != _journalRevision
            || evidence.EnvelopeDigest != _journalEnvelopeDigest
            || evidence.Binding.OperationId != OperationId)
        {

            return CovenantRecoveryAuthorityBootstrapper.Refusal();

        }

        if (Interlocked.Exchange(ref _consumed, 1) != 0)
        {

            return CovenantRecoveryAuthorityBootstrapper.Refusal();

        }

        string? masterApiKey = await _secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(masterApiKey))
        {

            return CovenantRecoveryAuthorityBootstrapper.Refusal();

        }

        // The persisted canonical position first, because the exclusive lease this recovery is about
        // to resume captures the dataset generation from the availability snapshot, and a lease that
        // captured none is refused by the coordinator after the gate is already closed.
        if (!await CovenantPersistedAvailabilityPublisher.PublishAsync(
                _availability,
                recoveryConnection,
                acceleratorHealthy: false,
                CovenantHealthTransition.Bootstrap,
                cancellationToken).ConfigureAwait(false))
        {

            return CovenantRecoveryAuthorityBootstrapper.Refusal();

        }

        // The same read-only publication the ordinary bootstrap performs. Here its failure is a
        // refusal rather than a warning: this path exists to obtain authority a handler then spends,
        // and continuing without it would dispatch a handler that cannot take its own lease.
        if (!await CovenantAuthorityStartupReconciler.ReconcileAsync(
                recoveryConnection,
                _runtime,
                _keys,
                _availability.Current,
                _hostToolsPolicy,
                masterApiKey,
                cancellationToken).ConfigureAwait(false))
        {

            return CovenantRecoveryAuthorityBootstrapper.Refusal();

        }

        try
        {

            _gate.AdoptDurableRecoveryOwner(
                Owner,
                scope: null,
                cleanupOnlyHistoricalCampaign: false);

        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {

            return CovenantRecoveryAuthorityBootstrapper.Refusal();

        }

        return Result.Success();

    }

}

/// <summary>The production recovery-authority bootstrapper, over this process's own runtime holder.</summary>
internal sealed class CovenantRecoveryAuthorityBootstrapper(
    CovenantOperationGate gate,
    CovenantRuntimeGenerationProvider runtime,
    CovenantEnvelopeMasterKeyProvider keys,
    CovenantAvailability availability,
    IHostProcessToolsRuntimePolicy hostToolsPolicy,
    ISecretStore secretStore,
    GrimoireOfflineTransitionEffectHandlerRegistry effects)
    : ICovenantRecoveryAuthorityBootstrapper
{

    private const int MaximumPayloadBytes = 4096;

    private readonly CovenantOperationGate _gate =
        gate ?? throw new ArgumentNullException(nameof(gate));

    private readonly CovenantRuntimeGenerationProvider _runtime =
        runtime ?? throw new ArgumentNullException(nameof(runtime));

    private readonly CovenantEnvelopeMasterKeyProvider _keys =
        keys ?? throw new ArgumentNullException(nameof(keys));

    private readonly CovenantAvailability _availability =
        availability ?? throw new ArgumentNullException(nameof(availability));

    private readonly IHostProcessToolsRuntimePolicy _hostToolsPolicy =
        hostToolsPolicy ?? throw new ArgumentNullException(nameof(hostToolsPolicy));

    private readonly ISecretStore _secretStore =
        secretStore ?? throw new ArgumentNullException(nameof(secretStore));

    private readonly GrimoireOfflineTransitionEffectHandlerRegistry _effects =
        effects ?? throw new ArgumentNullException(nameof(effects));

    public async Task<Result<ICovenantClosedRecoveryHandoff>> LoadAsync(
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

        // Consulted first and not advisory. A process the startup gate has not permitted derives no
        // envelope key and publishes no authority, and here that is a refusal rather than a warning:
        // everything after this point exists to obtain authority a handler then spends.
        if (!_hostToolsPolicy.CovenantPermitted)
        {

            return Result<ICovenantClosedRecoveryHandoff>.Failure(Refusal().Error);

        }

        // The one durable operation the journal names, rather than a scan. A scan is the adopter's
        // shape because it has no journal to ask; this pass does, and adopting anything the journal
        // did not name would be inventing an owner.
        Result<LaunchRow> row = await ReadLaunchRowAsync(
            recoveryConnection,
            evidence.Binding.OperationId,
            cancellationToken).ConfigureAwait(false);

        if (row.IsFailure)
        {

            return Result<ICovenantClosedRecoveryHandoff>.Failure(row.Error);

        }

        Result<GrimoireOfflineTransitionLaunchBinding> launch =
            GrimoireOfflineTransitionLaunch.FromCommittedCheckpoint(
                row.Value.CheckpointVersion,
                row.Value.CheckpointPayload);

        if (launch.IsFailure)
        {

            return Result<ICovenantClosedRecoveryHandoff>.Failure(Refusal().Error);

        }

        Result agreement = Agrees(evidence, row.Value, launch.Value);

        if (agreement.IsFailure)
        {

            return Result<ICovenantClosedRecoveryHandoff>.Failure(agreement.Error);

        }

        Result<CovenantOfflineTransitionSourceState> observed = await CovenantErasureInventorySource
            .ReadOfflineTransitionSourceStateAsync(recoveryConnection, transaction: null, cancellationToken)
            .ConfigureAwait(false);

        if (observed.IsFailure)
        {

            return Result<ICovenantClosedRecoveryHandoff>.Failure(Refusal().Error);

        }

        // The load-bearing check, and the reason these facts are verified rather than merely read. A
        // generation that is neither the source this transition was planned against nor the target it
        // preselected is a catalog this journal never described, and publishing its authority would
        // let the handler resume against a database it has no binding to.
        GrimoireOfflineTransitionObservedState state = GrimoireOfflineTransitionLaunch.Classify(
            launch.Value,
            observed.Value.DatasetGeneration,
            new GrimoireOfflineTransitionEpochTuple(
                observed.Value.AcceleratorEpoch,
                observed.Value.KeyReclamationEpoch,
                observed.Value.EnvelopeKeyEpoch));

        if (state is GrimoireOfflineTransitionObservedState.Ambiguous)
        {

            return Result<ICovenantClosedRecoveryHandoff>.Failure(Refusal().Error);

        }

        return Result<ICovenantClosedRecoveryHandoff>.Success(new CovenantClosedRecoveryHandoff(
            _gate,
            _runtime,
            _keys,
            _availability,
            _hostToolsPolicy,
            _secretStore,
            guardedDirectory,
            evidence,
            launch.Value.OperationId,
            launch.Value.OperationKind,
            new CovenantExclusiveRecoveryOwner(
                launch.Value.OperationId,
                launch.Value.Operation,
                launch.Value.EffectDigest),
            state));

    }

    /// <summary>
    /// Whether the row and the journal describe the same launch of the same transition.
    /// </summary>
    /// <remarks>
    /// Six comparisons over two durable records that were written at different times by different
    /// writers. The digest carries the whole launch, so it is the one that would catch a transposed
    /// tuple; the rest are named separately because each of them is a distinct way for an installation
    /// to hold two records about two different runs.
    ///
    /// <para>The revision is a floor rather than an equality. Recovery adopts the operation's lease
    /// before it resumes a transition and that adoption is a revision of its own, so demanding
    /// equality would lock the journal out of the row it exists to terminalize.</para>
    /// </remarks>
    private Result Agrees(
        GrimoireOfflineTransitionRecoveryEvidence evidence,
        LaunchRow row,
        GrimoireOfflineTransitionLaunchBinding launch)
    {

        if (launch.OperationId != evidence.Binding.OperationId
            || launch.Kind != evidence.Binding.Kind
            || launch.EffectDigest != evidence.Binding.EffectDigest
            || launch.Digest != evidence.Binding.DatabaseOperationLaunchBindingDigest
            || !string.Equals(launch.OperationKind, row.Kind, StringComparison.Ordinal)
            || launch.RecoveryPolicy != row.RecoveryPolicy
            || row.Revision < launch.StartingRevision)
        {

            return Refusal();

        }

        Result<IGrimoireOfflineTransitionEffectHandler> effect = _effects.Resolve(
            evidence.Binding.Kind,
            evidence.Binding.PayloadVersion);

        return effect.IsFailure || effect.Value.Operation != launch.Operation
            ? Refusal()
            : Result.Success();

    }

    private static async Task<Result<LaunchRow>> ReadLaunchRowAsync(
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
                    "Kind",
                    "State",
                    "RecoveryPolicy",
                    "CheckpointVersion",
                    "CheckpointReference",
                    "Revision",
                    CASE
                        WHEN typeof("CheckpointPayload") = 'blob'
                         AND length("CheckpointPayload") BETWEEN 1 AND @maximumPayload
                        THEN "CheckpointPayload"
                        ELSE NULL
                    END
                FROM "LongRunningOperations"
                WHERE "Id" = @id;
                """;

            Add(command, "@id", operationId.ToString("N"));

            Add(command, "@maximumPayload", MaximumPayloadBytes);

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                return Result<LaunchRow>.Failure(Refusal().Error);

            }

            if (reader.GetValue(0) is not string kind
                || reader.GetValue(1) is not long rawState
                || rawState is < int.MinValue or > int.MaxValue
                || !Enum.IsDefined((LongRunningOperationState)(int)rawState)
                || reader.GetValue(2) is not long rawPolicy
                || rawPolicy is < int.MinValue or > int.MaxValue
                || !Enum.IsDefined((LongRunningOperationRecoveryPolicy)(int)rawPolicy)
                || reader.GetValue(3) is not long rawVersion
                || rawVersion is < int.MinValue or > int.MaxValue
                || reader.GetValue(4) is not string reference
                || reader.GetValue(5) is not long revision
                || reader.GetValue(6) is not byte[] payload)
            {

                return Result<LaunchRow>.Failure(Refusal().Error);

            }

            LongRunningOperationState state = (LongRunningOperationState)(int)rawState;

            // A terminal row is a transition whose durable verdict is already written. Resuming one
            // would repeat effects against a database whose own ledger says the run is over.
            if (state is LongRunningOperationState.Completed
                or LongRunningOperationState.Failed
                or LongRunningOperationState.Abandoned)
            {

                return Result<LaunchRow>.Failure(Refusal().Error);

            }

            if (!string.Equals(
                    reference,
                    CovenantResetCheckpointInitiator.CheckpointReference(kind, operationId),
                    StringComparison.Ordinal))
            {

                return Result<LaunchRow>.Failure(Refusal().Error);

            }

            return new LaunchRow(
                kind,
                state,
                (LongRunningOperationRecoveryPolicy)(int)rawPolicy,
                (int)rawVersion,
                revision,
                payload);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception)
        {

            return Result<LaunchRow>.Failure(Refusal().Error);

        }

    }

    private static void Add(SqliteCommand command, string name, object value)
    {

        SqliteParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        _ = command.Parameters.Add(parameter);

    }

    /// <summary>
    /// The one refusal this whole path makes, which never names which comparison failed.
    /// </summary>
    /// <remarks>
    /// Every one of them has the same remedy and the same operator surface, and which of a dozen
    /// disagreements an installation is in is exactly the detail the parent design keeps out of
    /// operator-visible text.
    /// </remarks>
    internal static Result Refusal() =>
        new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The persisted Covenant authority does not agree with the authenticated offline-transition journal.");

    private sealed record LaunchRow(
        string Kind,
        LongRunningOperationState State,
        LongRunningOperationRecoveryPolicy RecoveryPolicy,
        int CheckpointVersion,
        long Revision,
        byte[] CheckpointPayload);

}
