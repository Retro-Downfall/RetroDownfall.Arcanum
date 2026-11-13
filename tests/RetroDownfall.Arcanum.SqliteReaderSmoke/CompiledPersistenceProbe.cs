using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Security;
using SQLitePCL;

namespace RetroDownfall.Arcanum.SqliteReaderSmoke;

internal static class CompiledPersistenceProbe
{
    internal static void Run(bool requireNative)
    {
        if (requireNative && RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new InvalidDataException("The persistence proof must execute as Native AOT.");
        }

        SqliteNativeRuntime.Instance.Initialize();

        string directory = Path.Combine(Path.GetTempPath(), "arcanum-persistence-probe-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            VerifyCompiledModel(Path.Combine(directory, "model.db"));

            VerifyNativeKeyAndRekey(Path.Combine(directory, "rekey.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyCompiledModel(string path)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Password = "synthetic-compiled-model-key",
            Pooling = false,
        }.ToString());

        connection.Open();

        DbContextOptionsBuilder<ArcanumDbContext> options = new();

        options.UseSqlite(connection).UseModel(ArcanumDbContextModel.Instance);

        using ArcanumDbContext context = new(options.Options, null!, new GrimoireDbPassphraseSource());

        foreach (string table in new[] { "Campaigns", "Sessions", "Entries" })
        {
            using Stream definition = typeof(ArcanumDbContext).Assembly.GetManifestResourceStream(
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.Tables." + table + ".sql")
                ?? throw new InvalidDataException("The canonical table definition is missing.");

            using StreamReader source = new(definition);

            using SqliteCommand create = connection.CreateCommand();

            create.CommandText = source.ReadToEnd();

            create.ExecuteNonQuery();
        }

        Guid sessionId = Guid.Parse("043f3561-1ba2-40f7-943f-e9bfe9daf567");

        Campaign campaign = new()
        {
            Id = Guid.Parse("952d9df8-8176-42fa-8722-505cc596a512"),
            Name = "Native persistence probe",
            NameLower = "native persistence probe",
            Path = "synthetic-native-persistence-probe",
            Type = WorkspaceType.Custom,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

        context.Campaigns.Add(campaign);

        Session session = new()
        {
            Id = sessionId,
            CampaignId = campaign.Id,
            CreatedAt = new DateTimeOffset(2026, 9, 8, 23, 0, 0, TimeSpan.FromHours(-4)).AddTicks(7),
            UpdatedAt = new DateTimeOffset(2026, 9, 9, 5, 0, 0, TimeSpan.FromHours(2)).AddTicks(8),
            LastSummarizedMessageAt = new DateTime(2026, 9, 9, 3, 0, 0, DateTimeKind.Unspecified).AddTicks(9),
            TotalCostUsd = 1234567890.1234567890123456789m,
        };

        context.Sessions.Add(session);

        Entry entry = new()
        {
            Id = Guid.Parse("072e891e-ec39-4d15-9a6f-734f445496bb"),
            SessionId = sessionId,
            Role = MessageRole.Assistant,
            Content = "native-callback-and-model",
            CreatedAt = session.CreatedAt,
        };

        context.Entries.Add(entry);

        context.SaveChanges();

        entry.Role = MessageRole.Tool;

        campaign.Type = WorkspaceType.Data;

        if (context.SaveChanges() != 2)
        {
            throw new InvalidDataException("Typed enum snapshots did not detect both tracked updates.");
        }

        connection.CreateFunction<long, long>("probe_increment", static value => value + 1);

        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT s.Id,s.CreatedAt,s.UpdatedAt,s.LastSummarizedMessageAt,s.TotalCostUsd,typeof(s.TotalCostUsd),e.Role,probe_increment(41),c.Type FROM Sessions s JOIN Entries e ON e.SessionId=s.Id JOIN Campaigns c ON c.Id=s.CampaignId;";

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read()
            || reader.GetGuid(0) != sessionId
            || reader.GetString(0) != "043F3561-1BA2-40F7-943F-E9BFE9DAF567"
            || reader.GetString(1) != "2026-09-09T03:00:00.0000007Z"
            || reader.GetString(2) != "2026-09-09T03:00:00.0000008Z"
            || reader.GetString(3) != "2026-09-09T03:00:00.0000009Z"
            || reader.GetString(4) != "1234567890.1234567890123456789"
            || reader.GetString(5) != "text"
            || reader.GetInt32(6) != (int)MessageRole.Tool
            || reader.GetInt64(7) != 42
            || reader.GetInt32(8) != (int)WorkspaceType.Data
            || reader.Read())
        {
            throw new InvalidDataException("The compiled model changed exact persistence or native callback behavior.");
        }
    }

    private static void VerifyNativeKeyAndRekey(string path)
    {
        if (raw.sqlite3_open(path, out sqlite3 database) != raw.SQLITE_OK)
        {
            throw new InvalidDataException("The native key probe could not open scratch storage.");
        }

        using (database)
        {
            if (raw.sqlite3_key(database, Encoding.UTF8.GetBytes("synthetic-before-rekey")) != raw.SQLITE_OK
                || raw.sqlite3_exec(database, "CREATE TABLE probe(Value INTEGER); INSERT INTO probe VALUES(73);") != raw.SQLITE_OK
                || raw.sqlite3_rekey(database, Encoding.UTF8.GetBytes("synthetic-after-rekey")) != raw.SQLITE_OK)
            {
                throw new InvalidDataException("The native key/rekey entry points failed.");
            }
        }

        byte[] prefix = File.ReadAllBytes(path).Take(16).ToArray();

        if (prefix.AsSpan().SequenceEqual("SQLite format 3\0"u8))
        {
            throw new InvalidDataException("Scratch storage was written without encryption.");
        }

        foreach (string key in new[] { "synthetic-after-rekey", "synthetic-before-rekey", "synthetic-wrong-key" })
        {
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Password = key,
                Pooling = false,
            }.ToString());

            try
            {
                connection.Open();

                using SqliteCommand command = connection.CreateCommand();

                command.CommandText = "SELECT Value FROM probe;";

                if (command.ExecuteScalar() is not long value || value != 73 || key != "synthetic-after-rekey")
                {
                    throw new InvalidDataException("Rekey did not preserve data or reject the previous key.");
                }
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == raw.SQLITE_NOTADB && key != "synthetic-after-rekey")
            {
                continue;
            }
        }
    }
}
