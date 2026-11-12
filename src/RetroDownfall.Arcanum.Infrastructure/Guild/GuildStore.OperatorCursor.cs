using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildStore
{
    internal Task<Result<Guid>> AuthenticateOperatorCursorAsync(GuildCallerContext caller, IGuildQuestHostPolicy policy,
        string cursor, string callerKey, string scope, bool selectionSuffix, CancellationToken ct)
    {
        Error invalid = new(ErrorCodes.Guild.CursorInvalid, "The cursor does not match this caller and scope.");

        if (cursor.Length != 32 || !Guid.TryParseExact(cursor, "N", out Guid id) || id == Guid.Empty)
            return Task.FromResult(Result<Guid>.Failure(invalid));

        return ExecuteAsync<Guid>(false, async (connection, transaction, token) =>
        {
            Result authenticated = await policy.AuthorizeLocateAsync(caller, connection, transaction, null, null, null, token).ConfigureAwait(false);

            if (authenticated.IsFailure) return authenticated.Error;

            await using SqliteCommand command = Command(connection, transaction,
                "SELECT RootQuestId,ScopeKey,CallerKey FROM guild_portal_cursors WHERE Id=$id");

            Add(command, "$id", Format(id));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(2) != callerKey) return invalid;

            string savedScope = reader.GetString(1);

            if (selectionSuffix)
            {
                string prefix = scope + ":";

                if (!savedScope.StartsWith(prefix, StringComparison.Ordinal)
                    || !long.TryParse(savedScope.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long generation)
                    || generation < 0) return invalid;
            }
            else if (savedScope != scope) return invalid;

            if (!Guid.TryParse(reader.GetString(0), out Guid root) || root == Guid.Empty) return Unavailable();

            return root;
        }, ct);
    }
}
