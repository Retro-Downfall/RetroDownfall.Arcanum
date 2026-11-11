using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    private static Error MarketNotFound => new(ErrorCodes.Guild.NotFound, "The work offer or bid is unavailable.");

    private static Error MarketCursorInvalid => new(ErrorCodes.Guild.CursorInvalid, "The work cursor does not match this caller and scope.");

    private static Error MarketCursorGone => new(ErrorCodes.Guild.CursorGone, "The work cursor owner or installation authority is unavailable.");

    private Task<Result<Guid>> OfferRootAsync(GuildCallerContext caller, Guid offer, CancellationToken ct) =>
        caller.Kind == GuildCallerKind.Run && caller.Execution is { } execution ? Task.FromResult(Result<Guid>.Success(execution.RootQuestId))
            : caller.Kind == GuildCallerKind.Operator ? guild.FindOfferRootAsync(caller, policy, offer, ct)
            : Task.FromResult(Result<Guid>.Failure(Forbidden));

    internal async Task<Result<WorkOfferDto>> GetOfferAsync(GuildCallerContext caller, Guid offerId, CancellationToken ct)
    {
        if (caller is null || offerId == Guid.Empty) return new Error(ErrorCodes.Guild.InvalidRequest, "The work offer identity is invalid.");

        var root = await OfferRootAsync(caller, offerId, ct).ConfigureAwait(false);

        if (root.IsFailure) return root.Error;

        return await authority.InQuestTransactionAsync<WorkOfferDto>(root.Value, async (connection, transaction, signets, token) =>
        {
            var scope = await OfferScopeWithinAsync(connection, transaction, offerId, token).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;

            var cohort = await ReadWithinAsync(connection, transaction, scope.Value.Cohort, token).ConfigureAwait(false);

            if (cohort.IsFailure) return cohort.Error;

            if (cohort.Value.Cohort.RootQuestId != root.Value) return Forbidden;

            var actor = await AuthorizeNoteWithinAsync(connection, transaction, signets, caller, cohort.Value.Cohort, false, token).ConfigureAwait(false);

            if (actor.IsFailure) return actor.Error;

            if (!await CanReadOfferWithinAsync(connection, transaction, caller, actor.Value, offerId, scope.Value.Issuer, token).ConfigureAwait(false)) return MarketNotFound;

            return await ReadOfferWithinAsync(connection, transaction, offerId, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal Task<Result<WorkOfferPageDto>> ListOffersAsync(GuildCallerContext caller, Guid cohortId, GuildWorkOfferState? state,
        GuildPageQuery query, CancellationToken ct) => ReadMarketPageAsync(caller, cohortId, false, state, query,
            (items, cursor, snapshot) => new WorkOfferPageDto(items, cursor, snapshot),
            ReadOfferWithinAsync, GuildJsonContext.Default.WorkOfferPageDto, ct);

    internal Task<Result<WorkBidPageDto>> ListBidsAsync(GuildCallerContext caller, Guid offerId, GuildPageQuery query,
        CancellationToken ct) => ReadMarketPageAsync(caller, offerId, true, null, query,
            (items, cursor, snapshot) => new WorkBidPageDto(items, cursor, snapshot),
            ReadBidWithinAsync, GuildJsonContext.Default.WorkBidPageDto, ct);

    private async Task<Result<TPage>> ReadMarketPageAsync<T, TPage>(GuildCallerContext caller, Guid parent, bool bids,
        GuildWorkOfferState? state, GuildPageQuery query, Func<ImmutableArray<T>, string?, long, TPage> project,
        Func<SqliteConnection, SqliteTransaction, Guid, CancellationToken, Task<Result<T>>> read,
        JsonTypeInfo<TPage> json, CancellationToken ct)
    {
        if (caller is null || parent == Guid.Empty || query is null || query.PageSize is < 1 or > 100
            || state is { } filter && !Enum.IsDefined(filter)) return new Error(ErrorCodes.Guild.InvalidRequest, "The work page is invalid.");

        var root = await (bids ? OfferRootAsync(caller, parent, ct) : NoteRootAsync(caller, parent, ct)).ConfigureAwait(false);

        if (root.IsFailure) return root.Error;

        return await authority.InQuestTransactionAsync<TPage>(root.Value, async (connection, transaction, signets, token) =>
        {
            Guid cohortId = parent;
            Guid issuer = Guid.Empty;

            if (bids)
            {
                var offer = await OfferScopeWithinAsync(connection, transaction, parent, token).ConfigureAwait(false);

                if (offer.IsFailure) return offer.Error;

                cohortId = offer.Value.Cohort;
                issuer = offer.Value.Issuer;
            }

            string scope = (bids ? "bids:" : "offers:") + Id(parent) + ":" + (state is null ? "all" : ((int)state.Value).ToString(CultureInfo.InvariantCulture))
                + ":" + query.PageSize.ToString(CultureInfo.InvariantCulture);
            string callerKey = NoteReaderKey(caller);
            Guid cursorId = Guid.Empty;

            if (query.Cursor is not null)
            {
                if (query.Cursor.Length != 32 || !Guid.TryParseExact(query.Cursor, "N", out cursorId) || cursorId == Guid.Empty) return MarketCursorInvalid;

                var authenticated = await policy.AuthorizeLocateAsync(caller, connection, transaction, caller.Execution?.QuestId, null, null, token).ConfigureAwait(false);

                if (authenticated.IsFailure) return authenticated.Error;

                await using var binding = Command(connection, transaction, """
                    SELECT EXISTS(SELECT 1 FROM guild_portal_cursors WHERE Id=$id AND CallerKey=$caller
                        AND RootQuestId=$root AND ScopeKey=$scope AND VisibilityKey=$cohort)
                    """);

                Add(binding, "$id", Id(cursorId));
                Add(binding, "$caller", callerKey);
                Add(binding, "$root", Id(root.Value));
                Add(binding, "$scope", scope);
                Add(binding, "$cohort", Id(cohortId));

                if (!Equals(1L, await binding.ExecuteScalarAsync(token).ConfigureAwait(false))) return MarketCursorInvalid;
            }

            var cohort = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

            if (cohort.IsFailure) return query.Cursor is not null && cohort.Error.Code == ErrorCodes.Guild.OwnerUnavailable ? MarketCursorGone : cohort.Error;

            if (cohort.Value.Cohort.RootQuestId != root.Value) return Forbidden;

            var actor = await AuthorizeNoteWithinAsync(connection, transaction, signets, caller, cohort.Value.Cohort, false, token).ConfigureAwait(false);

            if (actor.IsFailure) return query.Cursor is not null && actor.Error.Code == ErrorCodes.Guild.OwnerUnavailable ? MarketCursorGone : actor.Error;

            if (bids && !await CanReadOfferWithinAsync(connection, transaction, caller, actor.Value, parent, issuer, token).ConfigureAwait(false)) return MarketNotFound;

            var epoch = await GuildSignetStore.EpochAsync(connection, transaction, token).ConfigureAwait(false);

            if (epoch.IsFailure) return epoch.Error;

            long after = 0;
            long snapshot;

            if (query.Cursor is not null)
            {
                await using var cursor = Command(connection, transaction, """
                    SELECT AfterSequence,SnapshotSequence FROM guild_portal_cursors WHERE Id=$id
                        AND InstallationGeneration=$generation AND AuthorityEpoch=$epoch
                        AND CallerKey=$caller AND ScopeKey=$scope AND RootQuestId=$root AND VisibilityKey=$cohort
                    """);

                Add(cursor, "$id", Id(cursorId));
                Add(cursor, "$generation", Id(policy.InstallationGeneration));
                Add(cursor, "$epoch", Id(epoch.Value));
                Add(cursor, "$caller", callerKey);
                Add(cursor, "$scope", scope);
                Add(cursor, "$root", Id(root.Value));
                Add(cursor, "$cohort", Id(cohortId));

                await using var reader = await cursor.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return MarketCursorGone;

                if (reader.GetValue(0) is not long coordinate || reader.GetValue(1) is not long high || coordinate < 0 || high < coordinate) return Unavailable;

                after = coordinate;
                snapshot = high;
            }
            else
            {
                await using var high = Command(connection, transaction, bids
                    ? "SELECT BidSequence FROM guild_work_offers WHERE Id=$parent"
                    : "SELECT OfferSequence FROM guild_cohorts WHERE Id=$parent");

                Add(high, "$parent", Id(parent));

                if (await high.ExecuteScalarAsync(token).ConfigureAwait(false) is not long value || value < 0) return Unavailable;

                snapshot = value;
            }

            bool allBids = caller.Kind == GuildCallerKind.Operator || actor.Value.Source.Quest.Id == issuer;
            string sql = bids ? """
                SELECT CASE WHEN typeof(b.Id)='text' AND length(CAST(b.Id AS BLOB))=36 THEN b.Id END,b.BidSequence FROM guild_work_bids b
                WHERE b.OfferId=$parent AND b.BidSequence>$after AND b.BidSequence<=$snapshot
                    AND ($all=1 OR b.MemberId=$member)
                ORDER BY b.BidSequence,b.Id LIMIT $limit
                """ : """
                SELECT CASE WHEN typeof(o.Id)='text' AND length(CAST(o.Id AS BLOB))=36 THEN o.Id END,o.OfferSequence FROM guild_work_offers o
                WHERE o.CohortId=$parent AND o.OfferSequence>$after AND o.OfferSequence<=$snapshot
                    AND ($state IS NULL OR o.StateCode=$state)
                    AND ($operator=1 OR o.IssuerQuestId=$quest OR EXISTS(SELECT 1 FROM guild_work_offer_recipients r
                        WHERE r.OfferId=o.Id AND r.MemberId=$member))
                ORDER BY o.OfferSequence,o.Id LIMIT $limit
                """;
            List<(Guid Id, long Sequence)> rows = [];

            await using (var page = Command(connection, transaction, sql))
            {
                Add(page, "$parent", Id(parent));
                Add(page, "$after", after);
                Add(page, "$snapshot", snapshot);
                Add(page, "$limit", query.PageSize);
                Add(page, "$all", allBids ? 1 : 0);
                Add(page, "$member", actor.Value.Member is { } member ? Id(member) : null);
                Add(page, "$state", state is { } s ? (int)s : null);
                Add(page, "$operator", caller.Kind == GuildCallerKind.Operator ? 1 : 0);
                Add(page, "$quest", Id(actor.Value.Source.Quest.Id));

                await using var reader = await page.ExecuteReaderAsync(token).ConfigureAwait(false);

                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (reader.GetValue(0) is not string id || id.Length != 36 || !Guid.TryParseExact(id, "D", out Guid record)
                        || record == Guid.Empty || reader.GetValue(1) is not long sequence || sequence <= after || sequence > snapshot) return Unavailable;

                    rows.Add((record, sequence));
                }
            }

            ImmutableArray<T>.Builder items = ImmutableArray.CreateBuilder<T>();
            long last = after;
            bool more = false;

            foreach (var row in rows.Take(query.PageSize))
            {
                var item = await read(connection, transaction, row.Id, token).ConfigureAwait(false);

                if (item.IsFailure)
                {
                    if (item.Error != MarketProtectedContent) return item.Error;

                    // This row was authorized and its metadata checked, but current protected
                    // content cannot supply a new reference. Advance the bounded scan only.
                    last = row.Sequence;
                    continue;
                }

                if (JsonSerializer.SerializeToUtf8Bytes(project(items.ToImmutable().Add(item.Value), new string('0', 32), snapshot), json).Length > 1_048_576)
                {
                    if (items.Count == 0) return new Error(ErrorCodes.Guild.PayloadTooLarge, "One work item exceeds the page allocation.");

                    more = true;
                    break;
                }

                items.Add(item.Value);
                last = row.Sequence;
            }

            if (!more)
            {
                await using var remaining = Command(connection, transaction, "SELECT EXISTS(" + sql + ")");

                Add(remaining, "$parent", Id(parent));
                Add(remaining, "$after", last);
                Add(remaining, "$snapshot", snapshot);
                Add(remaining, "$limit", 1);
                Add(remaining, "$all", allBids ? 1 : 0);
                Add(remaining, "$member", actor.Value.Member is { } member ? Id(member) : null);
                Add(remaining, "$state", state is { } s ? (int)s : null);
                Add(remaining, "$operator", caller.Kind == GuildCallerKind.Operator ? 1 : 0);
                Add(remaining, "$quest", Id(actor.Value.Source.Quest.Id));

                more = Equals(1L, await remaining.ExecuteScalarAsync(token).ConfigureAwait(false));
            }

            string? next = null;

            if (more)
            {
                Guid nextId = Guid.NewGuid();

                await using var insert = Command(connection, transaction, """
                    INSERT INTO guild_portal_cursors(Id,InstallationGeneration,AuthorityEpoch,CallerKey,RootQuestId,ScopeKey,
                        AfterSequence,SnapshotSequence,CreatedAtUtc,VisibilityKey)
                    VALUES($id,$generation,$epoch,$caller,$root,$scope,$after,$snapshot,$now,$cohort)
                    """);

                Add(insert, "$id", Id(nextId));
                Add(insert, "$generation", Id(policy.InstallationGeneration));
                Add(insert, "$epoch", Id(epoch.Value));
                Add(insert, "$caller", callerKey);
                Add(insert, "$root", Id(root.Value));
                Add(insert, "$scope", scope);
                Add(insert, "$after", last);
                Add(insert, "$snapshot", snapshot);
                Add(insert, "$now", UtcInstantText.Format(time.GetUtcNow()));
                Add(insert, "$cohort", Id(cohortId));

                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                next = nextId.ToString("N");
            }

            return project(items.ToImmutable(), next, snapshot);
        }, ct).ConfigureAwait(false);
    }

    private static async Task<bool> CanReadOfferWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, NoteActor actor, Guid offer, Guid issuer, CancellationToken ct)
    {
        if (caller.Kind == GuildCallerKind.Operator || actor.Source.Quest.Id == issuer) return true;

        if (actor.Member is not { } member) return false;

        await using var query = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM guild_work_offer_recipients WHERE OfferId=$offer AND MemberId=$member)");

        Add(query, "$offer", Id(offer));
        Add(query, "$member", Id(member));

        return Equals(1L, await query.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static async Task<Result<(Guid Cohort, Guid Issuer)>> OfferScopeWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid offer, CancellationToken ct)
    {
        await using var query = Command(connection, transaction, """
            SELECT CohortId,IssuerQuestId FROM guild_work_offers WHERE Id=$offer
                AND typeof(CohortId)='text' AND length(CAST(CohortId AS BLOB))=36
                AND typeof(IssuerQuestId)='text' AND length(CAST(IssuerQuestId AS BLOB))=36
            """);

        Add(query, "$offer", Id(offer));

        await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) && Guid.TryParseExact(reader.GetString(0), "D", out Guid cohort)
            && Guid.TryParseExact(reader.GetString(1), "D", out Guid issuer) && cohort != Guid.Empty && issuer != Guid.Empty
            ? (cohort, issuer) : MarketNotFound;
    }
}
