using System.Security.Cryptography;
using System.Text;
using A2A;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildRemotePortState(GuildQuestStored Quest, LongRunningOperation Operation,
    GuildRemoteExecutionInput? Input, GuildQuestDispatchRecord? Dispatch, string? StopReasonCode = null);

internal sealed record GuildRemoteResultEvidence(Guid DispatchId, A2AGuildTaskObservation Observation);

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildRemotePortState>> ReadRemotePortAsync(GuildQuestDriver driver, Guid dispatchId, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildRemotePortState>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                var dispatch = await ReadDispatchWithinAsync(connection, transaction, dispatchId, token).ConfigureAwait(false);

                if (dispatch.IsFailure) return dispatch.Error;

                if (dispatch.Value is { } bound && (bound.QuestId != driver.Execution.QuestId || bound.LocalChildId is not null)) return Conflict;

                var stop = await ReadRemoteStopReasonWithinAsync(connection, transaction, driver.Execution.QuestId, token).ConfigureAwait(false);

                if (stop.IsFailure) return stop.Error;

                if (admitted.Value.Row.Quest.ResultReceiptId is not null)
                    return new GuildRemotePortState(admitted.Value.Row, admitted.Value.Operation, null, dispatch.Value, stop.Value);

                if (driver.Caller?.Kind == GuildCallerKind.HostReceipt)
                {
                    var target = await GuildRemoteExecutionStore.ReadRecoveryWithinAsync(connection, transaction,
                        driver.Execution.QuestId, token).ConfigureAwait(false);

                    return target.IsFailure ? target.Error : new GuildRemotePortState(admitted.Value.Row, admitted.Value.Operation,
                        new(1, target.Value.DiscoveryEndpoint, target.Value.Transport, A2ADispatchMode.Blocking, ["text/plain"], null, []), dispatch.Value, stop.Value);
                }

                var input = await GuildRemoteExecutionStore.ReadInputWithinAsync(connection, transaction, driver.Execution.QuestId, token).ConfigureAwait(false);

                return input.IsFailure ? input.Error : new GuildRemotePortState(admitted.Value.Row, admitted.Value.Operation, input.Value, dispatch.Value, stop.Value);
            }, ct);

    internal Task<Result<GuildRemoteExecutionInput>> ReadRemoteInvocationAsync(GuildQuestDriver driver, Guid dispatchId, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildRemoteExecutionInput>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                return admitted.IsFailure ? admitted.Error
                    : await ReadRemoteInvocationWithinAsync(connection, transaction, driver.Execution.QuestId, dispatchId, token).ConfigureAwait(false);
            }, ct);

    private static async Task<Result<GuildRemoteExecutionInput>> ReadRemoteInvocationWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid questId, Guid dispatchId, CancellationToken ct)
    {
        var input = await GuildRemoteExecutionStore.ReadInputWithinAsync(connection, transaction, questId, ct).ConfigureAwait(false);

        if (input.IsFailure || dispatchId == questId) return input;

        var accepted = await ReadRemoteContinuationOptionsWithinAsync(connection, transaction, questId, dispatchId, ct).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        return accepted.Value is not { } options ? input : input.Value with
        {
            Mode = (A2ADispatchMode)options.Mode,
            AcceptedOutputModes = options.AcceptedOutputModes.IsDefaultOrEmpty ? input.Value.AcceptedOutputModes : options.AcceptedOutputModes,
            SkillId = options.SkillId,
        };
    }

    internal Task<Result<string>> ReadRemoteBodyAsync(GuildQuestDriver driver, Guid dispatchId, CancellationToken ct) =>
        authority.InQuestTransactionAsync<string>(driver.Execution.RootQuestId, async (connection, transaction, signets, token) =>
        {
            var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

            if (admitted.IsFailure) return admitted.Error;

            if (dispatchId == driver.Execution.QuestId)
                return await ReadProviderContentWithinAsync(connection, transaction, admitted.Value.Row.Quest.GoalRef, token).ConfigureAwait(false);

            var continuation = await ReadContinuationWithinAsync(connection, transaction, admitted.Value.Row, dispatchId, token).ConfigureAwait(false);

            return continuation.IsFailure ? continuation.Error
                : await ReadProviderContentWithinAsync(connection, transaction, continuation.Value.ContentRef, token).ConfigureAwait(false);
        }, ct);

    internal Task<Result<bool>> RemotePreflightAsync(GuildQuestDriver driver, bool newWork, CancellationToken ct) =>
        authority.InQuestTransactionAsync<bool>(driver.Execution.RootQuestId, async (connection, transaction, signets, token) =>
        {
            var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, !newWork, token).ConfigureAwait(false);

            if (admitted.IsFailure) return admitted.Error;

            if (newWork && policy is GuildLocalHostPolicy { IsExecutionEnabled: false }) return Unavailable;

            if (newWork && (admitted.Value.Row.Quest.ResolvedGrant.Budget.Usd.Mode != GuildBudgetMode.Unlimited
                || admitted.Value.Row.Quest.ResolvedGrant.Budget.Tokens.Mode != GuildBudgetMode.Unlimited
                || allocations is GuildBudgetLedger { RequiresDailyReservation: true }))
                return new Error(ErrorCodes.Guild.BudgetCapabilityUnavailable, "General A2A cannot enforce the active finite spending authority.");

            return true;
        }, ct);

    internal Task<Result<SendMessageResponse>> SendRemoteEffectAsync(GuildQuestDriver driver, Guid dispatchId,
        Func<CancellationToken, Task<SendMessageResponse>> send, CancellationToken ct) =>
        authority.AdmitQuestEffectAsync<SendMessageResponse>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                if (policy is GuildLocalHostPolicy { IsExecutionEnabled: false }) return Unavailable;

                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                if (allocations is not GuildBudgetLedger budget) return Unavailable;

                if (budget.RequiresDailyReservation || admitted.Value.Row.Quest.ResolvedGrant.Budget.Usd.Mode != GuildBudgetMode.Unlimited
                    || admitted.Value.Row.Quest.ResolvedGrant.Budget.Tokens.Mode != GuildBudgetMode.Unlimited)
                    return new Error(ErrorCodes.Guild.BudgetCapabilityUnavailable, "General A2A cannot enforce the active finite spending authority.");

                var dispatch = await ReadDispatchWithinAsync(connection, transaction, dispatchId, token).ConfigureAwait(false);

                if (dispatch.IsFailure || dispatch.Value is not { } current) return dispatch.IsFailure ? dispatch.Error : Conflict;

                if (current.Evidence != GuildDispatchEvidence.Prepared) return false;

                if (!current.IsContinuation)
                {
                    var prepared = await budget.PrepareEffectWithinAsync(connection, transaction,
                        new(driver.Execution.QuestId, driver.Execution.QuestId, dispatchId, driver.Execution.RunId,
                            GuildBudgetEffectKind.External, driver.Execution.QuestId, null, null, null), token).ConfigureAwait(false);

                    if (prepared.IsFailure) return prepared.Error;

                    await using SqliteCommand insert = Command(connection, transaction, """
                        INSERT INTO guild_remote_effects(EffectId,QuestId,DispatchId) VALUES($quest,$quest,$dispatch)
                        ON CONFLICT(EffectId) DO NOTHING
                        """);

                    Add(insert, "$quest", Id(driver.Execution.QuestId));
                    Add(insert, "$dispatch", Id(dispatchId));

                    _ = await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                var exposed = await MarkPossiblyDispatchedWithinAsync(connection, transaction, signets,
                    driver, dispatchId, current.Revision, token).ConfigureAwait(false);

                if (exposed.IsFailure) return exposed.Error;

                if (current.IsContinuation && exposed.Value.MayDispatch)
                {
                    // This is the existing task's correlation, not acknowledgement of the answer.
                    // A lost answer acknowledgement may be queried, but never sent again.
                    var identity = await ReadRemoteTaskIdentityWithinAsync(connection, transaction, driver.Execution.QuestId, token).ConfigureAwait(false);

                    if (identity.IsFailure) return identity.Error;

                    if (exposed.Value.Dispatch.Revision == long.MaxValue) return ExhaustedReceipt;

                    await using SqliteCommand correlate = Command(connection, transaction, """
                        UPDATE guild_dispatches SET RemoteTaskId=$task,RemoteContextId=$context,Revision=Revision+1,UpdatedAtUtc=$now
                        WHERE DispatchId=$dispatch AND EvidenceCode=2 AND Revision=$revision
                        """);

                    Add(correlate, "$task", identity.Value.TaskId);
                    Add(correlate, "$context", identity.Value.ContextId);
                    Add(correlate, "$dispatch", Id(dispatchId));
                    Add(correlate, "$revision", exposed.Value.Dispatch.Revision);
                    Add(correlate, "$now", UtcInstantText.Format(time.GetUtcNow()));

                    if (await correlate.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                    var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, driver.Execution.RootQuestId,
                        driver.Execution.QuestId, GuildEventKind.QuestChanged, dispatchId, exposed.Value.Dispatch.Revision + 1,
                        null, time.GetUtcNow(), token, GuildPortalEventSource.Dispatch).ConfigureAwait(false);

                    if (projected.IsFailure) return projected.Error;
                }

                return exposed.Value.MayDispatch;
            }, async token => await send(token).ConfigureAwait(false), ct);

    internal Task<Result<T>> AdmitRemoteObservationAsync<T>(GuildQuestDriver driver,
        Func<CancellationToken, Task<T>> action, bool stopping, CancellationToken ct) =>
        authority.AdmitQuestEffectAsync<T>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, stopping, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                if (!stopping && policy is GuildLocalHostPolicy { IsExecutionEnabled: false }) return Unavailable;

                return true;
            }, async token => await action(token).ConfigureAwait(false), ct);

    internal Task<Result<GuildQuestDriver>> RefreshRemoteDriverAsync(GuildQuestDriver driver, bool renew, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildQuestDriver>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                LongRunningOperation operation = admitted.Value.Operation;

                if (operation.LeaseOwner != driver.LeaseOwner || operation.LeaseExpiresAt <= time.GetUtcNow()
                    || operation.State is not (LongRunningOperationState.Running or LongRunningOperationState.Waiting or LongRunningOperationState.Cancelling))
                    return RevisionConflict;

                if (renew)
                {
                    await using SqliteCommand heartbeat = Command(connection, transaction, """
                        UPDATE LongRunningOperations SET HeartbeatAt=$now,LeaseExpiresAt=$expires,Revision=Revision+1
                        WHERE Id=$id AND Revision=$revision AND LeaseOwner=$owner AND Revision<9223372036854775807
                        """);

                    Add(heartbeat, "$id", operation.Id.ToString("N"));
                    Add(heartbeat, "$revision", operation.Revision);
                    Add(heartbeat, "$owner", driver.LeaseOwner);
                    Add(heartbeat, "$now", UtcInstantText.Format(time.GetUtcNow()));
                    Add(heartbeat, "$expires", UtcInstantText.Format(time.GetUtcNow().AddMinutes(2)));

                    if (await heartbeat.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;
                }

                return driver with { OperationRevision = operation.Revision + (renew ? 1 : 0) };
            }, ct);

    internal Task<Result<GuildQuestStored>> ParkRemoteAsync(GuildQuestDriver driver, string reason, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildQuestStored>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                if (admitted.Value.Row.Quest.ResultReceiptId is not null) return admitted.Value.Row;

                var operation = admitted.Value.Operation;

                if (operation.State == LongRunningOperationState.ReconciliationRequired) return admitted.Value.Row;

                if (!await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, operation.Id,
                    operation.Revision, driver.LeaseOwner, LongRunningOperationState.ReconciliationRequired, time.GetUtcNow(), reason, token).ConfigureAwait(false))
                    return RevisionConflict;

                return await ReadWithinAsync(connection, transaction, driver.Execution.QuestId, token).ConfigureAwait(false);
            }, ct);

    private async Task<Result> ApplyRemoteEvidenceWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored row, GuildRemoteResultEvidence evidence, bool complete, CancellationToken ct)
    {
        if (allocations is not GuildBudgetLedger budget) return Unavailable;

        var dispatch = await ReadDispatchWithinAsync(connection, transaction, evidence.DispatchId, ct).ConfigureAwait(false);

        if (dispatch.IsFailure || dispatch.Value is not { } current || current.QuestId != row.Quest.Id || current.LocalChildId is not null)
            return dispatch.IsFailure ? dispatch.Error : Conflict;

        var observed = evidence.Observation;

        if (current.Evidence is not (GuildDispatchEvidence.PossiblyDispatched or GuildDispatchEvidence.Confirmed)
            || current.RemoteTaskId != observed.TaskId || current.RemoteContextId is not null && current.RemoteContextId != observed.ContextId
            || observed.TaskId is null && observed.State != "completed") return UncertainDispatch;

        var found = await budget.ReadEffectWithinAsync(connection, transaction, row.Quest.Id, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        var effect = found.Value;

        decimal usd = observed.Cost.CostUsd ?? effect.KnownUsd;
        long tokens = observed.Cost.TotalTokens ?? effect.KnownTokens;

        if (usd < effect.KnownUsd || tokens < effect.KnownTokens) return UncertainDispatch;

        string facts = $"Guild.Remote:{row.Quest.Id:D}:{evidence.DispatchId:D}:{observed.State}:{ExactUsdText.Format(usd)}:{tokens}:{complete}:{observed.Cost.CostUsd.HasValue}:{observed.Cost.TotalTokens.HasValue}";

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(facts));

        var applied = await budget.ApplyEvidenceWithinAsync(connection, transaction, row.Quest.Id,
            new(new Guid(digest.AsSpan(0, 16)), Convert.ToHexString(digest), usd, tokens,
                !complete || observed.Cost.CostUsd is null, !complete || observed.Cost.TotalTokens is null, complete), ct).ConfigureAwait(false);

        if (applied.IsFailure) return applied.Error;

        if (current.Evidence == GuildDispatchEvidence.PossiblyDispatched)
        {
            await using SqliteCommand confirm = Command(connection, transaction,
                "UPDATE guild_dispatches SET EvidenceCode=3,Revision=Revision+1,UpdatedAtUtc=$now WHERE DispatchId=$id AND Revision=$revision");

            Add(confirm, "$id", Id(current.DispatchId));
            Add(confirm, "$revision", current.Revision);
            Add(confirm, "$now", UtcInstantText.Format(time.GetUtcNow()));

            if (await confirm.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return RevisionConflict;
        }

        if (complete)
        {
            await using SqliteCommand settle = Command(connection, transaction,
                "UPDATE guild_remote_effects SET SettledAtUtc=COALESCE(SettledAtUtc,$now) WHERE EffectId=$id AND QuestId=$quest");

            Add(settle, "$id", Id(row.Quest.Id));
            Add(settle, "$quest", Id(row.Quest.Id));
            Add(settle, "$now", UtcInstantText.Format(time.GetUtcNow()));

            if (await settle.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Unavailable;
        }

        var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, row.Quest.RootQuestId, row.Quest.Id,
            GuildEventKind.SpendChanged, new Guid(digest.AsSpan(0, 16)), applied.Value.Revision, null, time.GetUtcNow(), ct,
            GuildPortalEventSource.Dispatch).ConfigureAwait(false);

        return projected.IsFailure ? projected.Error : Result.Success();
    }
}
