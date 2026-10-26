using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed partial class DoctorDiagnosticsCommandTests
{
    [Theory]
    [InlineData("pending", DoctorOutcome.Degraded)]
    [InlineData("empty", DoctorOutcome.Healthy)]
    [InlineData("legacy", DoctorOutcome.Unavailable)]
    public async Task Schema_transition_doctor_reads_encrypted_progress_without_mutating_the_database(
        string state, DoctorOutcome expected)
    {
        const string secret = "schema-transition-test-secret";

        SqliteNativeRuntime.Instance.Initialize();

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string database = ArcanumPaths.GrimoireDatabaseFile;

        await using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false,
            Password = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecretLegacy(secret),
        }.ToString()))
        {
            await connection.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = state == "legacy"
                ? "CREATE TABLE legacy (Id INTEGER PRIMARY KEY);"
                : GrimoireSchemaCatalog.CoreObjects.Single(item => item.Name == "grimoire_schema_transitions").Sql;

            _ = await command.ExecuteNonQueryAsync();

            if (state == "pending")
            {
                command.CommandText = """
                    INSERT INTO grimoire_schema_transitions (
                        FamilyCode, TransactionTierCode, FromVersion, TargetVersion, CompletedThroughVersion,
                        TargetSourceDefinitionFingerprint, BackfillName, BackfillCursor, BackfillRowsProcessed,
                        Revision, LastDurableErrorCode, StartedAtUtc, UpdatedAtUtc)
                    VALUES (0,0,8,25,8,$fingerprint,'core-utc-instant-canonicalization',
                        'PRIVATE-CURSOR-CANARY',4096,16,'Grimoire.Schema.DiskFull',$now,$now);
                    """;

                command.Parameters.AddWithValue("$fingerprint", new string('F', 64));

                command.Parameters.AddWithValue("$now", "2026-10-26T00:00:00.0000000Z");

                _ = await command.ExecuteNonQueryAsync();
            }
        }

        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(database));

        ServiceCollection services = BuildServices();

        services.AddSingleton<ISecretStore>(new SecretBearingStore(secret));

        CliTestResult result = await CliTestHarness.RunAsync(services,
            ["doctor", "--json", "--only", "grimoire.schema_transition"]);

        Assert.Equal(0, result.ExitCode);

        DoctorCheck check = Assert.Single(Deserialize(result.Output).Checks);

        Assert.Equal(expected, check.Outcome);

        if (state == "pending")
        {
            Assert.Contains("core-utc-instant-canonicalization", check.Detail, StringComparison.Ordinal);

            Assert.Contains("4096", check.Detail, StringComparison.Ordinal);

            Assert.Contains("Grimoire.Schema.DiskFull", check.Detail, StringComparison.Ordinal);

            Assert.Contains("2026-10-26", check.Detail, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("PRIVATE-CURSOR", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(secret, result.Output, StringComparison.Ordinal);

        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(database)));
    }
}
