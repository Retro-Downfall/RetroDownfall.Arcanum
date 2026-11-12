using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal static class GuildOperatorVisibility
{
    internal static async Task<Result<string>> ReadAsync(SqliteConnection connection, SqliteTransaction transaction, object policy, CancellationToken ct)
    {
        string visibility = policy is IGuildPortalDisclosurePolicy disclosure ? disclosure.VisibilityEpoch : "operator-metadata-v1";

        if (string.IsNullOrWhiteSpace(visibility) || Encoding.UTF8.GetByteCount(visibility) > 65_536)
            return new Error(ErrorCodes.Guild.Unavailable, "Guild visibility is unavailable.");

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT COALESCE((SELECT CASE WHEN typeof(Revision)='integer' AND Revision>=1 THEN Revision ELSE -1 END
                FROM guild_cohort_content_clock WHERE Id=1),0),
                COALESCE((SELECT CASE WHEN typeof(Revision)='integer' AND Revision>=1 THEN Revision ELSE -1 END
                FROM artifact_sensitivity_clock WHERE Id=1),0),
                COALESCE((SELECT MAX(Sequence) FROM owner_deletion_events),0)
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetValue(0) is not long content || content < 0
            || reader.GetValue(1) is not long sensitivity || sensitivity < 0 || reader.GetValue(2) is not long owners || owners < 0)
            return new Error(ErrorCodes.Guild.Unavailable, "Guild visibility is unavailable.");

        string value = visibility + ":" + content.ToString(CultureInfo.InvariantCulture) + ":"
            + sensitivity.ToString(CultureInfo.InvariantCulture) + ":" + owners.ToString(CultureInfo.InvariantCulture);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    internal static string Bind(string owner, string visibility) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner + ":" + visibility)));
}
