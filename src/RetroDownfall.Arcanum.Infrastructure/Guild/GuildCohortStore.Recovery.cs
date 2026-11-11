using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildCohortWorkCursor(int Phase, Guid? LastId, Guid? LastFrontierId);

internal sealed record GuildCohortWorkPage(ImmutableArray<Guid> CohortIds, GuildCohortWorkCursor? NextAfter);

internal sealed partial class GuildCohortStore
{
    private sealed record RecoveryProgress(CohortConvergenceDto? Projection);

    private sealed record CohortIssuerFacts(Guid RootQuestId, Guid? SessionId, Guid? CampaignId, bool Stopped, bool Ambiguous);

    internal Task<Result<GuildCohortWorkPage>> ReadWorkPageAsync(GuildCallerContext operatorCaller, GuildCohortWorkCursor? afterCohortId, CancellationToken ct) =>
        operatorCaller?.Kind != GuildCallerKind.Operator
            ? Task.FromResult(Result<GuildCohortWorkPage>.Failure(Forbidden))
            : guild.ReadCohortWorkPageAsync(operatorCaller, policy, afterCohortId, ct);

    internal async Task<Result<CohortConvergenceDto>> ReconcileAsync(GuildCallerContext operatorCaller, Guid cohortId, CancellationToken ct)
    {
        if (operatorCaller?.Kind != GuildCallerKind.Operator || cohortId == Guid.Empty) return Forbidden;

        Result<Guid> root = await guild.FindCohortRootAsync(operatorCaller, policy, cohortId, ct).ConfigureAwait(false);

        if (root.IsFailure) return root.Error;

        Result<RecoveryProgress> progress = await authority.InQuestTransactionAsync<RecoveryProgress>(root.Value,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildCohortStored> found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                CohortDto cohort = found.Value.Cohort;

                if (cohort.RootQuestId != root.Value) return Unavailable;

                Result allowed = await policy.AuthorizeLocateAsync(operatorCaller, connection, transaction,
                    cohort.IssuerQuestId, null, cohort.CampaignId, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                Result<CohortIssuerFacts> issuer = await ReadIssuerFactsWithinAsync(connection, transaction, cohort.IssuerQuestId, token).ConfigureAwait(false);

                if (issuer.IsFailure) return issuer.Error;

                if (issuer.Value.RootQuestId != cohort.RootQuestId || issuer.Value.CampaignId != cohort.CampaignId) return Unavailable;

                Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction,
                    issuer.Value.SessionId, issuer.Value.CampaignId, token).ConfigureAwait(false);

                bool ownerGone = owner.IsFailure && owner.Error.Code is ErrorCodes.Guild.OwnerUnavailable or ErrorCodes.Guild.NotFound;

                if (owner.IsFailure && !ownerGone) return owner.Error;

                bool stoppedIssuer = issuer.Value.Stopped;

                if (cohort.State is GuildCohortState.Open or GuildCohortState.Closed)
                {
                    Result<GuildGrant> live = await signets.ValidateCurrentGrantAsync(cohort.IssuerQuestId, token).ConfigureAwait(false);

                    bool revoked = live.IsFailure && live.Error.Code is ErrorCodes.Guild.AuthorityRevoked or ErrorCodes.Guild.AuthorityExpired;

                    if (!ownerGone && !stoppedIssuer && !revoked && (live.IsFailure || issuer.Value.Ambiguous))
                    {
                        // Already committed stop frontiers still drain when admission is disabled.
                        Result<bool> pendingStop = await AdvanceStopFrontierWithinAsync(connection, transaction, cohortId, time.GetUtcNow(), token).ConfigureAwait(false);

                        if (pendingStop.IsFailure) return pendingStop.Error;

                        return pendingStop.Value ? new RecoveryProgress(null) : live.IsFailure ? live.Error : Unavailable;
                    }

                    if (ownerGone || stoppedIssuer || revoked)
                    {
                        Result canceled = await CancelForIssuerWithinAsync(connection, transaction, cohort, time.GetUtcNow(), token).ConfigureAwait(false);

                        return canceled.IsFailure ? canceled.Error : new RecoveryProgress(null);
                    }
                }

                Result<bool> advanced = await AdvanceStopFrontierWithinAsync(connection, transaction, cohortId, time.GetUtcNow(), token).ConfigureAwait(false);

                if (advanced.IsFailure) return advanced.Error;

                if (advanced.Value) return new RecoveryProgress(null);

                if (cohort.State == GuildCohortState.Closed)
                {
                    string? before = await EvaluationStampAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

                    Result<bool> evaluated = await EvaluateWithinAsync(connection, transaction, found.Value, policy, time.GetUtcNow(), token).ConfigureAwait(false);

                    if (evaluated.IsFailure) return evaluated.Error;

                    string? after = await EvaluationStampAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

                    if (evaluated.Value || before != after) return new RecoveryProgress(null);
                }

                Result<GuildCohortSettlementStep> settlement = await AdvanceSettlementWithinAsync(connection, transaction, cohort, token).ConfigureAwait(false);

                if (settlement.IsFailure) return settlement.Error;

                if (!settlement.Value.Complete || settlement.Value.Snapshot is not { } snapshot) return new RecoveryProgress(null);

                GuildConvergenceDecision? decision;
                string? reason;
                long frozen;

                await using (SqliteCommand state = Command(connection, transaction,
                    "SELECT DecisionCode,ReasonCode,COALESCE(FrozenMemberCount,0) FROM guild_cohorts WHERE Id=$cohort"))
                {
                    Add(state, "$cohort", Id(cohortId));

                    await using SqliteDataReader reader = await state.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Unavailable;

                    decision = reader.IsDBNull(0) ? null : (GuildConvergenceDecision)reader.GetInt32(0);
                    reason = reader.IsDBNull(1) ? null : reader.GetString(1);
                    frozen = reader.GetInt64(2);
                }

                if (cohort.State == GuildCohortState.Draining && decision is not null
                    && settlement.Value.PendingMembers == 0 && settlement.Value.PendingDescendants == 0
                    && settlement.Value.CancelRequested == 0 && settlement.Value.PendingAccounts == 0 && settlement.Value.PendingEffects == 0 && !snapshot.UnknownUsd && !snapshot.UnknownTokens
                    && snapshot.UncertainCount == 0 && snapshot.UnpricedCount == 0
                    && Empty(snapshot.OutstandingAllocation) && Empty(snapshot.UnresolvedHolds))
                {
                    Result closed = await budgets.CloseAccountWithinAsync(connection, transaction, cohort.AllocationAccountId, cohort.Id, token).ConfigureAwait(false);

                    if (closed.IsFailure) return closed.Error;

                    GuildCohortState terminal = decision == GuildConvergenceDecision.Success ? GuildCohortState.Completed
                        : decision == GuildConvergenceDecision.Impossible ? GuildCohortState.Failed : GuildCohortState.Canceled;

                    Result finalized = await FinalizeCohortWithinAsync(connection, transaction, cohort, terminal, time.GetUtcNow(), token).ConfigureAwait(false);

                    return finalized.IsFailure ? finalized.Error : new RecoveryProgress(null);
                }

                GuildDataRef? selected = null;

                if (decision == GuildConvergenceDecision.Success && cohort.Policy.Kind != GuildConvergenceKind.All && !ownerGone)
                {
                    Result<GuildDataRef?> reference = await ReadSingleSelectedReferenceWithinAsync(connection, transaction, cohort, token).ConfigureAwait(false);

                    if (reference.IsFailure) return reference.Error;

                    selected = reference.Value;
                }

                GuildSpendDto spend = new(cohort.IssuerQuestId, snapshot.KnownUsd, snapshot.KnownTokens,
                    snapshot.UnknownUsd, snapshot.UnknownTokens, snapshot.PricedCount, snapshot.UnpricedCount,
                    snapshot.UncertainCount, snapshot.OutstandingAllocation, snapshot.UnresolvedHolds,
                    snapshot.Remaining, cohort.EventWatermark);

                return new RecoveryProgress(new(cohortId, cohort.Revision, cohort.State, decision, reason, selected,
                    frozen, settlement.Value.PendingMembers, settlement.Value.PendingDescendants,
                    settlement.Value.CancelRequested, spend, cohort.EventWatermark));
            }, ct).ConfigureAwait(false);

