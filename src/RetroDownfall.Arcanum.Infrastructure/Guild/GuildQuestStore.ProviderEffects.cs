using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    private readonly ConditionalWeakTable<GuildBudgetEffectRequest, GuildLocalInferenceExecution> _providerReceipts = new();

    internal Task<Result<T>> ExecuteProviderEffectAsync<T>(GuildLocalInferenceExecution execution,
        BudgetReservationService daily, GuildBudgetEffectRequest effect,
        Func<CancellationToken, Task<Result<T>>> send, CancellationToken ct, GuildLocalExecutionInput? expectedInput = null)
    {
        if (!ProviderIdentityMatches(execution, effect) || daily is null || send is null || allocations is not GuildBudgetLedger ledger)
            return Task.FromResult(Result<T>.Failure(InvalidReceipt));

        Result ready = RequireLocalExecutionReady();

        if (ready.IsFailure) return Task.FromResult(Result<T>.Failure(ready.Error));

        return authority.AdmitQuestEffectAsync<T>(execution.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                if (policy is GuildLocalHostPolicy { IsExecutionEnabled: false } || RequireLocalExecutionReady().IsFailure) return Unavailable;

                GuildQuestDriver driver = execution.Driver;

                var admitted = await RequireDriverWithinAsync(connection, transaction, signets,
                    driver, true, false, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                if (admitted.Value.Row.Quest.State == QuestState.InputRequired) return ClosedDispatch;

                var found = await ReadDispatchWithinAsync(connection, transaction, effect.DispatchId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                if (found.Value is not { } dispatch || !SameProviderDispatch(dispatch, execution.Dispatch)
                    || dispatch.Evidence == GuildDispatchEvidence.ResolvedNoDispatch) return Conflict;

                if (expectedInput is not null)
                {
                    var input = await MaterializeProviderInputWithinAsync(connection, transaction, admitted.Value.Row,
                        expectedInput, dispatch, token).ConfigureAwait(false);

                    if (input.IsFailure) return input.Error;
                }

                Result<GuildBudgetEffect> prior = await ledger.ReadEffectWithinAsync(connection, transaction, effect.EffectId, token).ConfigureAwait(false);

                if (prior.IsSuccess)
                    return prior.Value.Request with { ReservationId = effect.ReservationId } == effect
                        ? Result<bool>.Success(false) : Conflict;

                if (prior.Error.Code != ErrorCodes.Guild.NotFound) return prior.Error;

                // No new identity may turn uncertain paid work into an automatic retry.
                await using (SqliteCommand uncertain = Command(connection, transaction,
                    "SELECT EXISTS(SELECT 1 FROM guild_budget_effects WHERE QuestId=$quest AND StateCode=1)"))
                {
                    Add(uncertain, "$quest", Id(effect.QuestId));

                    if (Equals(1L, await uncertain.ExecuteScalarAsync(token).ConfigureAwait(false))) return UncertainDispatch;
                }

                Result run = await EnsureProviderRunWithinAsync(connection, transaction, admitted.Value.Row, token).ConfigureAwait(false);

                if (run.IsFailure) return run.Error;

                Result<GuildBudgetEffect> prepared = await GuildBudgetPhysicalAccounting.PrepareLocalWithinAsync(connection,
                    transaction, ledger, daily, effect, time.GetUtcNow().AddHours(1), token).ConfigureAwait(false);

                if (prepared.IsFailure) return prepared.Error;

                if (dispatch.Evidence == GuildDispatchEvidence.Prepared)
                {
                    var marked = await MarkPossiblyDispatchedWithinAsync(connection, transaction, signets, driver,
                        dispatch.DispatchId, dispatch.Revision, token).ConfigureAwait(false);

                    return marked.IsFailure ? marked.Error : marked.Value.MayDispatch;
                }

                var exposed = await ledger.ExposeEffectWithinAsync(connection, transaction, effect.EffectId,
                    prepared.Value.Revision, token).ConfigureAwait(false);

                return exposed.IsFailure ? exposed.Error : exposed.Value.MayDispatch;
            }, token =>
            {
                if (policy is GuildLocalHostPolicy { IsExecutionEnabled: false } || RequireLocalExecutionReady().IsFailure)
                    return Task.FromResult(Result<T>.Failure(Unavailable));

                _providerReceipts.Add(effect, execution);

                return send(token);
            }, ct);
    }

    internal Task<Result<GuildBudgetEffect>> SettleProviderEffectAsync(GuildLocalInferenceExecution execution,
        BudgetReservationService daily, GuildBudgetEffectRequest effect, BillableOperationRecord bill, CancellationToken ct)
    {
        if (!ProviderIdentityMatches(execution, effect) || !_providerReceipts.TryGetValue(effect, out var owner)
            || !ReferenceEquals(owner, execution) || daily is null || allocations is not GuildBudgetLedger ledger
            || bill is null || bill.RunId != effect.AccountingAssociationId || bill.InputTokens < 0 || bill.OutputTokens < 0
            || bill.CachedTokens < 0 || bill.CachedTokens > bill.InputTokens || bill.ReasoningTokens < 0
            || bill.ReasoningTokens > bill.OutputTokens || bill.ActualCostUsd < 0 || !Enum.IsDefined(bill.Status))
            return Task.FromResult(Result<GuildBudgetEffect>.Failure(InvalidReceipt));

        // Evidence is allowed after revocation. It grants no new send and must not be discarded because authority ended.
        return authority.InQuestTransactionAsync<GuildBudgetEffect>(execution.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildBudgetEffect> found = await ledger.ReadEffectWithinAsync(connection, transaction, effect.EffectId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                if (found.Value.Request with { ReservationId = effect.ReservationId } != effect
                    || found.Value.State is GuildBudgetEffectState.Prepared or GuildBudgetEffectState.ProvenNoDispatch) return Conflict;

                bool replay;

                await using (SqliteCommand prior = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM BillableOperations WHERE Id=$id)"))
                {
                    Add(prior, "$id", effect.SourceId.ToString("N"));

                    replay = Equals(1L, await prior.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                if (replay)
                {
                    if (!await ProviderBillMatchesWithinAsync(connection, transaction, effect.SourceId, bill, token).ConfigureAwait(false)) return Conflict;
                }
                else
                {
                    _ = await TurnRunWriter.RecordBillableOperationWithinAsync(connection, transaction, effect.SourceId, bill, token).ConfigureAwait(false);
                }

                Result<GuildBudgetEffect> applied = await GuildBudgetPhysicalAccounting.ApplyLocalBillWithinAsync(connection,
                    transaction, ledger, effect.EffectId, effect.SourceId, token, daily).ConfigureAwait(false);

                if (applied.IsFailure || replay) return applied;

                Result<GuildQuestStored> quest = await ReadWithinAsync(connection, transaction, effect.QuestId, token).ConfigureAwait(false);

                if (quest.IsFailure) return quest.Error;

                if (quest.Value.Quest.Revision == long.MaxValue) return ExhaustedReceipt;

                await using SqliteCommand update = Command(connection, transaction,
                    "UPDATE guild_quests SET Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$quest AND Revision=$revision");

                Add(update, "$now", UtcInstantText.Format(time.GetUtcNow()));

                Add(update, "$quest", Id(effect.QuestId));

                Add(update, "$revision", quest.Value.Quest.Revision);

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
                    execution.Driver.Execution.RootQuestId, effect.QuestId, GuildEventKind.SpendChanged,
                    effect.QuestId, quest.Value.Quest.Revision + 1, null, time.GetUtcNow(), token).ConfigureAwait(false);

                return projected.IsFailure ? projected.Error : applied;
            }, ct);
    }

    private bool ProviderIdentityMatches(GuildLocalInferenceExecution execution, GuildBudgetEffectRequest effect) =>
        execution is not null && ReferenceEquals(execution.Store, this) && DriverShape(execution.Driver)
        && execution.Dispatch is not null && effect is not null && effect.Kind == GuildBudgetEffectKind.LocalProvider
        && effect.QuestId == execution.Driver.Execution.QuestId && effect.DispatchId == execution.Dispatch.DispatchId
        && effect.AccountingAssociationId == execution.Driver.Execution.RunId
        && execution.Dispatch.AccountingAssociationId == effect.AccountingAssociationId;

    private static bool SameProviderDispatch(GuildQuestDispatchRecord current, GuildQuestDispatchRecord accepted) =>
        current.DispatchId == accepted.DispatchId && current.QuestId == accepted.QuestId
        && current.InvocationDigest == accepted.InvocationDigest && current.AccountingAssociationId == accepted.AccountingAssociationId
        && current.IsContinuation == accepted.IsContinuation;

    private async Task<Result> EnsureProviderRunWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored quest, CancellationToken ct)
    {
        if (quest.Quest.SessionId is not Guid sessionId) return InvalidReceipt;

        await using (SqliteCommand existing = Command(connection, transaction,
            "SELECT RequestId,SessionId,Surface,Purpose,Status FROM InferenceRuns WHERE Id=$run"))
        {
            Add(existing, "$run", quest.Quest.RunId.ToString("N"));

            await using SqliteDataReader reader = await existing.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                return reader.GetString(0) == quest.Quest.Id.ToString("N") && reader.GetString(1) == sessionId.ToString("N")
                    && reader.GetString(2) == "guild" && reader.GetString(3) == "quest" && reader.GetInt64(4) == 0
                    ? Result.Success() : Result.Failure(Conflict);
        }

        await using SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO InferenceRuns(Id,RequestId,SessionId,Surface,Purpose,StartedAt,Status)
            VALUES($run,$request,$session,'guild','quest',$now,0)
            """);

        Add(insert, "$run", quest.Quest.RunId.ToString("N"));

        Add(insert, "$request", quest.Quest.Id.ToString("N"));

        Add(insert, "$session", sessionId.ToString("N"));

        Add(insert, "$now", UtcInstantText.Format(time.GetUtcNow()));

        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }

    private static async Task<bool> ProviderBillMatchesWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid sourceId, BillableOperationRecord bill, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM BillableOperations WHERE Id=$id AND RunId=$run AND OperationType=$type
                AND Provider=$provider AND Model=$model AND Purpose=$purpose AND StartedAt=$started AND CompletedAt=$completed
                AND InputTokens=$input AND OutputTokens=$output AND ReasoningTokens=$reasoning AND CachedTokens=$cached
                AND PricingSnapshotJson=$pricing AND ActualCostUsd=$cost AND Status=$status AND ProviderRequestId IS $request)
            """);

        Add(command, "$id", sourceId.ToString("N"));

        Add(command, "$run", bill.RunId.ToString("N"));

        Add(command, "$type", (int)bill.OperationType);

        Add(command, "$provider", bill.Provider);

        Add(command, "$model", bill.Model);

        Add(command, "$purpose", bill.Purpose);

        Add(command, "$started", UtcInstantText.Format(bill.StartedAt));

        Add(command, "$completed", UtcInstantText.Format(bill.CompletedAt));

        Add(command, "$input", bill.InputTokens);

        Add(command, "$output", bill.OutputTokens);

        Add(command, "$reasoning", bill.ReasoningTokens);

        Add(command, "$cached", bill.CachedTokens);

        Add(command, "$pricing", bill.PricingSnapshotJson);

        Add(command, "$cost", ExactUsdText.Format(bill.ActualCostUsd));

        Add(command, "$status", (int)bill.Status);

        Add(command, "$request", bill.ProviderRequestId);

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }
}
