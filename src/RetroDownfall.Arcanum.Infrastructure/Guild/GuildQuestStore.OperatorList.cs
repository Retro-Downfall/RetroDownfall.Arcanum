using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildQuestPageDto>> ListOperatorAsync(GuildCallerContext caller, GuildListQuestsQuery query, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || query is null || query.RootId == Guid.Empty
            || query.ParentId == Guid.Empty || query.PageSize is < 1 or > 100 || query.State is { } state && !Enum.IsDefined(state))
            return Task.FromResult(Result<GuildQuestPageDto>.Failure(new(ErrorCodes.Guild.InvalidRequest, "The Quest page is invalid.")));

        return authority.InQuestTransactionAsync<GuildQuestPageDto>(caller.InstallationGeneration, async (connection, transaction, _, token) =>
        {
            Result authenticated = await policy.AuthorizeLocateAsync(caller, connection, transaction, null, null, null, token).ConfigureAwait(false);

            if (authenticated.IsFailure) return authenticated.Error;

            string scope = "quest-list:" + Id(query.RootId) + ":" + Id(query.ParentId) + ":"
                + (query.State is null ? "*" : ((int)query.State.Value).ToString(CultureInfo.InvariantCulture)) + ":" + query.PageSize.ToString(CultureInfo.InvariantCulture);

            string callerKey = TreeCallerKey(caller);

            Result<Guid> epoch = await GuildSignetStore.EpochAsync(connection, transaction, token).ConfigureAwait(false);

            if (epoch.IsFailure) return epoch.Error;

            Result<string> visibility = await GuildOperatorVisibility.ReadAsync(connection, transaction, policy, token).ConfigureAwait(false);

            if (visibility.IsFailure) return visibility.Error;

            long after = 0;
            long snapshot;

            if (query.Cursor is { } cursor)
            {
                if (cursor.Length != 32 || !Guid.TryParseExact(cursor, "N", out Guid cursorId) || cursorId == Guid.Empty) return TreeCursorInvalid;

                await using SqliteCommand saved = Command(connection, transaction, """
                    SELECT InstallationGeneration,AuthorityEpoch,CallerKey,ScopeKey,AfterSequence,SnapshotSequence,VisibilityKey
                    FROM guild_portal_cursors WHERE Id=$id
                    """);

                Add(saved, "$id", Id(cursorId));

                await using SqliteDataReader reader = await saved.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return TreeCursorInvalid;
                if (reader.GetString(2) != callerKey || reader.GetString(3) != scope) return TreeCursorInvalid;
                if (reader.GetString(6) != visibility.Value) return TreeCursorGone;
                if (reader.GetString(0) != Id(policy.InstallationGeneration) || reader.GetString(1) != Id(epoch.Value)) return TreeCursorGone;
                if (reader.GetValue(4) is not long position || reader.GetValue(5) is not long high || position < 0 || high < position) return Unavailable;

                after = position;
                snapshot = high;
            }
            else
            {
                await using SqliteCommand high = Command(connection, transaction,
                    "SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name='guild_operator_sequences'),0)");

                if (await high.ExecuteScalarAsync(token).ConfigureAwait(false) is not long value || value < 0) return Unavailable;

                snapshot = value;
            }

            foreach (Guid ownerId in new[] { query.RootId, query.ParentId }.OfType<Guid>())
            {
                Result<GuildQuestStored> owner = await ReadWithinAsync(connection, transaction, ownerId, token).ConfigureAwait(false);
                if (owner.IsFailure) return query.Cursor is null ? owner.Error : TreeCursorGone;
                Result visible = await RequireVisibleAsync(connection, transaction, caller, owner.Value, token).ConfigureAwait(false);
                if (visible.IsFailure) return query.Cursor is null ? visible.Error : TreeCursorGone;
            }

            ImmutableArray<GuildQuestDto>.Builder items = ImmutableArray.CreateBuilder<GuildQuestDto>();
            bool more = false;
            bool end = false;

            while (!end && !more)
            {
                List<(Guid Id, long Sequence)> batch = [];

                await using (SqliteCommand select = Command(connection, transaction, """
                    SELECT q.Id,s.Sequence FROM guild_operator_sequences s JOIN guild_quests q ON q.Id=s.ObjectId
                    WHERE s.KindCode=3 AND s.Sequence>$after AND s.Sequence<=$snapshot
                        AND ($root IS NULL OR q.RootQuestId=$root) AND ($parent IS NULL OR q.ParentQuestId=$parent)
                    ORDER BY s.Sequence LIMIT 100
                    """))
                {
                    Add(select, "$after", after);
                    Add(select, "$snapshot", snapshot);
                    Add(select, "$root", Id(query.RootId));
                    Add(select, "$parent", Id(query.ParentId));

                    await using SqliteDataReader reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);

                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (!Guid.TryParse(reader.GetString(0), out Guid id) || id == Guid.Empty || reader.GetValue(1) is not long sequence || sequence <= after) return Unavailable;
                        batch.Add((id, sequence));
                    }
                }

                end = batch.Count < 100;

                foreach ((Guid id, long sequence) in batch)
                {
                    Result<GuildQuestStored> row = await ReadWithinAsync(connection, transaction, id, token).ConfigureAwait(false);
                    if (row.IsFailure)
                    {
                        if (row.Error.Code is ErrorCodes.Guild.OwnerUnavailable or ErrorCodes.Guild.NotFound) { after = sequence; continue; }
                        return row.Error;
                    }

                    Result visible = await RequireVisibleAsync(connection, transaction, caller, row.Value, token).ConfigureAwait(false);
                    if (visible.IsFailure)
                    {
                        if (visible.Error.Code is ErrorCodes.Guild.OwnerUnavailable or ErrorCodes.Guild.NotFound or ErrorCodes.Guild.Forbidden) { after = sequence; continue; }
                        return visible.Error;
                    }

                    if (query.State is { } filter && row.Value.Quest.State != filter) { after = sequence; continue; }
                    if (items.Count == query.PageSize) { more = true; break; }

                    items.Add(row.Value.Quest with { Receipt = null });
                    GuildQuestPageDto candidate = new(items.ToImmutable(), new string('x', 32), snapshot);
                    if (JsonSerializer.SerializeToUtf8Bytes(candidate, GuildJsonContext.Default.GuildQuestPageDto).Length > TreePageMaximumBytes)
                    {
                        items.RemoveAt(items.Count - 1);
                        if (items.Count == 0) return new Error(ErrorCodes.Guild.PayloadTooLarge, "The Quest exceeds the page boundary.");
                        more = true;
                        break;
                    }

                    after = sequence;
                }
            }

            string? next = null;
            if (more)
            {
                Guid id = Guid.NewGuid();
                await using SqliteCommand insert = Command(connection, transaction, """
                    INSERT INTO guild_portal_cursors(Id,InstallationGeneration,AuthorityEpoch,CallerKey,RootQuestId,ScopeKey,
                        AfterSequence,SnapshotSequence,CreatedAtUtc,VisibilityKey)
                    VALUES($id,$generation,$epoch,$caller,$root,$scope,$after,$snapshot,$now,$visibility)
                    """);
                Add(insert, "$id", Id(id));
                Add(insert, "$generation", Id(policy.InstallationGeneration));
                Add(insert, "$epoch", Id(epoch.Value));
                Add(insert, "$caller", callerKey);
                Add(insert, "$root", Id(policy.InstallationGeneration));
                Add(insert, "$scope", scope);
                Add(insert, "$after", after);
                Add(insert, "$snapshot", snapshot);
                Add(insert, "$now", UtcInstantText.Format(time.GetUtcNow()));
                Add(insert, "$visibility", visibility.Value);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                next = id.ToString("N");
            }

            return new GuildQuestPageDto(items.ToImmutable(), next, snapshot);
        }, ct);
    }
}
