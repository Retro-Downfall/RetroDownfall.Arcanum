using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>Supplemental schema-name lookup controls, not a synthetic executable paid graph.</summary>
public sealed class BackupRestoreLongRestSanitationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Older_schema_absence_refuses_case_aliased_inventory_or_kind16_labels(bool labelResidue)
    {
        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = new("Data Source=:memory:;Pooling=False");

        await connection.OpenAsync(CancellationToken.None);

        // Minimal reader fixture only: one old Core-version row and harmless residue marker.
        // This does not claim a valid restored schema, physical attempt, capability or paid effect.
        await using (SqliteCommand setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE grimoire_feature_schemas (FamilyCode INTEGER, TransactionTierCode INTEGER, SchemaVersion INTEGER);
                INSERT INTO grimoire_feature_schemas VALUES (0, 0, 17);
                """ + (labelResidue
                    ? "CREATE TABLE ARTIFACT_SENSITIVITY (ArtifactKindCode INTEGER); INSERT INTO ARTIFACT_SENSITIVITY VALUES (16);"
                    : "CREATE TABLE LONG_REST_PROVIDER_EFFECTS (Sentinel INTEGER); INSERT INTO LONG_REST_PROVIDER_EFFECTS VALUES (1);");

            _ = await setup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        string lookup = labelResidue ? "artifact_sensitivity" : "long_rest_provider_effects";

        await using SqliteCommand observe = connection.CreateCommand();

        // SQLite's actual identifier resolution sees the row through the lowercase product name.
        observe.CommandText = $"SELECT COUNT(*) FROM {lookup};";

        Assert.Equal(1L, (long)(await observe.ExecuteScalarAsync(CancellationToken.None))!);

        observe.CommandText = "SELECT group_concat(name || ':' || sql, '|') FROM sqlite_master;";

        string beforeCatalog = (string)(await observe.ExecuteScalarAsync(CancellationToken.None))!;

        Result verified = await BackupRestoreLongRestSanitation.VerifyCommittedAsync(connection, CancellationToken.None);

        Assert.True(verified.IsFailure);

        Assert.Equal(beforeCatalog, (string)(await observe.ExecuteScalarAsync(CancellationToken.None))!);

        observe.CommandText = $"SELECT COUNT(*) FROM {lookup};";

        Assert.Equal(1L, (long)(await observe.ExecuteScalarAsync(CancellationToken.None))!);

        observe.CommandText = "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode = 0 AND TransactionTierCode = 0;";

        Assert.Equal(17L, (long)(await observe.ExecuteScalarAsync(CancellationToken.None))!);
    }
}
