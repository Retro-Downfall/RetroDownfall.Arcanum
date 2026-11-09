using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildCancellationWork(Guid QuestId, long Revision, long IssuedSequence);

internal sealed partial class GuildQuestStore
{
    internal async Task<Result<bool>> RefreshCancellationConvergenceAsync(GuildCallerContext caller, Guid questId, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || questId == Guid.Empty) return InvalidReceipt;

        var located = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<bool>(located.Value.Quest.RootQuestId,
            async (connection, transaction, _, token) =>
            {
                var current = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, current.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                // The projection has just checked all physical subtree obligations in this
                // transaction. Persisting its truth retires discovery work, never accounting.
                if (!current.Value.Quest.CancellationRequested || !current.Value.Quest.CancellationConverged) return false;

                await using (SqliteCommand recorded = Command(connection, transaction,
                    "SELECT CancellationConverged FROM guild_quests WHERE Id=$quest"))
                {
                    Add(recorded, "$quest", Id(questId));

                    if (Equals(1L, await recorded.ExecuteScalarAsync(token).ConfigureAwait(false))) return true;
                }

                if (current.Value.Quest.Revision == long.MaxValue) return ExhaustedReceipt;

                DateTimeOffset now = time.GetUtcNow();

                await using SqliteCommand update = Command(connection, transaction, """
                    UPDATE guild_quests SET CancellationConverged=1,Revision=Revision+1,UpdatedAtUtc=$now
                    WHERE Id=$quest AND Revision=$revision AND CancellationRequested=1 AND CancellationConverged=0
                    """);

                Add(update, "$quest", Id(questId));

                Add(update, "$revision", current.Value.Quest.Revision);

                Add(update, "$now", UtcInstantText.Format(now));

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, current.Value.Quest.RootQuestId,
                    questId, GuildEventKind.QuestChanged, questId, current.Value.Quest.Revision + 1, null, now, token).ConfigureAwait(false);

                return projected.IsFailure ? projected.Error : true;
            }, ct).ConfigureAwait(false);
    }

    internal async Task<Result<ImmutableArray<GuildCancellationWork>>> ReadCancellationWorkPageAsync(
        GuildCallerContext caller, Guid questId, long afterSequence, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || questId == Guid.Empty || afterSequence < 0)
            return InvalidReceipt;

        var located = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<ImmutableArray<GuildCancellationWork>>(located.Value.Quest.RootQuestId,
            async (connection, transaction, _, token) =>
            {
                var current = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, current.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                // The committed frontier prevents later issuance, so sequence pagination cannot
                // miss a child committed before cancellation. Terminal nodes remain traversal edges.
                await using SqliteCommand page = Command(connection, transaction, """
                    WITH RECURSIVE descendants(Id) AS (
                        SELECT Id FROM guild_quests WHERE Id=$quest AND CancellationRequested=1
                        UNION SELECT c.Id FROM guild_quests c JOIN descendants p ON c.ParentQuestId=p.Id)
                    SELECT c.Id,c.Revision,c.IssuedSequence FROM descendants d JOIN guild_quests c ON c.Id=d.Id
                    WHERE c.Id<>$quest AND c.IssuedSequence>$after AND c.CancellationRequested=0 AND c.Retired=0
                        AND NOT EXISTS(SELECT 1 FROM guild_result_receipts r WHERE r.QuestId=c.Id)
                    ORDER BY c.IssuedSequence,c.Id LIMIT 100
                    """);

                Add(page, "$quest", Id(questId));

                Add(page, "$after", afterSequence);

                ImmutableArray<GuildCancellationWork>.Builder work = ImmutableArray.CreateBuilder<GuildCancellationWork>();

                await using SqliteDataReader reader = await page.ExecuteReaderAsync(token).ConfigureAwait(false);

                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid id) || id == Guid.Empty
                        || reader.GetInt64(1) < 1 || reader.GetInt64(2) <= afterSequence) return Unavailable;

                    work.Add(new(id, reader.GetInt64(1), reader.GetInt64(2)));
                }

                return work.ToImmutable();
            }, ct).ConfigureAwait(false);
    }
}
