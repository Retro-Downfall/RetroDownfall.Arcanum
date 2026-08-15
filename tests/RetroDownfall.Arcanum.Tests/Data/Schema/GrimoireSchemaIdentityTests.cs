using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The schema identity that replaced <c>__EFMigrationsHistory</c>. It is what <c>.arcbackup</c>
/// records and re-checks (§5.4.8), and what tells an operator a local database no longer matches the
/// canonical schema and should be reinstalled (README, "Local Grimoire reinstall").
/// </summary>
public sealed class GrimoireSchemaIdentityTests
{

    private const int Dimensions = 1536;

    [Fact]
    public async Task Identity_is_deterministic_for_the_same_installed_schema()
    {

        await using SqliteConnection first = await InstallAsync();

        await using SqliteConnection second = await InstallAsync();

        string firstIdentity = await GrimoireSchemaIdentity.ComputeAsync(first, CancellationToken.None);

        Assert.StartsWith(GrimoireSchemaIdentity.Prefix, firstIdentity, StringComparison.Ordinal);

        Assert.Equal(
            firstIdentity,
            await GrimoireSchemaIdentity.ComputeAsync(second, CancellationToken.None));

    }

    [Fact]
    public async Task Identity_changes_when_the_installed_schema_drifts()
    {

        await using SqliteConnection connection = await InstallAsync();

        string before = await GrimoireSchemaIdentity.ComputeAsync(connection, CancellationToken.None);

        await using (SqliteCommand drift = connection.CreateCommand())
        {

            drift.CommandText = """DROP INDEX "IX_Entries_SessionId_Sequence";""";

            _ = await drift.ExecuteNonQueryAsync(CancellationToken.None);

        }

        Assert.NotEqual(
            before,
            await GrimoireSchemaIdentity.ComputeAsync(connection, CancellationToken.None));

    }

    [Fact]
    public async Task Identity_ignores_row_content()
    {

        await using SqliteConnection connection = await InstallAsync();

        string before = await GrimoireSchemaIdentity.ComputeAsync(connection, CancellationToken.None);

        await using (SqliteCommand insert = connection.CreateCommand())
        {

            insert.CommandText = """
                INSERT INTO "MageSettings" ("Key", "Value", "UpdatedAt")
                VALUES ('probe', 'value', '2026-08-05T00:00:00Z');
                """;

            _ = await insert.ExecuteNonQueryAsync(CancellationToken.None);

        }

        Assert.Equal(
            before,
            await GrimoireSchemaIdentity.ComputeAsync(connection, CancellationToken.None));

    }

    private static async Task<SqliteConnection> InstallAsync()
    {

        SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            "Data Source=:memory:",
            CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        return connection;

    }

}
