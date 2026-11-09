using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal const int TreePageMaximumBytes = 1_048_576;

    /// <summary>One indexed data statement captures membership, current projections and the unfiltered summary.
    /// Authority and the retained opaque continuation are checked separately on the same admitted transaction.</summary>
    internal Task<Result<GuildQuestTreePage>> GetTreeAsync(GuildCallerContext caller, Guid questId,
        GuildPageQuery query, QuestState? state, CancellationToken ct)
    {
        if (caller is null || questId == Guid.Empty || query is null || query.PageSize is < 1 or > 100
            || state is not null && !Enum.IsDefined(state.Value))
            return Task.FromResult(Result<GuildQuestTreePage>.Failure(new(ErrorCodes.Guild.InvalidRequest, "The tree query is invalid.")));

        return authority.InQuestTransactionAsync<GuildQuestTreePage>(caller.Execution?.RootQuestId ?? questId,
            async (connection, transaction, signets, token) =>
            {
                string callerKey = TreeCallerKey(caller);

                string scope = TreeScope(questId, query.PageSize, state);

                Result<Guid> epoch = await GuildSignetStore.EpochAsync(connection, transaction, token).ConfigureAwait(false);

                if (epoch.IsFailure) return epoch.Error;

                TreeCheckpoint? checkpoint = null;

                if (query.Cursor is { } cursor)
                {
                    Result<TreeCheckpoint> saved = await ReadTreeCursorWithinAsync(connection, transaction, callerKey,
                        scope, cursor, epoch.Value, token).ConfigureAwait(false);

                    if (saved.IsFailure) return saved.Error;

                    checkpoint = saved.Value;
                }

                if (caller.InstallationGeneration != policy.InstallationGeneration)
                    return checkpoint is null ? OwnerUnavailable : TreeCursorGone;

                // A run may enumerate only its own descendant domain. Enforce the immutable
                // lineage here as well as in host policy, so locating a relative cannot widen it.
                if (caller.Execution is { } scoped)
                {
                    await using SqliteCommand lineage = Command(connection, transaction, """
                        WITH RECURSIVE ancestors(Id,ParentQuestId) AS (
                            SELECT Id,ParentQuestId FROM guild_quests WHERE Id=$target AND RootQuestId=$root
                            UNION SELECT parent.Id,parent.ParentQuestId FROM guild_quests parent JOIN ancestors child ON parent.Id=child.ParentQuestId
                                WHERE parent.RootQuestId=$root)
                        SELECT EXISTS(SELECT 1 FROM ancestors WHERE Id=$issuer)
                        """);

                    Add(lineage, "$target", Id(questId));

                    Add(lineage, "$root", Id(scoped.RootQuestId));

                    Add(lineage, "$issuer", Id(scoped.QuestId));

                    if (!Equals(1L, await lineage.ExecuteScalarAsync(token).ConfigureAwait(false))) return OwnerUnavailable;
                }

                Result located = await policy.AuthorizeLocateAsync(caller, connection, transaction, questId, null, null, token).ConfigureAwait(false);

                if (located.IsFailure) return located.Error.Code == ErrorCodes.Guild.Forbidden ? OwnerUnavailable : located.Error;

                Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (found.IsFailure)
                    return checkpoint is not null && found.Error.Code is ErrorCodes.Guild.NotFound or ErrorCodes.Guild.OwnerUnavailable
                        ? TreeCursorGone : found.Error;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, found.Value, token).ConfigureAwait(false);

                if (visible.IsFailure)
                    return checkpoint is not null && visible.Error.Code == ErrorCodes.Guild.OwnerUnavailable ? TreeCursorGone
                        : visible.Error.Code == ErrorCodes.Guild.Forbidden ? OwnerUnavailable : visible.Error;

                if (caller.Execution is { } execution)
                {
                    Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                    if (live.IsFailure) return live.Error;
                }

                GuildQuestDto owner = found.Value.Quest;

                string ownerKey = TreeOwnerKey(owner);

                if (checkpoint is not null && (checkpoint.Root != owner.RootQuestId || checkpoint.OwnerKey != ownerKey))
                    return TreeCursorGone;

                Result<GuildQuestTreePage> page = await ReadTreePageWithinAsync(connection, transaction,
                    owner, checkpoint, query.PageSize, state, token).ConfigureAwait(false);

                if (page.IsFailure) return page.Error;

                GuildQuestTreePage result = page.Value;

                if (result.NextCursor is not null)
                {
                    GuildQuestTreeNode last = result.Items[^1];

                    string next = await SaveTreeCursorWithinAsync(connection, transaction, callerKey, ownerKey,
                        scope, owner.RootQuestId, epoch.Value, result.SnapshotSequence, last.IssuedSequence, last.Id, token).ConfigureAwait(false);

                    result = result with { NextCursor = next };
                }

                // Nodes contain only bounded identities, enums and scalars. Check the actual generated
                // representation too; adding a field cannot silently weaken the page byte contract.
                return JsonSerializer.SerializeToUtf8Bytes(result, GuildTreeJsonContext.Default.GuildQuestTreePage).Length <= TreePageMaximumBytes
                    ? result : Unavailable;
            }, ct);
    }

    private static async Task<Result<GuildQuestTreePage>> ReadTreePageWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestDto owner, TreeCheckpoint? checkpoint, int size, QuestState? state, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, TreePageSql(owner.ParentQuestId is null));

        Add(command, "$root", Id(owner.RootQuestId));

        Add(command, "$subtree", Id(owner.Id));

        Add(command, "$snapshot", checkpoint is null ? DBNull.Value : checkpoint.Snapshot);

        Add(command, "$after", checkpoint?.After ?? 0);

        Add(command, "$afterId", checkpoint is null ? string.Empty : Id(checkpoint.Last));

        Add(command, "$state", state is null ? DBNull.Value : (int)state.Value);

        Add(command, "$limit", size + 1);

        ImmutableArray<GuildQuestTreeNode>.Builder items = ImmutableArray.CreateBuilder<GuildQuestTreeNode>();

        GuildQuestTreeSummary? summary = null;

        long snapshot = 0;

        bool more = false;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.GetInt64(6) != 0) return Unavailable;

            snapshot = reader.GetInt64(0);

            summary ??= new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));

            if (reader.IsDBNull(7)) continue;

            if (items.Count == size)
            {
                more = true;

                continue;
            }

            items.Add(new(Guid.Parse(reader.GetString(7)), Guid.Parse(reader.GetString(8)),
                reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)), reader.GetInt64(10), reader.GetInt64(11),
                (QuestState)reader.GetInt64(12), reader.IsDBNull(13) ? null : (GuildWaitReason)reader.GetInt64(13),
                reader.GetBoolean(14), reader.GetBoolean(15), reader.GetBoolean(16), (GuildAccountingState)reader.GetInt64(17), reader.GetBoolean(18)));
        }

        return summary is null ? Unavailable : new GuildQuestTreePage(summary, items.ToImmutable(), more ? "pending" : null, snapshot);
    }

    // Keep the page statement visible to real SQLCipher query-plan tests. It performs no per-node
    // commands and never selects content, names, grants, or retained variable-length error text.
    internal static string TreePageSql(bool root) => """
        WITH RECURSIVE high(Snapshot) AS (
            SELECT COALESCE($snapshot,MAX(IssuedSequence),0) FROM guild_quests INDEXED BY idx_guild_quests_root WHERE RootQuestId=$root),
        members(Id) AS (
        """ + (root ? """
            SELECT Id FROM guild_quests INDEXED BY idx_guild_quests_root
            WHERE RootQuestId=$root AND IssuedSequence<=(SELECT Snapshot FROM high)
        """ : """
            SELECT Id FROM guild_quests WHERE Id=$subtree AND IssuedSequence<=(SELECT Snapshot FROM high)
            UNION
            SELECT child.Id FROM guild_quests child INDEXED BY idx_guild_quests_parent JOIN members parent ON child.ParentQuestId=parent.Id
            WHERE child.RootQuestId=$root AND child.IssuedSequence<=(SELECT Snapshot FROM high)
        """) + $"""
        ), facts AS MATERIALIZED (
            SELECT c.Id,c.RootQuestId,c.ParentQuestId,c.IssuedSequence,c.Revision,c.LastNonterminalStateCode,c.WaitReasonCode,
                c.CancellationRequested,c.CancellationConverged,c.AccountingStateCode,c.Retired,
                o.State AS OperationState,
                CASE WHEN c.CancellationRequested=1 OR o.State=3 OR o.TerminalErrorCode='operation.cancelled' THEN 1 ELSE 0 END AS ObservedCancellation,
                CASE WHEN r.ResultId='00000000-0000-0000-0000-000000000000' THEN NULL
                    WHEN o.State NOT IN(4,5,6) AND r.QuestId IS NOT NULL THEN NULL
                    WHEN o.State=0 THEN 0 WHEN o.State=1 THEN 1
                    WHEN o.State=2 AND c.WaitReasonCode=0 AND c.QuestionId IS NOT NULL AND c.ContinuationId IS NOT NULL
                        AND c.QuestionId<>'00000000-0000-0000-0000-000000000000'
                        AND c.ContinuationId<>'00000000-0000-0000-0000-000000000000' THEN 2
                    WHEN o.State=2 AND c.LastNonterminalStateCode IN(0,1) AND c.WaitReasonCode IN(1,2,3) THEN c.LastNonterminalStateCode
                    WHEN o.State=3 THEN c.LastNonterminalStateCode
                    WHEN o.State=4 AND o.TerminalErrorCode IS NULL AND r.OutcomeCode=3 AND r.ReasonCode IS NULL THEN 3
                    WHEN o.State=4 AND o.TerminalErrorCode='operation.cancelled' AND r.OutcomeCode=5 AND r.ReasonCode='operation.cancelled'
                        AND r.WorkStoppedProven=1 AND {TreeExecutionConvergenceSql}
                        THEN 5
                    WHEN o.State=5 AND o.TerminalErrorCode='Guild.Rejected' AND r.NoDispatchProven=1
                        AND (r.QuestId IS NULL OR (r.OutcomeCode=6 AND r.ReasonCode='Guild.Rejected')) THEN 6
                    WHEN o.State IN(5,6) AND o.TerminalErrorCode IS NOT NULL AND length(trim(o.TerminalErrorCode,char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)))>0
                        AND o.TerminalErrorCode NOT IN('operation.cancelled','Guild.Rejected','Guild.ReconciliationRequired')
                        AND r.OutcomeCode=4 AND r.ReasonCode=o.TerminalErrorCode THEN 4
                    ELSE NULL END AS RecognizedState,
                CASE WHEN {GuildQuestRetentionPins.StoppedExecutionUnresolved}
                    THEN 1 ELSE 0 END AS ExecutionOpen,
                CASE WHEN (c.CancellationRequested=1 OR o.State=3 OR o.TerminalErrorCode='operation.cancelled')
                        AND {TreeExecutionConvergenceSql}
                    THEN 1 ELSE 0 END AS VerifiedConvergence,
                CASE WHEN {TreeGuidTextSql("c.Id")} AND {TreeGuidTextSql("c.RootQuestId")}
                    AND (c.ParentQuestId IS NULL OR {TreeGuidTextSql("c.ParentQuestId")})
                    AND (c.QuestionId IS NULL OR {TreeGuidTextSql("c.QuestionId")})
                    AND (c.ContinuationId IS NULL OR {TreeGuidTextSql("c.ContinuationId")})
                    AND typeof(c.IssuedSequence)='integer' AND c.IssuedSequence>0
                    AND typeof(c.Revision)='integer' AND c.Revision>0
                    AND c.LastNonterminalStateCode IN(0,1,2) AND typeof(c.LastNonterminalStateCode)='integer'
                    AND c.CancellationRequested IN(0,1) AND typeof(c.CancellationRequested)='integer'
                    AND c.CancellationConverged IN(0,1) AND typeof(c.CancellationConverged)='integer'
                    AND c.Retired IN(0,1) AND typeof(c.Retired)='integer'
                    AND c.AccountingStateCode IN(0,1,2) AND typeof(c.AccountingStateCode)='integer'
                    AND (c.WaitReasonCode IS NULL OR (typeof(c.WaitReasonCode)='integer' AND c.WaitReasonCode IN(0,1,2,3,4)))
                    AND c.InstallationGeneration=(SELECT InstallationGeneration FROM guild_quests WHERE Id=$subtree)
                    AND o.Id IS NOT NULL AND o.Kind='guild-quest' AND o.RunId=c.RunId
                    AND o.SessionId IS c.SessionId AND o.CheckpointReference=c.Id
                    AND typeof(o.RecoveryPolicy)='integer' AND o.RecoveryPolicy=2
                    AND typeof(o.CheckpointVersion)='integer' AND o.CheckpointVersion=1
                    AND CASE WHEN typeof(o.CheckpointPayload)='blob' AND length(o.CheckpointPayload) BETWEEN 1 AND 512
                        AND json_valid(CAST(o.CheckpointPayload AS TEXT)) THEN
                            json_type(CAST(o.CheckpointPayload AS TEXT))='object'
                            AND json_type(CAST(o.CheckpointPayload AS TEXT),'$.questId')='text'
                            AND upper(json_extract(CAST(o.CheckpointPayload AS TEXT),'$.questId'))=c.Id
                            AND json_type(CAST(o.CheckpointPayload AS TEXT),'$.version')='integer'
                            AND json_extract(CAST(o.CheckpointPayload AS TEXT),'$.version')=1
                            AND json_extract(CAST(o.CheckpointPayload AS TEXT),'$.phase') IN('admitted','execution','delivery','awaitingInput','cancellation','reconciliation')
                            AND (SELECT COUNT(*) FROM json_each(CAST(o.CheckpointPayload AS TEXT)))=3
                            AND (SELECT COUNT(DISTINCT key) FROM json_each(CAST(o.CheckpointPayload AS TEXT)))=3
                            AND NOT EXISTS(SELECT 1 FROM json_each(CAST(o.CheckpointPayload AS TEXT)) WHERE key NOT IN('questId','version','phase'))
                        ELSE 0 END
                    AND (o.TerminalErrorCode IS NULL OR (typeof(o.TerminalErrorCode)='text' AND length(CAST(o.TerminalErrorCode AS BLOB))<=800))
                    AND (r.QuestId IS NULL OR ({TreeGuidTextSql("r.ResultId")}
                        AND typeof(r.Digest)='blob' AND length(r.Digest)=32
                        AND typeof(r.OutcomeCode)='integer' AND r.OutcomeCode IN(3,4,5,6)
                        AND typeof(r.NoDispatchProven)='integer' AND r.NoDispatchProven IN(0,1)
                        AND typeof(r.WorkStoppedProven)='integer' AND r.WorkStoppedProven IN(0,1)
                        AND (r.ReasonCode IS NULL OR (typeof(r.ReasonCode)='text' AND length(CAST(r.ReasonCode AS BLOB))<=65536))))
                    AND typeof(o.State)='integer' AND o.State BETWEEN 0 AND 7
                    AND s.QuestId=c.Id AND s.RootQuestId=c.RootQuestId AND s.RunId=c.RunId AND s.AgentId=c.AgentId
                    AND s.SessionId IS c.SessionId AND s.CampaignId IS c.CampaignId AND s.WorkspaceId IS c.WorkspaceId
                    AND s.InstallationGeneration=c.InstallationGeneration
                    AND s.OriginPrincipalId=(SELECT OriginPrincipalId FROM guild_signets WHERE QuestId=$subtree)
                    THEN 0 ELSE 1 END AS Invalid
            FROM members m JOIN guild_quests c ON c.Id=m.Id
            LEFT JOIN LongRunningOperations o ON o.Id=c.OperationId
            LEFT JOIN guild_result_receipts r ON r.QuestId=c.Id
            LEFT JOIN guild_signets s ON s.SignetId=c.SignetId
        ), projected AS MATERIALIZED (
            SELECT *,COALESCE(RecognizedState,LastNonterminalStateCode) AS LiveState,
                CASE WHEN RecognizedState IS NULL THEN 4 WHEN RecognizedState IN(3,4,5,6) OR OperationState IN(0,1) THEN NULL ELSE WaitReasonCode END AS LiveWait,
                ObservedCancellation AS LiveCancellation,
                CASE WHEN RecognizedState IS NULL THEN 1 ELSE 0 END AS Reconciliation,
                CASE WHEN ExecutionOpen=1 OR RecognizedState IS NULL THEN 1 ELSE 0 END AS Active
            FROM facts
        ), totals AS (
            SELECT COUNT(*) AS Members,COALESCE(SUM(Active),0) AS Active,COALESCE(SUM(Reconciliation),0) AS Reconciliation,
                COALESCE(SUM(LiveCancellation),0) AS Cancellation,COALESCE(SUM(CASE WHEN AccountingStateCode<>0 THEN 1 ELSE 0 END),0) AS Accounting,
                COALESCE(SUM(Invalid),0) AS Invalid FROM projected
        ), page AS (
            SELECT * FROM projected WHERE Invalid=0 AND ($state IS NULL OR LiveState=$state)
                AND (IssuedSequence>$after OR (IssuedSequence=$after AND Id>$afterId))
            ORDER BY IssuedSequence,Id LIMIT $limit
        )
        SELECT high.Snapshot,t.Members,t.Active,t.Reconciliation,t.Cancellation,t.Accounting,t.Invalid,
            p.Id,p.RootQuestId,p.ParentQuestId,p.IssuedSequence,p.Revision,p.LiveState,p.LiveWait,p.LiveCancellation,
            CASE WHEN p.VerifiedConvergence=1 AND p.LiveCancellation=1 AND p.Active=0 THEN 1 ELSE 0 END,
            p.Reconciliation,p.AccountingStateCode,p.Retired
        FROM high CROSS JOIN totals t LEFT JOIN page p ON 1=1 ORDER BY p.IssuedSequence,p.Id
        """;

    private static string TreeGuidTextSql(string column) => $"""
        (CASE WHEN typeof({column})='text' AND length(CAST({column} AS BLOB))=36 THEN
            substr({column},9,1)='-' AND substr({column},14,1)='-' AND substr({column},19,1)='-' AND substr({column},24,1)='-'
            AND length(replace({column},'-',''))=32 AND lower(replace({column},'-','')) NOT GLOB '*[^0-9a-f]*'
            ELSE 0 END)
        """;

    private static string TreeExecutionConvergenceSql => """
        NOT EXISTS(WITH RECURSIVE stopped_tree(Id) AS (
            SELECT Id FROM guild_quests WHERE Id=c.Id
            UNION SELECT child.Id FROM guild_quests child INDEXED BY idx_guild_quests_parent
                JOIN stopped_tree parent ON child.ParentQuestId=parent.Id)
            SELECT 1 FROM guild_quests stopped_member WHERE stopped_member.Id IN(SELECT Id FROM stopped_tree) AND
        """ + GuildQuestRetentionPins.StoppedExecutionUnresolved.Replace("c.", "stopped_member.", StringComparison.Ordinal) + ")";
}
