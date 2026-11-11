using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildStore
{
    internal Task<Result<GuildCohortWorkPage>> ReadCohortWorkPageAsync(GuildCallerContext caller,
        IGuildQuestHostPolicy policy, GuildCohortWorkCursor? after, CancellationToken ct) =>
        ExecuteAsync<GuildCohortWorkPage>(false, async (connection, transaction, token) =>
        {
            Result allowed = await policy.AuthorizeLocateAsync(caller, connection, transaction, null, null, null, token).ConfigureAwait(false);

            if (allowed.IsFailure) return allowed.Error;

            int phase = after?.Phase ?? 1;

            if (phase is < 1 or > 4 || after?.LastId == Guid.Empty || after?.LastFrontierId == Guid.Empty
                || phase != 4 && after?.LastFrontierId is not null
                || phase == 4 && (after?.LastId is null) != (after?.LastFrontierId is null)) return Invalid();

            await using SqliteCommand command = Command(connection, transaction, phase < 4 ? """
                SELECT c.Id,NULL FROM guild_cohorts c INDEXED BY idx_guild_cohorts_state
                JOIN guild_signets s ON s.QuestId=c.IssuerQuestId
                WHERE c.StateCode=$phase AND c.Id>$after COLLATE BINARY AND s.OriginPrincipalId=$principal
                ORDER BY c.Id COLLATE BINARY LIMIT 100
                """ : """
                SELECT f.CohortId,f.Id FROM guild_cohort_stop_frontiers f INDEXED BY idx_guild_cohort_stop_frontiers_pending
                JOIN guild_cohorts c ON c.Id=f.CohortId JOIN guild_signets s ON s.QuestId=c.IssuerQuestId
                WHERE f.Completed=0 AND (f.CohortId>$after COLLATE BINARY
                    OR (f.CohortId=$after AND f.Id>$frontier COLLATE BINARY)) AND s.OriginPrincipalId=$principal
                ORDER BY f.CohortId COLLATE BINARY,f.Id COLLATE BINARY LIMIT 100
                """);

            Add(command, "$principal", Format(caller.PrincipalId!.Value));
            Add(command, "$phase", phase);
            Add(command, "$after", after?.LastId is { } value ? Format(value) : "");
            Add(command, "$frontier", after?.LastFrontierId is { } frontier ? Format(frontier) : "");

            ImmutableArray<Guid>.Builder items = ImmutableArray.CreateBuilder<Guid>();
            Guid? lastFrontier = null;

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid id) || id == Guid.Empty) return Unavailable();

                if (phase == 4)
                {
                    if (reader.IsDBNull(1) || !Guid.TryParseExact(reader.GetString(1), "D", out Guid frontierId) || frontierId == Guid.Empty)
                        return Unavailable();

                    lastFrontier = frontierId;
                }

                items.Add(id);
            }

            GuildCohortWorkCursor? next = items.Count == 100
                ? new(phase, items[^1], lastFrontier)
                : phase < 4 ? new(phase + 1, null, null) : null;

            return new GuildCohortWorkPage(items.ToImmutable(), next);
        }, ct);

    internal Task<Result<Guid>> FindCohortRootAsync(GuildCallerContext caller, IGuildQuestHostPolicy policy,
        Guid cohortId, CancellationToken ct) => ExecuteAsync<Guid>(false, async (connection, transaction, token) =>
        {
            Result allowed = await policy.AuthorizeLocateAsync(caller, connection, transaction, null, null, null, token).ConfigureAwait(false);

            if (allowed.IsFailure) return allowed.Error;

            await using SqliteCommand command = Command(connection, transaction, """
                SELECT c.RootQuestId FROM guild_cohorts c JOIN guild_signets s ON s.QuestId=c.IssuerQuestId
                WHERE c.Id=$cohort AND s.OriginPrincipalId=$principal
                """);

            Add(command, "$cohort", Format(cohortId));
            Add(command, "$principal", Format(caller.PrincipalId!.Value));

            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string text
                && Guid.TryParseExact(text, "D", out Guid root) && root != Guid.Empty
                ? Result<Guid>.Success(root) : Result<Guid>.Failure(Unavailable());
        }, ct);
}
