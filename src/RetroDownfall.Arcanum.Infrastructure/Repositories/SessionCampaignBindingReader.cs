using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// Reads the exactly-one immutable Campaign binding of a Session.
/// </summary>
/// <remarks>
/// Raw parameterized SQL against the scoped Grimoire connection rather than an EF query, matching the
/// rest of the Covenant tier: this read sits on the turn hot path in front of every session-backed
/// request, and the binding table has no EF entity by design (§10.12).
///
/// <para>A Session row with no binding row is an integrity failure, not an implicit Global Session. The
/// backfill runs inside the core install transaction, so a missing row after startup means something
/// wrote a Session outside that path — and treating it as Global would be inventing an authority answer
/// nobody recorded.</para>
/// </remarks>
internal sealed class SessionCampaignBindingReader(ICovenantConnectionSource connections)
    : ISessionCampaignBindingReader
{

    private const string Sql = """
        SELECT b.BindingKindCode,
               b.CampaignId
        FROM session_campaign_bindings AS b
        WHERE b.SessionId = $sessionId;
        """;

    private const string SessionExistsSql = """
        SELECT 1
        FROM "Sessions"
        WHERE "Id" = $sessionId;
        """;

    public async ValueTask<Result<SessionCampaignBindingRecord?>> FindAsync(
        Guid? sessionId,
        CancellationToken cancellationToken)
    {

        if (sessionId is not { } id || id == Guid.Empty)
        {
            return Result<SessionCampaignBindingRecord?>.Success(null);
        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = Sql;

        _ = command.Parameters.AddWithValue("$sessionId", id.ToString());

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            await reader.CloseAsync().ConfigureAwait(false);

            // Distinguish "no such Session" from "Session without a binding". The first is an ordinary
            // client error; the second is corruption, and reporting it as a missing Session would send
            // an operator looking for the wrong problem.
            return await SessionExistsAsync(connection, id, cancellationToken).ConfigureAwait(false)
                ? Result<SessionCampaignBindingRecord?>.Failure(
                    new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "This Session has no immutable Campaign binding."))
                : Result<SessionCampaignBindingRecord?>.Failure(
                    new Error(ErrorCodes.Session.NotFound, "Session not found."));

        }

        long kindCode = reader.GetInt64(0);

        Guid? campaignId = reader.IsDBNull(1)
            ? null
            : Guid.Parse(reader.GetString(1));

        if (kindCode is < 1 or > 3)
        {
            return Result<SessionCampaignBindingRecord?>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "This Session's Campaign binding is malformed."));
        }

        return Result<SessionCampaignBindingRecord?>.Success(
            new SessionCampaignBindingRecord(
                id,
                SessionCampaignBinding.Create((SessionCampaignBindingKind)kindCode, campaignId)));

    }

    private static async Task<bool> SessionExistsAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = SessionExistsSql;

        _ = command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;

    }

}