        if (progress.IsFailure) return progress.Error;

        return progress.Value.Projection is { } projection ? projection
            : Result<CohortConvergenceDto>.Failure(new(ErrorCodes.Guild.MaintenanceDeferred, "Cohort reconciliation made bounded progress and will continue."));
    }

    private static async Task<Result<CohortIssuerFacts>> ReadIssuerFactsWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid issuerId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT q.RootQuestId,q.SessionId,q.CampaignId,q.Retired,q.CancellationRequested,o.State,
                q.LastNonterminalStateCode,q.WaitReasonCode,q.QuestionId,q.ContinuationId,o.TerminalErrorCode,
                EXISTS(SELECT 1 FROM guild_result_receipts r WHERE r.QuestId=q.Id),
                CASE WHEN typeof(q.RootQuestId)='text' AND length(CAST(q.RootQuestId AS BLOB))=36
                  AND (q.SessionId IS NULL OR (typeof(q.SessionId)='text' AND length(CAST(q.SessionId AS BLOB))=36))
                  AND (q.CampaignId IS NULL OR (typeof(q.CampaignId)='text' AND length(CAST(q.CampaignId AS BLOB))=36))
                  AND typeof(q.Retired)='integer' AND q.Retired IN(0,1)
                  AND typeof(q.CancellationRequested)='integer' AND q.CancellationRequested IN(0,1)
                  AND typeof(o.State)='integer' AND o.State BETWEEN 0 AND 7
                  AND (o.TerminalErrorCode IS NULL OR (typeof(o.TerminalErrorCode)='text' AND length(CAST(o.TerminalErrorCode AS BLOB))<=128))
                  AND (q.QuestionId IS NULL OR (typeof(q.QuestionId)='text' AND length(CAST(q.QuestionId AS BLOB))=36))
                  AND (q.ContinuationId IS NULL OR (typeof(q.ContinuationId)='text' AND length(CAST(q.ContinuationId AS BLOB))=36))
                  THEN 1 ELSE 0 END
            FROM guild_quests q JOIN LongRunningOperations o ON o.Id=q.OperationId WHERE q.Id=$issuer
            """);

        Add(command, "$issuer", Id(issuerId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt64(12) != 1
            || !Guid.TryParseExact(reader.GetString(0), "D", out Guid root) || root == Guid.Empty) return Unavailable;

        Guid? session = null;
        Guid? campaign = null;

        if (!reader.IsDBNull(1))
        {
            if (!Guid.TryParseExact(reader.GetString(1), "D", out Guid value) || value == Guid.Empty) return Unavailable;
            session = value;
        }

        if (!reader.IsDBNull(2))
        {
            if (!Guid.TryParseExact(reader.GetString(2), "D", out Guid value) || value == Guid.Empty) return Unavailable;
            campaign = value;
        }

        bool stopped = reader.GetInt64(3) == 1 || reader.GetInt64(4) == 1 || reader.GetInt64(5) is >= 3 and <= 6;
        bool attention = reader.GetInt64(5) == 7;
        bool parkedShape = attention && !stopped && reader.GetInt64(6) == (long)QuestState.InputRequired
            && !reader.IsDBNull(7) && reader.GetInt64(7) == (long)GuildWaitReason.Input
            && !reader.IsDBNull(8) && !reader.IsDBNull(9) && !reader.IsDBNull(10)
            && reader.GetString(10) == ErrorCodes.Guild.AwaitingInput && reader.GetInt64(11) == 0
            && Guid.TryParseExact(reader.GetString(8), "D", out Guid questionId) && questionId != Guid.Empty
            && Guid.TryParseExact(reader.GetString(9), "D", out Guid continuationId) && continuationId != Guid.Empty;

        GuildQuestInputEvidence? question = parkedShape
            ? new(Guid.Parse(reader.GetString(8)), Guid.Parse(reader.GetString(9))) : null;

        await reader.DisposeAsync().ConfigureAwait(false);

        if (question is not null && !await GuildQuestStore.HasClosedAncestorAsync(connection, transaction, issuerId, ct).ConfigureAwait(false)
            && (await GuildQuestStore.RequireWaitFrontierWithinAsync(connection, transaction, issuerId, ct).ConfigureAwait(false)).IsSuccess
            && await GuildQuestStore.HasParkedInputCheckpointWithinAsync(connection, transaction, issuerId, question, ct).ConfigureAwait(false))
            attention = false;

        return new CohortIssuerFacts(root, session, campaign, stopped, attention && !stopped);
    }

    private static async Task<string?> EvaluationStampAsync(SqliteConnection connection, SqliteTransaction transaction, Guid cohort, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT Generation||':'||ResultSequence||':'||LastResultSequence||':'||ProcessedCount||':'||InvalidCount||':'||MaxBucketCount||':'||
                COALESCE(PendingResultSequence,-1)||':'||CollisionAfterBucket FROM guild_cohort_eval_state WHERE CohortId=$cohort
            """);

        Add(command, "$cohort", Id(cohort));

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private static bool Empty(GuildBudgetGrant budget) => budget.Usd.Mode == GuildBudgetMode.Finite && budget.Usd.Value == 0
        && budget.Tokens.Mode == GuildBudgetMode.Finite && budget.Tokens.Value == 0;

    private static async Task<Result> CancelForIssuerWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        CohortDto cohort, DateTimeOffset now, CancellationToken ct)
    {
        if (cohort.Revision == long.MaxValue) return Unavailable;

        Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId,
            cohort.IssuerQuestId, GuildEventKind.CohortChanged, cohort.Id, cohort.Revision + 1, null, now, ct).ConfigureAwait(false);

        if (projected.IsFailure) return projected.Error;

        await using SqliteCommand command = Command(connection, transaction, """
            UPDATE guild_cohorts SET StateCode=3,DecisionCode=3,ReasonCode='issuer-unavailable',
                EventWatermark=$watermark,Revision=Revision+1 WHERE Id=$cohort AND Revision=$revision AND StateCode IN(1,2);
            INSERT INTO guild_cohort_stop_frontiers(Id,CohortId,MemberId) VALUES($frontier,$cohort,NULL);
            """);

        Add(command, "$cohort", Id(cohort.Id));
        Add(command, "$revision", cohort.Revision);
        Add(command, "$watermark", projected.Value);
        Add(command, "$frontier", Id(Guid.NewGuid()));

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 2 ? Result.Success() : Conflict;
    }

    private static async Task<Result> FinalizeCohortWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        CohortDto cohort, GuildCohortState terminal, DateTimeOffset now, CancellationToken ct)
    {
        if (cohort.Revision == long.MaxValue) return Unavailable;

        Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId,
            cohort.IssuerQuestId, GuildEventKind.CohortChanged, cohort.Id, cohort.Revision + 1, null, now, ct).ConfigureAwait(false);

        if (projected.IsFailure) return projected.Error;

        await using SqliteCommand command = Command(connection, transaction, """
            UPDATE guild_cohorts SET StateCode=$state,EventWatermark=$watermark,Revision=Revision+1
            WHERE Id=$cohort AND Revision=$revision AND StateCode=3
            """);

        Add(command, "$state", (int)terminal);
        Add(command, "$watermark", projected.Value);
        Add(command, "$cohort", Id(cohort.Id));
        Add(command, "$revision", cohort.Revision);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Success() : Conflict;
    }
}
