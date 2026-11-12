using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildOperatorRosterVisibility(Guid Epoch, Guid Stamp);

internal sealed partial class GuildStore
{
    internal Task<Result<GuildStoredCardPage>> ListOperatorCardsAsync(GuildLocalHostPolicy policy, GuildCallerContext caller,
        GuildAgentSourceKind? source, string? skill, string? after, int size, long? snapshot, CancellationToken ct) =>
        ExecuteAsync<GuildStoredCardPage>(false, (connection, transaction, token) =>
            !policy.Authenticates(caller) || caller.Kind != GuildCallerKind.Operator
                ? Task.FromResult(Result<GuildStoredCardPage>.Failure(new(ErrorCodes.Guild.Forbidden, "Current Operator inspection is required.")))
                : ListWithinAsync(connection, transaction, new(null, null, null), source, skill, after, size, token,
                    snapshot, operatorInspection: true), ct);

    internal Task<Result<GuildOperatorRosterVisibility>> ReadOperatorRosterVisibilityAsync(GuildLocalHostPolicy policy,
        GuildCallerContext caller, CancellationToken ct) => ExecuteAsync<GuildOperatorRosterVisibility>(false,
        async (connection, transaction, token) =>
        {
            if (!policy.Authenticates(caller) || caller.Kind != GuildCallerKind.Operator)
                return new Error(ErrorCodes.Guild.Forbidden, "Current Operator inspection is required.");

            Result<Guid> epoch = await GuildSignetStore.EpochAsync(connection, transaction, token).ConfigureAwait(false);

            if (epoch.IsFailure) return epoch.Error;

            await using SqliteCommand clocks = Command(connection, transaction, """
                SELECT COALESCE((SELECT CASE WHEN typeof(Revision)='integer' AND Revision>=1 THEN Revision ELSE -1 END
                    FROM guild_cohort_content_clock WHERE Id=1),0),
                    COALESCE((SELECT CASE WHEN typeof(Revision)='integer' AND Revision>=1 THEN Revision ELSE -1 END
                    FROM artifact_sensitivity_clock WHERE Id=1),0),
                    COALESCE((SELECT MAX(Sequence) FROM owner_deletion_events),0)
                """);

            await using SqliteDataReader reader = await clocks.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetValue(0) is not long content || content < 0
                || reader.GetValue(1) is not long labels || labels < 0 || reader.GetValue(2) is not long owners || owners < 0)
                return new Error(ErrorCodes.Guild.Unavailable, "The Roster visibility evidence is unavailable.");

            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{content}:{labels}:{owners}")));

            return new GuildOperatorRosterVisibility(epoch.Value, new Guid(digest.AsSpan(0, 16)));
        }, ct);
}
