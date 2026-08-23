using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The read-only proof that the core tier a full installation reset is about to journal Campaign
/// cleanup children into is the exact shape this binary declares.
/// </summary>
/// <remarks>
/// Every refusal here has to be indistinguishable from every other one, and none of them may leave a
/// mark on the database. A readiness check that repaired what it found, or that named the object it
/// disliked, would turn a fail-closed gate into either a silent migration or an oracle over the
/// installed catalog.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FullInstallationResetCampaignSchemaReadinessTests
{

    private static CancellationToken Token => CancellationToken.None;

    /// <summary>
    /// The kind-four objects this readiness exists to require. A predecessor installation has the
    /// rest of the core tier and none of these.
    /// </summary>
    private static readonly string[] CleanupObjects =
    [
        "campaign_path_full_reset_cleanup_evidence_guard_insert",
        "campaign_path_full_reset_cleanup_evidence_guard_update",
        "campaign_path_full_reset_cleanup_evidence_guard_delete",
        "campaign_path_full_reset_cleanup_evidence",
    ];

    [Fact]
    public async Task Exact_core_manifest_is_accepted_without_ddl_or_metadata_mutation()
    {

        await using CovenantSchemaScratchDatabase database = await ExactAsync();

        CatalogSnapshot before = await CatalogSnapshot.CaptureAsync(database, Token);

        Result result = await Readiness().RequireExactAsync(database.Connection, Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        (await CatalogSnapshot.CaptureAsync(database, Token)).AssertUnchangedFrom(before);

    }

    [Fact]
    public async Task Exact_core_manifest_stays_ready_across_repeated_proofs()
    {

        await using CovenantSchemaScratchDatabase database = await ExactAsync();

        CatalogSnapshot before = await CatalogSnapshot.CaptureAsync(database, Token);

        FullInstallationResetCampaignSchemaReadiness readiness = Readiness();

        for (int attempt = 0; attempt < 3; attempt++)
        {

            Assert.True((await readiness.RequireExactAsync(database.Connection, Token)).IsSuccess);

        }

        (await CatalogSnapshot.CaptureAsync(database, Token)).AssertUnchangedFrom(before);

    }

    /// <summary>
    /// The predecessor shape: an installation whose core tier predates the kind-four cleanup
    /// objects entirely. It is refused rather than migrated, because this repository has no
    /// in-place migration path and a reset that silently added the objects would be journaling
    /// children into a schema no operator ever agreed to.
    /// </summary>
    [Fact]
    public async Task Predecessor_core_manifest_without_the_cleanup_objects_is_rejected()
    {

        await using CovenantSchemaScratchDatabase database = await PredecessorAsync();

        CatalogSnapshot before = await CatalogSnapshot.CaptureAsync(database, Token);

        AssertRefused(await Readiness().RequireExactAsync(database.Connection, Token));

        (await CatalogSnapshot.CaptureAsync(database, Token)).AssertUnchangedFrom(before);

    }

    [Theory]
    [InlineData("DROP TABLE campaign_path_full_reset_cleanup_evidence;")]
    [InlineData("DROP TRIGGER campaign_path_full_reset_cleanup_evidence_guard_insert;")]
    [InlineData("DROP TRIGGER campaign_path_full_reset_cleanup_evidence_guard_update;")]
    [InlineData("DROP TRIGGER campaign_path_full_reset_cleanup_evidence_guard_delete;")]
    [InlineData("DROP TRIGGER campaign_path_marker_intents_guard_update;")]
    [InlineData("DROP INDEX ux_campaign_path_marker_intents_owner_campaign_kind;")]
    [InlineData("ALTER TABLE campaign_path_marker_intents ADD COLUMN DriftColumn TEXT NULL;")]
    [InlineData("ALTER TABLE campaign_path_full_reset_cleanup_evidence ADD COLUMN DriftColumn TEXT NULL;")]
    public async Task A_missing_or_drifted_object_is_rejected_without_ddl_or_metadata_mutation(
        string damage)
    {

        await using CovenantSchemaScratchDatabase database = await ExactAsync();

        await database.ExecuteAsync(damage, Token);

        CatalogSnapshot before = await CatalogSnapshot.CaptureAsync(database, Token);

        AssertRefused(await Readiness().RequireExactAsync(database.Connection, Token));

        (await CatalogSnapshot.CaptureAsync(database, Token)).AssertUnchangedFrom(before);

    }

    /// <summary>
    /// A rebuilt table whose declaration differs only in its guards is the shape a hand-repaired
    /// installation reaches. It has every object the manifest names, so only the stored definition
    /// separates it from the exact one.
    /// </summary>
    [Fact]
    public async Task A_companion_rebuilt_without_its_checks_is_rejected()
    {

        await using CovenantSchemaScratchDatabase database = await ExactAsync();

        await database.ExecuteAsync(
            """
            DROP TABLE campaign_path_full_reset_cleanup_evidence;
            CREATE TABLE campaign_path_full_reset_cleanup_evidence (
                IntentId TEXT NOT NULL PRIMARY KEY
                    REFERENCES campaign_path_marker_intents(IntentId) ON DELETE CASCADE,
                CampaignInventoryEntryDigest BLOB NOT NULL,
                IndexedPhysicalIdentityDigest BLOB NOT NULL,
                CanonicalDisplayPathDigest BLOB NOT NULL,
                SameHandleOwnershipEvidenceDigest BLOB NOT NULL,
                ObservationCode INTEGER NOT NULL,
                OpenedSameHandleOwnershipEvidenceDigest BLOB NULL,
                ObservationDigest BLOB NOT NULL
            );
            """,
            Token);

        CatalogSnapshot before = await CatalogSnapshot.CaptureAsync(database, Token);

        AssertRefused(await Readiness().RequireExactAsync(database.Connection, Token));

        (await CatalogSnapshot.CaptureAsync(database, Token)).AssertUnchangedFrom(before);

    }

    [Theory]
    [InlineData("CREATE TABLE covenant_unexpected_table (Id INTEGER);")]
    [InlineData("CREATE INDEX covenant_unexpected_index ON campaign_path_marker_intents(CampaignId);")]
    public async Task An_unexpected_declared_object_is_rejected(string unexpected)
    {

        await using CovenantSchemaScratchDatabase database = await ExactAsync();

        await database.ExecuteAsync(unexpected, Token);

        AssertRefused(await Readiness().RequireExactAsync(database.Connection, Token));

    }

    /// <summary>
    /// Catalog read uncertainty is not readiness. A connection that cannot answer the catalog
    /// question at all must fail closed rather than being treated as "nothing is wrong".
    /// </summary>
    [Fact]
    public async Task Catalog_read_uncertainty_is_not_treated_as_readiness()
    {

        await using CovenantSchemaScratchDatabase database = await ExactAsync();

        SqliteConnection closed = new(database.Connection.ConnectionString);

        Assert.Equal(ConnectionState.Closed, closed.State);

        AssertRefused(await Readiness().RequireExactAsync(closed, Token));

        await closed.DisposeAsync();

    }

    /// <summary>
    /// An open connection whose catalog cannot be read at all — the shape a wrong key or a damaged
    /// file reaches by the time a caller hands it over already opened.
    /// </summary>
    [Fact]
    public async Task An_open_connection_whose_catalog_cannot_be_read_is_not_treated_as_readiness()
    {

        string directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"arcanum-readiness-{Guid.NewGuid():N}")).FullName;

        try
        {

            string path = Path.Combine(directory, "not-a-database.db");

            await File.WriteAllTextAsync(path, "this is not a SQLite database", Token);

            SqliteConnection unreadable = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString());

            await unreadable.OpenAsync(Token);

            Assert.Equal(ConnectionState.Open, unreadable.State);

            AssertRefused(await Readiness().RequireExactAsync(unreadable, Token));

            await unreadable.DisposeAsync();

        }
        finally
        {

            Directory.Delete(directory, recursive: true);

        }

    }

    [Fact]
    public async Task A_null_connection_is_refused_rather_than_thrown()
    {

        AssertRefused(await Readiness().RequireExactAsync(null!, Token));

    }

    [Fact]
    public async Task Caller_cancellation_is_rethrown_rather_than_collapsed_into_a_refusal()
    {

        await using CovenantSchemaScratchDatabase database = await ExactAsync();

        using CancellationTokenSource cancellation = new();

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Readiness().RequireExactAsync(database.Connection, cancellation.Token));

    }

    /// <summary>
    /// The caller keeps its connection, and keeps it untransacted. The coordinator borrows one
    /// non-pooled core connection for the whole operation and opens its own immediate transaction
    /// after this gate, so a readiness check that closed the connection — or left a snapshot open on
    /// it — would break the operation it was gating.
    /// </summary>
    [Fact]
    public async Task The_callers_connection_is_left_open_and_free_of_any_snapshot()
    {

        await using CovenantSchemaScratchDatabase database = await ExactAsync();

        Result result = await Readiness().RequireExactAsync(database.Connection, Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(ConnectionState.Open, database.Connection.State);

        // Nothing was left behind to inherit: the caller's own immediate transaction still begins.
        await using SqliteTransaction transaction = (SqliteTransaction)
            await database.Connection.BeginTransactionAsync(Token);

        await using SqliteCommand command = database.Connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT COUNT(*) FROM campaign_path_marker_intents;";

        Assert.Equal(0L, await command.ExecuteScalarAsync(Token));

        await transaction.RollbackAsync(Token);

    }

    /// <summary>
    /// A caller that already holds a snapshot is refused rather than silently joined.
    /// </summary>
    /// <remarks>
    /// The gate reads the catalog outside any transaction on purpose: joining one would mean
    /// proving the schema against a snapshot taken before the proof, and beginning one would leave
    /// the coordinator's connection inside a transaction this type has no authority to end. Neither
    /// is worth a fallback, so the unexpected shape fails closed like every other one.
    /// </remarks>
    [Fact]
    public async Task A_caller_held_snapshot_is_refused_rather_than_joined()
    {

        await using CovenantSchemaScratchDatabase database = await ExactAsync();

        await using SqliteTransaction transaction = (SqliteTransaction)
            await database.Connection.BeginTransactionAsync(Token);

        AssertRefused(await Readiness().RequireExactAsync(database.Connection, Token));

        Assert.Equal(ConnectionState.Open, database.Connection.State);

        Assert.Same(database.Connection, transaction.Connection);

        await transaction.RollbackAsync(Token);

    }

    private static FullInstallationResetCampaignSchemaReadiness Readiness() =>
        new(new GrimoireSchemaManifestInspector(GrimoireSchemaTierOwnershipRegistry.CreateDefault()));

    private static async Task<CovenantSchemaScratchDatabase> ExactAsync()
    {

        CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(Token);

        try
        {

            _ = await GrimoireSchemaTestInstaller.InstallAsync(database.Connection, 1536, Token);

            return database;

        }
        catch
        {

            await database.DisposeAsync();

            throw;

        }

    }

    private static async Task<CovenantSchemaScratchDatabase> PredecessorAsync()
    {

        CovenantSchemaScratchDatabase database = await ExactAsync();

        try
        {

            foreach (string name in CleanupObjects)
            {

                await database.ExecuteAsync(
                    name.Contains("guard", StringComparison.Ordinal)
                        ? $"DROP TRIGGER {name};"
                        : $"DROP TABLE {name};",
                    Token);

            }

            return database;

        }
        catch
        {

            await database.DisposeAsync();

            throw;

        }

    }

    /// <summary>
    /// Every refusal is the same refusal, and none of them names what was wrong. An operator gets a
    /// remedy; nobody gets a map of the installed catalog.
    /// </summary>
    private static void AssertRefused(Result result)
    {

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

        foreach (GrimoireSchemaManifestEntry entry in GrimoireSchemaManifests.Core.Entries)
        {

            Assert.DoesNotContain(entry.Name, result.Error.Message, StringComparison.Ordinal);

            foreach (GrimoireExpectedIndex index in entry.Indexes)
            {

                Assert.DoesNotContain(index.Name, result.Error.Message, StringComparison.Ordinal);

            }

        }

    }

    /// <summary>
    /// Everything a readiness check must not move: the installed catalog itself, and the per-tier
    /// metadata a schema installer would rewrite.
    /// </summary>
    private sealed record CatalogSnapshot(string Catalog, string TierMetadata, long SchemaVersion)
    {

        internal static async Task<CatalogSnapshot> CaptureAsync(
            CovenantSchemaScratchDatabase database,
            CancellationToken cancellationToken)
        {

            string catalog = await ReadAsync(
                database,
                """
                SELECT group_concat("type" || '|' || "name" || '|' || COALESCE("sql", ''), char(30))
                FROM sqlite_master ORDER BY "type", "name";
                """,
                cancellationToken);

            string metadata = await ReadAsync(
                database,
                """
                SELECT group_concat(
                    FamilyCode || '|' || TransactionTierCode || '|' || SchemaVersion || '|'
                        || SourceDefinitionFingerprint || '|' || InstalledCatalogFingerprint || '|'
                        || InstalledAtUtc || '|' || HealthCode || '|'
                        || COALESCE(HealthDetailCode, ''),
                    char(30))
                FROM grimoire_feature_schemas
                ORDER BY FamilyCode, TransactionTierCode;
                """,
                cancellationToken);

            await using SqliteCommand version = database.Connection.CreateCommand();

            version.CommandText = "PRAGMA schema_version;";

            object? value = await version.ExecuteScalarAsync(cancellationToken);

            return new CatalogSnapshot(catalog, metadata, Convert.ToInt64(value, provider: null));

        }

        internal void AssertUnchangedFrom(CatalogSnapshot before)
        {

            Assert.Equal(before.Catalog, Catalog);

            Assert.Equal(before.TierMetadata, TierMetadata);

            // SQLite bumps this counter for every schema change, including one made and rolled
            // back. It is the one observable a readiness check cannot leave behind quietly.
            Assert.Equal(before.SchemaVersion, SchemaVersion);

        }

        private static async Task<string> ReadAsync(
            CovenantSchemaScratchDatabase database,
            string sql,
            CancellationToken cancellationToken)
        {

            await using SqliteCommand command = database.Connection.CreateCommand();

            command.CommandText = sql;

            object? value = await command.ExecuteScalarAsync(cancellationToken);

            return value is null or DBNull ? string.Empty : (string)value;

        }

    }

}
