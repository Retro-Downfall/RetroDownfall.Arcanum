using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class SessionContextPinStore(ArcanumDbContext db, TimeProvider timeProvider)
    : ISessionContextPinStore
{
    public async Task<IReadOnlyList<SessionContextPinRecord>> ListAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = CreateCommand();
        command.CommandText = """
            SELECT "Id", "SessionId", "Kind", "TargetIdentifier", "DisplayLabel",
                   "ContentVersion", "CreatedAt", "UpdatedAt"
            FROM "SessionContextPins"
            WHERE "SessionId" = $sessionId
            ORDER BY "CreatedAt", "Id";
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        List<SessionContextPinRecord> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    public async Task<SessionContextPinRecord> UpsertAsync(
        Guid sessionId,
        SessionContextPinKind kind,
        string targetIdentifier,
        string displayLabel,
        string? contentVersion,
        CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();
        DateTimeOffset now = timeProvider.GetUtcNow();
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = CreateCommand();
        command.CommandText = """
            INSERT INTO "SessionContextPins"
                ("Id", "SessionId", "Kind", "TargetIdentifier", "DisplayLabel",
                 "ContentVersion", "CreatedAt", "UpdatedAt")
            VALUES ($id, $sessionId, $kind, $target, $label, $version, $now, $now)
            ON CONFLICT ("SessionId", "Kind", "TargetIdentifier") DO UPDATE SET
                "DisplayLabel" = excluded."DisplayLabel",
                "ContentVersion" = excluded."ContentVersion",
                "UpdatedAt" = excluded."UpdatedAt"
            RETURNING "Id", "SessionId", "Kind", "TargetIdentifier", "DisplayLabel",
                      "ContentVersion", "CreatedAt", "UpdatedAt";
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$target", targetIdentifier);
        command.Parameters.AddWithValue("$label", displayLabel);
        command.Parameters.AddWithValue("$version", (object?)contentVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Read(reader);
    }

    public async Task<bool> DeleteAsync(
        Guid sessionId,
        Guid pinId,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = CreateCommand();
        command.CommandText = """
            DELETE FROM "SessionContextPins"
            WHERE "SessionId" = $sessionId AND "Id" = $id;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$id", pinId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private SqliteCommand CreateCommand() =>
        (SqliteCommand)db.Database.GetDbConnection().CreateCommand();

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static SessionContextPinRecord Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        (SessionContextPinKind)reader.GetInt32(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture));
}
