using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
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
        BudgetReservationService? daily, GuildBudgetEffectRequest effect,
        Func<CancellationToken, Task<Result<T>>> send, CancellationToken ct, GuildLocalExecutionInput? expectedInput = null,
        IReadOnlyList<GuildCircleInputBatch>? circleInputs = null, GuildSpendSourceCredential? spendSource = null,
        string? spendPricing = null, IReadOnlyList<GuildWorkspaceReadDisclosure>? workspaceDisclosures = null)
    {
        if (!ProviderIdentityMatches(execution, effect) || send is null || allocations is not GuildBudgetLedger ledger)
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

                Result circle = await ValidateCircleInputWithinAsync(connection, transaction, signets, execution, circleInputs, token).ConfigureAwait(false);

                if (circle.IsFailure) return circle.Error;

                Result workspace = await ValidateWorkspaceToolDisclosuresWithinAsync(connection, transaction, signets,
                    execution, workspaceDisclosures, token).ConfigureAwait(false);

                if (workspace.IsFailure) return workspace.Error;

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

                Result<GuildBudgetEffect> prepared = await ledger.PrepareEffectWithinAsync(connection,
                    transaction, effect, token).ConfigureAwait(false);

                if (prepared.IsFailure) return prepared.Error;

                if (spendSource is not null)
                {
                    Result captured = await spendSource.CaptureWithinAsync(connection, transaction, effect,
                        admitted.Value.Row.Issuance.InstallationGeneration, spendPricing ?? string.Empty, token).ConfigureAwait(false);

                    if (captured.IsFailure) return captured.Error;
                }

                if (dispatch.Evidence == GuildDispatchEvidence.Prepared)
                {
                    var marked = await MarkPossiblyDispatchedWithinAsync(connection, transaction, signets, driver,
                        dispatch.DispatchId, dispatch.Revision, token).ConfigureAwait(false);

                    if (marked.IsFailure) return marked.Error;

                    if (!marked.Value.MayDispatch) return false;
                }
                else
                {
                    var exposed = await ledger.ExposeEffectWithinAsync(connection, transaction, effect.EffectId,
                        prepared.Value.Revision, token).ConfigureAwait(false);

                    if (exposed.IsFailure) return exposed.Error;

                    if (!exposed.Value.MayDispatch) return false;
                }

                Result accepted = await CommitCircleInputWithinAsync(connection, transaction, signets, execution, circleInputs, token).ConfigureAwait(false);

                return accepted.IsFailure ? accepted.Error : true;
            }, token =>
            {
                if (policy is GuildLocalHostPolicy { IsExecutionEnabled: false } || RequireLocalExecutionReady().IsFailure)
                    return Task.FromResult(Result<T>.Failure(Unavailable));

                _providerReceipts.Add(effect, execution);

                return send(token);
            }, ct);
    }

    // This capability never leaves the store: only a successful return from the admitted
    // callback followed by a frozen, validated projection can create it.
    private sealed record ReceivedProviderResponse(GuildQuestStore Store, GuildLocalInferenceExecution Execution,
        GuildBudgetEffectRequest Effect, object Response, BillableOperationRecord Bill);

    internal async Task<Result<T>> ExecuteAndSettleProviderResponseAsync<T>(GuildLocalInferenceExecution execution,
        BudgetReservationService? daily, GuildBudgetEffectRequest effect, Func<CancellationToken, Task<Result<T>>> send,
        Func<T, Result<BillableOperationRecord>> project, CancellationToken ct, GuildLocalExecutionInput? expectedInput = null,
        IReadOnlyList<GuildCircleInputBatch>? circleInputs = null, GuildSpendSourceCredential? spendSource = null,
        string? spendPricing = null, IReadOnlyList<GuildWorkspaceReadDisclosure>? workspaceDisclosures = null) where T : class
    {
        Result<T> received = await ExecuteProviderEffectAsync(execution, daily, effect, send, ct, expectedInput,
            circleInputs, spendSource, spendPricing, workspaceDisclosures).ConfigureAwait(false);

        if (received.IsFailure) return received.Error;

        Result<BillableOperationRecord> projected = project(received.Value);

        if (projected.IsFailure) return projected.Error;

        BillableOperationRecord bill = projected.Value with { };

        if (!ValidProviderBill(effect, bill)) return InvalidReceipt;

        try
        {
            _ = checked(bill.InputTokens + bill.OutputTokens);
        }
        catch (OverflowException)
        {
            return InvalidReceipt;
        }

        ReceivedProviderResponse proof = new(this, execution, effect, received.Value, bill);

        Result<GuildBudgetEffect> settled = await SettleProviderEffectCoreAsync(execution, daily, effect, bill,
            proof, CancellationToken.None).ConfigureAwait(false);

        return settled.IsFailure ? settled.Error : received;
    }

    internal Task<Result<GuildBudgetEffect>> SettleProviderEffectAsync(GuildLocalInferenceExecution execution,
        BudgetReservationService daily, GuildBudgetEffectRequest effect, BillableOperationRecord bill, CancellationToken ct)
        => SettleProviderEffectCoreAsync(execution, daily, effect, bill, null, ct);

    private Task<Result<GuildBudgetEffect>> SettleProviderEffectCoreAsync(GuildLocalInferenceExecution execution,
        BudgetReservationService? daily, GuildBudgetEffectRequest effect, BillableOperationRecord bill,
        ReceivedProviderResponse? proof, CancellationToken ct)
    {
        if (proof is not null && (!ReferenceEquals(proof.Store, this) || !ReferenceEquals(proof.Execution, execution)
            || !ReferenceEquals(proof.Effect, effect) || !ReferenceEquals(proof.Bill, bill) || proof.Response is null))
            return Task.FromResult(Result<GuildBudgetEffect>.Failure(InvalidReceipt));

        if (!ProviderIdentityMatches(execution, effect) || !_providerReceipts.TryGetValue(effect, out var owner)
            || !ReferenceEquals(owner, execution) || allocations is not GuildBudgetLedger ledger
            || !ValidProviderBill(effect, bill))
            return Task.FromResult(Result<GuildBudgetEffect>.Failure(InvalidReceipt));

        // Evidence is allowed after revocation. It grants no new send and must not be discarded because authority ended.
        return authority.InQuestTransactionAsync<GuildBudgetEffect>(execution.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildBudgetEffect> found = await ledger.ReadEffectWithinAsync(connection, transaction, effect.EffectId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                if (found.Value.Request with { ReservationId = effect.ReservationId } != effect
                    || found.Value.State is GuildBudgetEffectState.Prepared or GuildBudgetEffectState.ProvenNoDispatch) return Conflict;

                Result<GuildBudgetEffect?> initial = await GuildSpendSourceStore.ReadInitialWithinAsync(connection,
                    transaction, effect, bill, token).ConfigureAwait(false);

                if (initial.IsFailure) return initial.Error;

                // Saved evidence is independent of a later mutable bill or pruned Portal event.
                if (initial.Value is { } saved) return saved;

                bool legacy;

                bool hasEvidence;

                await using (SqliteCommand prior = Command(connection, transaction, """
                    SELECT EXISTS(SELECT 1 FROM guild_budget_evidence WHERE ReceiptId=$receipt),
                        EXISTS(SELECT 1 FROM guild_budget_evidence WHERE EffectId=$effect)
                    """))
                {
                    Add(prior, "$receipt", Id(effect.SourceId));

                    Add(prior, "$effect", Id(effect.EffectId));

                    await using SqliteDataReader reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Conflict;

                    legacy = reader.GetBoolean(0);

                    hasEvidence = reader.GetBoolean(1);
                }

                if (legacy)
                {
                    // A legacy receipt cannot supply a complete initial snapshot. Its old exact replay
                    // remains available only while the unique original bill is still present.
                    if (!await ProviderBillMatchesWithinAsync(connection, transaction, effect.SourceId, bill, token).ConfigureAwait(false)) return Conflict;
                }
                else
                {
                    if (hasEvidence || found.Value.State != GuildBudgetEffectState.PossiblyDispatched) return Conflict;

                    // The accounting adapter checks normalized aliases after this insert. Any duplicate
                    // causes the caller-owned transaction, including this insert, to roll back.
                    _ = await TurnRunWriter.RecordBillableOperationWithinAsync(connection, transaction, effect.SourceId, bill, token).ConfigureAwait(false);
                }

                Result<GuildBudgetEffect> applied = proof is null || legacy
                    ? await GuildBudgetPhysicalAccounting.ApplyLocalBillWithinAsync(connection,
                        transaction, ledger, effect.EffectId, effect.SourceId, token, daily).ConfigureAwait(false)
                    : await ApplyReceivedProviderBillWithinAsync(connection, transaction, ledger, found.Value,
                        bill, daily, token).ConfigureAwait(false);

                if (applied.IsFailure || legacy) return applied;

                Result<GuildQuestStored> quest = await ReadWithinAsync(connection, transaction, effect.QuestId, token).ConfigureAwait(false);

                if (quest.IsFailure) return quest.Error;

                if (quest.Value.Quest.Revision == long.MaxValue) return ExhaustedReceipt;

                await using SqliteCommand update = Command(connection, transaction,
                    "UPDATE guild_quests SET Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$quest AND Revision=$revision");

                Add(update, "$now", UtcInstantText.Format(time.GetUtcNow()));

                Add(update, "$quest", Id(effect.QuestId));

                Add(update, "$revision", quest.Value.Quest.Revision);

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                DateTimeOffset acceptedAt = time.GetUtcNow();

                Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
                    execution.Driver.Execution.RootQuestId, effect.QuestId, GuildEventKind.SpendChanged,
                    effect.QuestId, quest.Value.Quest.Revision + 1, null, acceptedAt, token,
                    spend: new(effect.EffectId, effect.SourceId, applied.Value.Revision, GuildSpendOrigin.LocalResponse)).ConfigureAwait(false);

                if (projected.IsFailure) return projected.Error;

                Result captured = await GuildSpendSourceStore.CaptureInitialWithinAsync(connection, transaction,
                    effect, bill, applied.Value, projected.Value, token).ConfigureAwait(false);

                if (captured.IsFailure) return captured.Error;

                GuildSpendReceiptDto receipt = new(effect.SourceId, effect.QuestId, effect.EffectId, GuildSpendOrigin.LocalResponse,
                    null, new(applied.Value.KnownUsd, applied.Value.KnownTokens, applied.Value.UnknownUsd, applied.Value.UnknownTokens,
                        applied.Value.State == GuildBudgetEffectState.Complete,
                        new(bill.InputTokens, bill.OutputTokens, bill.CachedTokens, bill.ReasoningTokens)),
                    applied.Value.Revision, projected.Value, acceptedAt);

                Result receiptSaved = await SavePhysicalSpendReceiptWithinAsync(connection, transaction, receipt, Id(effect.SourceId), token).ConfigureAwait(false);

                return receiptSaved.IsFailure ? receiptSaved.Error : applied;
            }, ct);
    }

    private static async Task<Result<GuildBudgetEffect>> ApplyReceivedProviderBillWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildBudgetLedger ledger, GuildBudgetEffect effect, BillableOperationRecord bill,
        BudgetReservationService? daily, CancellationToken ct)
    {
        GuildBudgetEffectRequest request = effect.Request;

        if (!await ProviderBillMatchesWithinAsync(connection, transaction, request.SourceId, bill, ct).ConfigureAwait(false))
            return Conflict;

        try
        {
            long tokens = checked(bill.InputTokens + bill.OutputTokens);

            bool unknownTokens = bill.Status != BillableOperationStatus.Completed;

            bool unknownUsd = unknownTokens || request.UsdBound is null;

            string identity = string.Join("|", request.SourceId.ToString("N"), request.AccountingAssociationId.ToString("N"),
                ExactUsdText.Format(bill.ActualCostUsd), tokens.ToString(CultureInfo.InvariantCulture), true, unknownUsd, unknownTokens);

            GuildBudgetEvidence evidence = new(request.SourceId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
                bill.ActualCostUsd, tokens, unknownUsd, unknownTokens, true);

            Result<GuildBudgetEffect> applied = await ledger.ApplyEvidenceWithinAsync(connection, transaction,
                request.EffectId, evidence, ct).ConfigureAwait(false);

            if (applied.IsFailure) return applied.Error;

            if (request.ReservationId is { } reservation)
            {
                if (daily is null) return InvalidReceipt;

                Result disposition = !unknownUsd
                    ? await daily.ReconcileWithinAsync(connection, transaction, reservation, request.AccountingAssociationId,
                        bill.ActualCostUsd, true, ct).ConfigureAwait(false)
                    : await daily.RecordPartialUsageWithinAsync(connection, transaction, reservation, request.AccountingAssociationId,
                        bill.ActualCostUsd, ct).ConfigureAwait(false);

                if (disposition.IsFailure) return disposition.Error;
            }

            return applied;
        }
        catch (OverflowException)
        {
            return InvalidReceipt;
        }
    }

    private static bool ValidProviderBill(GuildBudgetEffectRequest effect, BillableOperationRecord bill) =>
        bill is not null && bill.RunId == effect.AccountingAssociationId && bill.InputTokens >= 0 && bill.OutputTokens >= 0
        && bill.CachedTokens >= 0 && bill.CachedTokens <= bill.InputTokens && bill.ReasoningTokens >= 0
        && bill.ReasoningTokens <= bill.OutputTokens && bill.ActualCostUsd >= 0
        && Enum.IsDefined(bill.OperationType) && Enum.IsDefined(bill.Status);

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
            SELECT count(*),coalesce(sum(CASE WHEN RunId=$run AND OperationType=$type
                AND Provider=$provider AND Model=$model AND Purpose=$purpose AND StartedAt=$started AND CompletedAt=$completed
                AND InputTokens=$input AND OutputTokens=$output AND ReasoningTokens=$reasoning AND CachedTokens=$cached
                AND PricingSnapshotJson=$pricing AND ActualCostUsd=$cost AND Status=$status AND ProviderRequestId IS $request
                THEN 1 ELSE 0 END),0)
            FROM BillableOperations WHERE Id=$compact COLLATE NOCASE OR Id=$dashed COLLATE NOCASE
            """);

        Add(command, "$compact", sourceId.ToString("N"));

        Add(command, "$dashed", sourceId.ToString("D"));

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

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) && reader.GetInt64(0) == 1 && reader.GetInt64(1) == 1;
    }
}
