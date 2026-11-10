using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Only a qualified host adapter may inspect an already bound effect; this never authorizes a launch.</summary>
internal interface IGuildQuestRecoveryObserver
{
    bool CanObserve(GuildQuestRecoveryTarget target);

    /// <summary>
    /// Synchronously admit and hand off a query/cancel of the exact bound effect before returning its task.
    /// The task may observe that attempt, but must never launch work or apply a domain result.
    /// </summary>
    Task<Result> ObserveAsync(GuildQuestRecoveryTarget target, CancellationToken cancellationToken);
}

internal sealed record GuildQuestRecoveryTarget(Guid QuestId, Guid OperationId, Guid RunId,
    Guid RootQuestId, Guid InstallationGeneration, Guid AuthorityEpoch, Guid DispatchId, long DispatchRevision,
    GuildDispatchEvidence Evidence, Guid? LocalChildId, string? RemoteTaskId, string? RemoteContextId,
    bool CancellationRequested, string LeaseOwner, long OperationRevision);

/// <summary>Read-only crash reconciliation. The shared reconciler owns the resulting ledger CAS.</summary>
internal sealed class GuildQuestRecoveryHandler(IGrimoireOrdinaryConnectionFactory connections,
    IGrimoireConnectionAdmissionGate gate, TimeProvider time, IEnumerable<IGuildQuestRecoveryObserver> observers)
    : ILongRunningOperationRecoveryHandler
{
    private readonly IGuildQuestRecoveryObserver[] _observers = observers.ToArray();

    public string Kind => LongRunningOperationKinds.GuildQuest;

    public int SupportedCheckpointVersion => GuildQuestCheckpoint.Version;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
    {
        Result<GuildQuestCheckpointPayload> checkpoint = GuildQuestCheckpoint.Decode(operation.CheckpointVersion, operation.CheckpointPayload);

        if (checkpoint.IsFailure)
            return LongRunningOperationRecoveryResult.RequiresAttention(checkpoint.Error.Code);

        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.LongRunningOperationRecovery, out IGrimoireWorkLease? work))
            return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.MaintenanceDeferred);

        await using IGrimoireWorkLease held = work!;

        if (!held.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect))
            return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.MaintenanceDeferred);

        await using IGrimoireExternalEffectGroup heldEffect = effect!;

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.MaintenanceRevocation);

        try
        {
            string? afterDispatch = null;

            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();

                if (held.MaintenanceRevocationPending || held.Generation != gate.CurrentGeneration)
                    return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.MaintenanceDeferred);

                Result<RecoverySnapshot> read = await ReadAsync(operation, checkpoint.Value.QuestId, linked.Token, afterDispatch).ConfigureAwait(false);

                if (read.IsFailure)
                    return Uncertain();

                RecoverySnapshot snapshot = read.Value;

                if (Terminal(snapshot) is { } terminal)
                    return terminal;

                if (!snapshot.Evidence.CancellationRequested && snapshot.Evidence.ResultReceipt is null && snapshot.HasLegacyTool)
                    return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.LegacyToolName);

                if (snapshot.Evidence.ResultReceipt is null
                    && operation.State is LongRunningOperationState.Running or LongRunningOperationState.Waiting or LongRunningOperationState.ReconciliationRequired
                    && !snapshot.Evidence.CancellationRequested && !snapshot.HasPossiblyDispatched
                    && snapshot.Evidence.LastNonterminalState == QuestState.InputRequired
                    && snapshot.Evidence.WaitReason == GuildWaitReason.Input
                    && snapshot.Evidence.AwaitingInput is { QuestionId: var question, ContinuationId: var continuation }
                    && question != Guid.Empty && continuation != Guid.Empty)
                    return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.AwaitingInput);

                if (snapshot.Evidence.ResultReceipt is not null
                    || operation.State is LongRunningOperationState.Completed or LongRunningOperationState.Failed or LongRunningOperationState.Abandoned
                    || operation.State == LongRunningOperationState.Waiting && snapshot.Evidence.WaitReason == GuildWaitReason.Input)
                    return Uncertain();

                if (snapshot.Target is not { } target)
                    return Uncertain();

                afterDispatch = Id(target.DispatchId);

                if (target.Evidence is not (GuildDispatchEvidence.PossiblyDispatched or GuildDispatchEvidence.Confirmed)
                    || target.LocalChildId.HasValue == !string.IsNullOrWhiteSpace(target.RemoteTaskId))
                    continue;

                IGuildQuestRecoveryObserver[] qualified;

                try
                {
                    qualified = _observers.Where(observer => observer.CanObserve(target)).ToArray();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return Uncertain();
                }

                if (qualified.Length != 1)
                    continue;

                if (!await ObserveAsync(operation, snapshot, qualified[0], held, linked.Token).ConfigureAwait(false))
                    return Uncertain();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && held.MaintenanceRevocation.IsCancellationRequested)
        {
            return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.MaintenanceDeferred);
        }
        catch (Exception exception) when (exception is SqliteException or FormatException or InvalidDataException)
        {
            return Uncertain();
        }
    }

    private async Task<Result<RecoverySnapshot>> ReadAsync(LongRunningOperation expected, Guid questId, CancellationToken ct, string? dispatchId = null, bool exactDispatch = false)
    {
        Result<IGrimoireOrdinaryConnectionLease> acquired = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct).ConfigureAwait(false);

        if (acquired.IsFailure)
            return acquired.Error;

        await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

        await using SqliteTransaction transaction = lease.Connection.BeginTransaction(deferred: true);

        Result available = await GuildStorageAvailability.CheckAsync(lease.Connection, transaction, ct).ConfigureAwait(false);

        if (available.IsFailure)
            return available.Error;

        Result bounded = await GuildQuestStore.RequireBoundedReadWithinAsync(lease.Connection, transaction, questId, ct, expected.Id).ConfigureAwait(false);

        if (bounded.IsFailure)
            return bounded.Error;

        LongRunningOperation? current = await LongRunningOperationStore.ReadWithinAsync(lease.Connection, transaction, expected.Id, ct).ConfigureAwait(false);

        if (current is null || !SameOperation(expected, current))
            return Refused();

        bool terminal = current.State is LongRunningOperationState.Completed or LongRunningOperationState.Failed or LongRunningOperationState.Abandoned;

        if (!terminal && (string.IsNullOrWhiteSpace(current.LeaseOwner) || current.LeaseExpiresAt is not { } expires || expires <= time.GetUtcNow()))
            return Refused();

        await using SqliteCommand command = lease.Connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT c.OperationId,c.RunId,c.RootQuestId,c.InstallationGeneration,c.Revision,c.LastNonterminalStateCode,
                c.WaitReasonCode,c.QuestionId,c.ContinuationId,c.CancellationRequested,c.SessionId,
                a.Epoch,r.ResultId,r.Digest,r.OutcomeCode,r.ReasonCode,r.NoDispatchProven,r.WorkStoppedProven,
                b.ResultId,b.Digest,EXISTS(SELECT 1 FROM guild_dispatches uncertain WHERE uncertain.QuestId=c.Id AND uncertain.EvidenceCode=2)
            FROM guild_quests c
            LEFT JOIN guild_signet_authority_state a ON a.StateKey=1
            LEFT JOIN guild_result_receipts r ON r.QuestId=c.Id
            LEFT JOIN guild_result_outbox b ON b.QuestId=c.Id
            WHERE c.Id=$id
            """;

        command.Parameters.AddWithValue("$id", Id(questId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || Guid.Parse(reader.GetString(0)) != current.Id
            || Guid.Parse(reader.GetString(1)) != current.RunId || NullableGuid(reader, 10) != current.SessionId || reader.IsDBNull(11))
            return Refused();

        GuildResultReceipt? receipt = reader.IsDBNull(12) ? null : new(questId, Guid.Parse(reader.GetString(12)),
            Convert.ToHexString((byte[])reader.GetValue(13)), null, (QuestState)reader.GetInt32(14),
            reader.IsDBNull(15) ? null : reader.GetString(15));

        bool outboxMatches = receipt is not null && !reader.IsDBNull(18) && Guid.Parse(reader.GetString(18)) == receipt.ResultId
            && ((byte[])reader.GetValue(19)).AsSpan().SequenceEqual((byte[])reader.GetValue(13));

        GuildQuestInputEvidence? input = NullableGuid(reader, 7) is { } question && NullableGuid(reader, 8) is { } continuation
            ? new(question, continuation) : null;

        GuildQuestProjectionEvidence evidence = new(questId, current.Id, (QuestState)reader.GetInt32(5),
            reader.IsDBNull(6) ? null : (GuildWaitReason)reader.GetInt32(6), input, receipt,
            receipt is not null && reader.GetBoolean(16), receipt is not null && reader.GetBoolean(17),
            current.State == LongRunningOperationState.Abandoned && receipt?.Outcome == QuestState.Failed, reader.GetBoolean(9));

        Guid root = Guid.Parse(reader.GetString(2));

        Guid installationGeneration = Guid.Parse(reader.GetString(3));

        Guid epoch = Guid.Parse(reader.GetString(11));

        long questRevision = reader.GetInt64(4);

        bool possiblyDispatched = reader.GetBoolean(20);

        await reader.DisposeAsync().ConfigureAwait(false);

        if (evidence.CancellationRequested || current.TerminalErrorCode == LongRunningOperationRecoveryOutcomes.Cancelled)
        {
            evidence = evidence with
            {
                CancellationConverged = await GuildQuestStore.ConvergenceSettledWithinAsync(
                    lease.Connection, transaction, questId, ct).ConfigureAwait(false)
            };
        }

        bool hasLegacyTool = false;

        if (!evidence.CancellationRequested && evidence.ResultReceipt is null)
        {
            var diagnosed = await GuildQuestStore.ReadWaitToolDiagnosticsWithinAsync(lease.Connection, transaction,
                questId, input?.ContinuationId, ct).ConfigureAwait(false);

            if (diagnosed.IsFailure) return diagnosed.Error;

            hasLegacyTool = diagnosed.Value.Length > 0;
        }

        RecoverySnapshot snapshot = new(current, evidence, root, installationGeneration,
            epoch, questRevision, outboxMatches, null, possiblyDispatched, hasLegacyTool);

        await using SqliteCommand dispatch = lease.Connection.CreateCommand();

        dispatch.Transaction = transaction;

        dispatch.CommandText = "SELECT DispatchId,Revision,EvidenceCode,LocalChildId,RemoteTaskId,RemoteContextId FROM guild_dispatches "
            + "WHERE QuestId=$id" + (dispatchId is null ? "" : exactDispatch ? " AND DispatchId=$dispatch" : " AND DispatchId>$dispatch")
            + " ORDER BY DispatchId LIMIT 1";

        dispatch.Parameters.AddWithValue("$id", Id(questId));

        if (dispatchId is not null)
            dispatch.Parameters.AddWithValue("$dispatch", dispatchId);

        await using SqliteDataReader target = await dispatch.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await target.ReadAsync(ct).ConfigureAwait(false))
            return snapshot;

        GuildQuestRecoveryTarget observed = new(questId, current.Id, current.RunId!.Value, snapshot.RootId,
            snapshot.InstallationGeneration, snapshot.AuthorityEpoch, Guid.Parse(target.GetString(0)), target.GetInt64(1),
            (GuildDispatchEvidence)target.GetInt32(2), NullableGuid(target, 3), target.IsDBNull(4) ? null : target.GetString(4),
            target.IsDBNull(5) ? null : target.GetString(5), evidence.CancellationRequested || current.State == LongRunningOperationState.Cancelling,
            current.LeaseOwner ?? "", current.Revision);

        return snapshot with { Target = observed };
    }

    private async Task<bool> ObserveAsync(LongRunningOperation operation, RecoverySnapshot snapshot,
        IGuildQuestRecoveryObserver observer, IGrimoireWorkLease work, CancellationToken ct)
    {
        try
        {
            var acquired = await GuildSignetRootGate.AcquireAsync(gate, snapshot.RootId, ct).ConfigureAwait(false);

            if (acquired.IsFailure)
                return false;

            using GuildSignetRootGate.RootLease root = acquired.Value;

            if (!root.Enter() || work.MaintenanceRevocationPending || work.Generation != gate.CurrentGeneration)
                return false;

            Result<RecoverySnapshot> current = await ReadAsync(operation, snapshot.Evidence.QuestId, ct,
                Id(snapshot.Target!.DispatchId), exactDispatch: true).ConfigureAwait(false);

            if (current.IsFailure || !SameFence(snapshot, current.Value))
                return false;

            ct.ThrowIfCancellationRequested();

            if (work.MaintenanceRevocationPending || work.Generation != gate.CurrentGeneration)
                return false;

            Result observed = await ObserveAndReleaseRootAsync(root, observer, snapshot.Target!, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            if (observed.IsFailure)
                return false;

            if (work.MaintenanceRevocationPending || work.Generation != gate.CurrentGeneration)
                return false;

            Result<RecoverySnapshot> after = await ReadAsync(operation, snapshot.Evidence.QuestId, ct,
                Id(snapshot.Target!.DispatchId), exactDispatch: true).ConfigureAwait(false);

            return after.IsSuccess && SameFence(snapshot, after.Value);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<Result> ObserveAndReleaseRootAsync(GuildSignetRootGate.RootLease root,
        IGuildQuestRecoveryObserver observer, GuildQuestRecoveryTarget target, CancellationToken ct)
    {
        Task<Result> observation = observer.ObserveAsync(target, ct);

        return await ReleaseRootAndObserveAsync(root, observation).ConfigureAwait(false);
    }

    private static async Task<Result> ReleaseRootAndObserveAsync(GuildSignetRootGate.RootLease root, Task<Result> observation)
    {
        Result observed;

        try
        {
            root.Dispose();
        }
        finally
        {
            // Even a failed ordering-scope release cannot detach the admitted observation from
            // the caller's retained work/effect lifetime.
            observed = await observation.ConfigureAwait(false);
        }

        return observed;
    }

    private static bool SameFence(RecoverySnapshot before, RecoverySnapshot after) =>
        before.RootId == after.RootId && before.InstallationGeneration == after.InstallationGeneration
        && before.AuthorityEpoch == after.AuthorityEpoch && before.AuthorityEpoch != Guid.Empty
        && before.QuestRevision == after.QuestRevision && before.Evidence == after.Evidence
        && before.Target == after.Target && before.OutboxMatches == after.OutboxMatches
        && before.HasPossiblyDispatched == after.HasPossiblyDispatched && before.HasLegacyTool == after.HasLegacyTool;

    private static LongRunningOperationRecoveryResult? Terminal(RecoverySnapshot snapshot)
    {
        GuildQuestProjectedState projection = GuildQuestProjection.Project(snapshot.Operation, snapshot.Evidence);

        if (snapshot.OutboxMatches && !projection.ReconciliationRequired
            && projection.State is QuestState.Completed or QuestState.Failed or QuestState.Canceled or QuestState.Rejected)
            return new(snapshot.Operation.State, snapshot.Operation.TerminalErrorCode);

        return null;
    }

    private static bool SameOperation(LongRunningOperation expected, LongRunningOperation current) =>
        current.Id == expected.Id && current.Kind == LongRunningOperationKinds.GuildQuest
        && current.Kind == expected.Kind && current.RecoveryPolicy == LongRunningOperationRecoveryPolicy.ReconcileAndComplete
        && current.State == expected.State && current.Revision == expected.Revision && current.RunId == expected.RunId
        && current.SessionId == expected.SessionId && current.TerminalErrorCode == expected.TerminalErrorCode
        && current.LeaseOwner == expected.LeaseOwner && current.LeaseExpiresAt == expected.LeaseExpiresAt
        && current.CheckpointReference == expected.CheckpointReference
        && current.CheckpointVersion == expected.CheckpointVersion && current.CheckpointPayload is { } bytes
        && expected.CheckpointPayload is { } expectedBytes && bytes.AsSpan().SequenceEqual(expectedBytes);

    private static Guid? NullableGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static string Id(Guid value) => value.ToString("D").ToUpperInvariant();

    private static Error Refused() => new(ErrorCodes.Guild.ReconciliationRequired, "The Quest recovery linkage or current lease cannot be proven.");

    private static LongRunningOperationRecoveryResult Uncertain() => LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.ReconciliationRequired);

    private sealed record RecoverySnapshot(LongRunningOperation Operation, GuildQuestProjectionEvidence Evidence,
        Guid RootId, Guid InstallationGeneration, Guid AuthorityEpoch, long QuestRevision, bool OutboxMatches, GuildQuestRecoveryTarget? Target, bool HasPossiblyDispatched, bool HasLegacyTool);
}
