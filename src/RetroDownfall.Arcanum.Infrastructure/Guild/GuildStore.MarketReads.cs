using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildStore
{
    internal Task<Result<Guid>> FindOfferRootAsync(GuildCallerContext caller, IGuildQuestHostPolicy policy,
        Guid offerId, CancellationToken ct) => ExecuteAsync<Guid>(false, async (connection, transaction, token) =>
        {
            var allowed = await policy.AuthorizeLocateAsync(caller, connection, transaction, null, null, null, token).ConfigureAwait(false);

            if (allowed.IsFailure) return allowed.Error;

            await using SqliteCommand command = Command(connection, transaction, """
                SELECT c.RootQuestId FROM guild_work_offers o JOIN guild_cohorts c ON c.Id=o.CohortId
                JOIN guild_signets s ON s.QuestId=c.IssuerQuestId
                WHERE o.Id=$offer AND s.OriginPrincipalId=$principal
                    AND typeof(c.RootQuestId)='text' AND length(CAST(c.RootQuestId AS BLOB))=36
                """);

            Add(command, "$offer", Format(offerId));
            Add(command, "$principal", caller.PrincipalId is { } principal ? Format(principal) : null);

            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string text
                && Guid.TryParseExact(text, "D", out Guid root) && root != Guid.Empty
                ? Result<Guid>.Success(root) : new Error(ErrorCodes.Guild.NotFound, "The work offer is unavailable.");
        }, ct);
}
