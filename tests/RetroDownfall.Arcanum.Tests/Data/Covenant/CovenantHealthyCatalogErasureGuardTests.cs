using System.Data;

using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The complete catalog and metadata proof required before a healthy-catalog factory erasure.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CovenantHealthyCatalogErasureGuardTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Healthy_canonical_with_absent_or_complete_accelerator_succeeds(bool withAccelerator)
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator);

        Result result = await Guard(database).RequireHealthyAsync(Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

    }

    [Theory]
    [InlineData("DROP TABLE covenant_key_epochs;")]
    [InlineData("DROP TRIGGER covenant_entries_guard_delete;")]
    [InlineData("DROP INDEX idx_covenant_entries_campaign;")]
    [InlineData("ALTER TABLE covenant_turn_receipt_aggregate ADD COLUMN DriftColumn TEXT NULL;")]
    public async Task Missing_or_changed_canonical_manifest_object_refuses(string damage)
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator: true);

        await database.ExecuteAsync(damage, Token);

        AssertRefused(await Guard(database).RequireHealthyAsync(Token), database);

    }

    [Fact]
    public async Task Accelerator_metadata_with_a_missing_object_refuses()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator: true);

        await database.ExecuteAsync("DROP TRIGGER covenant_search_documents_ai;", Token);

        AssertRefused(await Guard(database).RequireHealthyAsync(Token), database);

    }

    [Fact]
    public async Task One_trusted_accelerator_index_without_the_tier_or_metadata_is_not_absent()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator: false);

        // This trusted name is nested under a manifest table entry, not a top-level entry. Counting
        // only Entry.Name would call this wholly absent even though a shipped accelerator index is
        // present in sqlite_master.
        await database.ExecuteAsync(
            """
            CREATE TABLE unrelated_index_owner (CampaignId TEXT NULL);
            CREATE INDEX idx_covenant_search_documents_campaign
                ON unrelated_index_owner(CampaignId);
            """,
            Token);

        AssertRefused(await Guard(database).RequireHealthyAsync(Token), database);

    }

    [Theory]
    [InlineData("CREATE TABLE covenant_unexpected_table (Id INTEGER);")]
    [InlineData("CREATE TRIGGER covenant_unexpected_trigger AFTER INSERT ON covenant_entries BEGIN SELECT 1; END;")]
    [InlineData("CREATE INDEX covenant_unexpected_index ON covenant_entries(AuthoredKey);")]
    public async Task Unexpected_Covenant_table_trigger_or_index_refuses(string unexpected)
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator: false);

        await database.ExecuteAsync(unexpected, Token);

        AssertRefused(await Guard(database).RequireHealthyAsync(Token), database);

    }

    [Theory]
    [InlineData(MetadataDamage.Absent)]
    [InlineData(MetadataDamage.Duplicated)]
    [InlineData(MetadataDamage.Unhealthy)]
    [InlineData(MetadataDamage.WrongVersion)]
    [InlineData(MetadataDamage.WrongSource)]
    [InlineData(MetadataDamage.WrongInstalledFingerprint)]
    [InlineData(MetadataDamage.DiagnosticBearing)]
    public async Task Invalid_canonical_metadata_refuses_even_when_manifest_is_valid(MetadataDamage damage)
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator: true);

        await DamageCanonicalMetadataAsync(database, damage);

        AssertRefused(await Guard(database).RequireHealthyAsync(Token), database);

    }

    [Fact]
    public async Task An_unreadable_encrypted_catalog_refuses_content_free()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator: true);

        CovenantHealthyCatalogErasureGuard guard = new(
            new WrongKeyMaintenanceFactory(database.DatabasePath),
            CovenantSqliteConnectionInitializer.Instance,
            new CovenantConnectionDrain(),
            Inspector());

        AssertRefused(await guard.RequireHealthyAsync(Token), database);

    }

    [Fact]
    public async Task Committed_definition_damage_in_a_live_WAL_refuses()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator: false);

        await database.ExecuteAsync(
            """
            PRAGMA wal_checkpoint(TRUNCATE);
            PRAGMA wal_autocheckpoint=0;
            DROP TRIGGER covenant_entries_guard_delete;
            """,
            Token);

        Assert.True(File.Exists(database.DatabasePath + "-wal"));

        Assert.True(new FileInfo(database.DatabasePath + "-wal").Length > 0);

        AssertRefused(await Guard(database).RequireHealthyAsync(Token), database);

    }

    [Fact]
    public async Task Owning_proof_enrolls_before_one_ReadOnly_initialization_then_closes_disposes_and_unregisters()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator: true);

        RecordingMaintenanceConnectionFactory connections =
            new(database.MaintenanceConnections());

        RecordingConnectionDrain drain = new();

        RecordingInitializer initializer = new(drain);

        CovenantHealthyCatalogErasureGuard guard =
            new(connections, initializer, drain, Inspector());

        Result result = await guard.RequireHealthyAsync(Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        SqliteConnection candidate = Assert.Single(connections.Opened);

        Assert.Equal([CovenantSqliteConnectionMode.ReadOnly], initializer.Modes);

        Assert.True(initializer.SawEnrolledConnection);

        Assert.Equal(ConnectionState.Closed, candidate.State);

        Assert.Equal(0, drain.ActiveRegistrations);

    }

    [Fact]
    public async Task Owning_failure_closes_disposes_and_unregisters_the_direct_handle()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator: true);

        await database.ExecuteAsync("DROP TRIGGER covenant_entries_guard_delete;", Token);

        RecordingMaintenanceConnectionFactory connections =
            new(database.MaintenanceConnections());

        RecordingConnectionDrain drain = new();

        CovenantHealthyCatalogErasureGuard guard = new(
            connections,
            CovenantSqliteConnectionInitializer.Instance,
            drain,
            Inspector());

        AssertRefused(await guard.RequireHealthyAsync(Token), database);

        Assert.Equal(ConnectionState.Closed, Assert.Single(connections.Opened).State);

        Assert.Equal(0, drain.ActiveRegistrations);

    }

    [Fact]
    public async Task Caller_cancellation_is_rethrown_after_the_direct_handle_is_cleaned_up()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator: true);

        RecordingMaintenanceConnectionFactory connections =
            new(database.MaintenanceConnections());

        RecordingConnectionDrain drain = new();

        using CancellationTokenSource cancellation = new();

        CovenantHealthyCatalogErasureGuard guard = new(
            connections,
            new CancellingInitializer(cancellation),
            drain,
            Inspector());

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => guard.RequireHealthyAsync(cancellation.Token));

        Assert.Equal(ConnectionState.Closed, Assert.Single(connections.Opened).State);

        Assert.Equal(0, drain.ActiveRegistrations);

    }

    [Fact]
    public async Task Borrowed_proof_uses_the_callers_open_connection_and_active_snapshot_without_lifecycle_actions()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator: true);

        CovenantHealthyCatalogErasureGuard guard = new(
            new UnreachableMaintenanceConnectionFactory(),
            new UnreachableInitializer(),
            new UnreachableConnectionDrain(),
            Inspector());

        await using SqliteTransaction transaction =
            (SqliteTransaction)await database.Connection.BeginTransactionAsync(Token);

        Result result = await guard.RequireHealthyWithinAsync(
            database.Connection,
            transaction,
            Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Same(database.Connection, transaction.Connection);

        await using SqliteCommand command = database.Connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT COUNT(*) FROM covenant_state;";

        Assert.Equal(1L, await command.ExecuteScalarAsync(Token));

        await transaction.RollbackAsync(Token);

    }

    [Fact]
    public async Task Borrowed_proof_refuses_a_completed_transaction_without_touching_the_connection()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyAsync(withAccelerator: true);

        CovenantHealthyCatalogErasureGuard guard = Guard(database);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await database.Connection.BeginTransactionAsync(Token);

        await transaction.CommitAsync(Token);

        Result result = await guard.RequireHealthyWithinAsync(
            database.Connection,
            transaction,
            Token);

        AssertRefused(result, database);

        Assert.Equal(ConnectionState.Open, database.Connection.State);

    }

    private static CovenantHealthyCatalogErasureGuard Guard(
        CovenantSchemaScratchDatabase database) =>
        new(
            database.MaintenanceConnections(),
            CovenantSqliteConnectionInitializer.Instance,
            new CovenantConnectionDrain(),
            Inspector());

    private static GrimoireSchemaManifestInspector Inspector() =>
        new(GrimoireSchemaTierOwnershipRegistry.CreateDefault());

    private static async Task<CovenantSchemaScratchDatabase> HealthyAsync(bool withAccelerator)
    {

        CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(Token);

        try
        {

            await database.InstallHealthyCovenantCatalogAsync(withAccelerator, Token);

            return database;

        }
        catch
        {

            await database.DisposeAsync();

            throw;

        }

    }

    private static void AssertRefused(
        Result result,
        CovenantSchemaScratchDatabase database)
    {

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.Contains("restore", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Covenant-family reinitialize", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("full installation reset", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(database.DatabasePath, result.Error.Message, StringComparison.Ordinal);

        foreach (GrimoireSchemaManifest manifest in Manifests())
        {

            foreach (GrimoireSchemaManifestEntry entry in manifest.Entries)
            {

                Assert.DoesNotContain(entry.Name, result.Error.Message, StringComparison.Ordinal);

                foreach (GrimoireExpectedIndex index in entry.Indexes)
                {

                    Assert.DoesNotContain(index.Name, result.Error.Message, StringComparison.Ordinal);

                }

            }

        }

    }

    private static IReadOnlyList<GrimoireSchemaManifest> Manifests() =>
        [
            GrimoireSchemaManifests.CovenantCanonical,
            GrimoireSchemaManifests.CovenantAccelerator,
        ];

    private static async Task DamageCanonicalMetadataAsync(
        CovenantSchemaScratchDatabase database,
        MetadataDamage damage)
    {

        if (damage is MetadataDamage.Duplicated)
        {

            GrimoireSchemaInspectionResult inspected = await Inspector().InspectAsync(
                database.Connection,
                transaction: null,
                GrimoireSchemaManifests.CovenantCanonical,
                Token);

            Assert.True(inspected.IsValid);

            await database.ExecuteAsync(
                """
                DROP TABLE grimoire_feature_schemas;
                CREATE TABLE grimoire_feature_schemas (
                    FamilyCode INTEGER NOT NULL,
                    TransactionTierCode INTEGER NOT NULL,
                    SchemaVersion INTEGER NOT NULL,
                    SourceDefinitionFingerprint TEXT NOT NULL,
                    InstalledCatalogFingerprint TEXT NOT NULL,
                    InstalledAtUtc TEXT NOT NULL,
                    HealthCode INTEGER NOT NULL,
                    HealthDetailCode TEXT NULL
                );
                """,
                Token);

            await InsertMetadataAsync(database, inspected.InstalledCatalogFingerprint!);

            await InsertMetadataAsync(database, inspected.InstalledCatalogFingerprint!);

            return;

        }

        string mutation = damage switch
        {

            MetadataDamage.Absent => "DELETE FROM grimoire_feature_schemas WHERE TransactionTierCode = 1;",

            MetadataDamage.Unhealthy =>
                "UPDATE grimoire_feature_schemas SET HealthCode = 1 WHERE TransactionTierCode = 1;",

            // One past the declared head, read from the chain rather than written as a literal. A
            // literal stops being a wrong version the moment the tier evolves onto it, and the case
            // then damages nothing while still reading as a damage test.
            MetadataDamage.WrongVersion =>
                "UPDATE grimoire_feature_schemas SET SchemaVersion = "
                    + (GrimoireSchemaVersionChains.CovenantCanonicalSchemaVersion + 1).ToString(CultureInfo.InvariantCulture)
                    + " WHERE TransactionTierCode = 1;",

            MetadataDamage.WrongSource =>
                $"UPDATE grimoire_feature_schemas SET SourceDefinitionFingerprint = '{new string('A', 64)}' "
                    + "WHERE TransactionTierCode = 1;",

            MetadataDamage.WrongInstalledFingerprint =>
                $"UPDATE grimoire_feature_schemas SET InstalledCatalogFingerprint = 'sha256-{new string('b', 64)}' "
                    + "WHERE TransactionTierCode = 1;",

            MetadataDamage.DiagnosticBearing =>
                "UPDATE grimoire_feature_schemas SET HealthDetailCode = 'Grimoire.Schema.Test' "
                    + "WHERE TransactionTierCode = 1;",

            _ => throw new ArgumentOutOfRangeException(nameof(damage)),

        };

        await database.ExecuteAsync(mutation, Token);

    }

    private static async Task InsertMetadataAsync(
        CovenantSchemaScratchDatabase database,
        string installedFingerprint)
    {

        GrimoireSchemaManifest manifest = GrimoireSchemaManifests.CovenantCanonical;

        await using SqliteCommand command = database.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO grimoire_feature_schemas (
                FamilyCode, TransactionTierCode, SchemaVersion, SourceDefinitionFingerprint,
                InstalledCatalogFingerprint, InstalledAtUtc, HealthCode, HealthDetailCode)
            VALUES ($family, $tier, $version, $source, $installed, '2026-08-20T00:00:00Z', 0, NULL);
            """;

        _ = command.Parameters.AddWithValue("$family", (long)manifest.Family);

        _ = command.Parameters.AddWithValue("$tier", (long)manifest.TransactionTier);

        _ = command.Parameters.AddWithValue("$version", manifest.Version);

        _ = command.Parameters.AddWithValue("$source", manifest.SourceDefinitionFingerprint);

        _ = command.Parameters.AddWithValue("$installed", installedFingerprint);

        _ = await command.ExecuteNonQueryAsync(Token);

    }

    public enum MetadataDamage
    {

        Absent,

        Duplicated,

        Unhealthy,

        WrongVersion,

        WrongSource,

        WrongInstalledFingerprint,

        DiagnosticBearing,

    }

    private sealed class RecordingMaintenanceConnectionFactory(
        ICovenantMaintenanceConnectionFactory inner) : ICovenantMaintenanceConnectionFactory
    {

        private readonly List<SqliteConnection> _opened = [];

        internal IReadOnlyList<SqliteConnection> Opened => _opened;

        public string DatabasePath => inner.DatabasePath;

        public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken)
        {

            SqliteConnection connection = await inner.OpenReadOnlyAsync(cancellationToken);

            _opened.Add(connection);

            return connection;

        }

        public Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SqliteConnection> OpenSideFileAsync(
            string path,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AttachSideFileAsync(
            SqliteConnection connection,
            string alias,
            string path,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class WrongKeyMaintenanceFactory(string databasePath)
        : ICovenantMaintenanceConnectionFactory
    {

        public string DatabasePath { get; } = databasePath;

        public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken)
        {

            SqliteConnection connection = new(
                CovenantMaintenanceConnectionFactory.ReadOnly(DatabasePath, "definitely-the-wrong-key").ToString());

            try
            {

                await connection.OpenAsync(cancellationToken);

                return connection;

            }
            catch
            {

                await connection.DisposeAsync();

                throw;

            }

        }

        public Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SqliteConnection> OpenSideFileAsync(
            string path,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AttachSideFileAsync(
            SqliteConnection connection,
            string alias,
            string path,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingConnectionDrain : ICovenantConnectionDrain
    {

        private readonly CovenantConnectionDrain _inner = new();

        private int _active;

        internal int ActiveRegistrations => Volatile.Read(ref _active);

        public IDisposable Register(SqliteConnection connection)
        {

            IDisposable inner = _inner.Register(connection);

            _ = Interlocked.Increment(ref _active);

            return new Registration(inner, this);

        }

        public Task<Result> DrainAsync(CancellationToken cancellationToken) =>
            _inner.DrainAsync(cancellationToken);

        public Result ClearExactPoolAfterClose(SqliteConnection connection) =>
            _inner.ClearExactPoolAfterClose(connection);

        private sealed class Registration(
            IDisposable inner,
            RecordingConnectionDrain owner) : IDisposable
        {

            private int _disposed;

            public void Dispose()
            {

                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {

                    return;

                }

                inner.Dispose();

                _ = Interlocked.Decrement(ref owner._active);

            }

        }

    }

    private sealed class RecordingInitializer(RecordingConnectionDrain drain)
        : ICovenantSqliteConnectionInitializer
    {

        private readonly List<CovenantSqliteConnectionMode> _modes = [];

        internal IReadOnlyList<CovenantSqliteConnectionMode> Modes => _modes;

        internal bool SawEnrolledConnection { get; private set; }

        public async ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            SawEnrolledConnection = drain.ActiveRegistrations == 1;

            _modes.Add(mode);

            await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
                connection,
                mode,
                cancellationToken);

        }

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            CovenantSqliteConnectionInitializer.Instance.Authorize(connection, kind);

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            CovenantSqliteConnectionInitializer.Instance
                .AuthorizeRestoreStagingManagedAuthoritySanitization(authority, runIdentity);

    }

    private sealed class CancellingInitializer(CancellationTokenSource cancellation)
        : ICovenantSqliteConnectionInitializer
    {

        public ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            cancellation.Cancel();

            cancellationToken.ThrowIfCancellationRequested();

            throw new InvalidOperationException("The cancellation token did not cancel.");

        }

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class UnreachableMaintenanceConnectionFactory : ICovenantMaintenanceConnectionFactory
    {

        public string DatabasePath => throw new NotSupportedException();

        public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SqliteConnection> OpenSideFileAsync(
            string path,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AttachSideFileAsync(
            SqliteConnection connection,
            string alias,
            string path,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class UnreachableInitializer : ICovenantSqliteConnectionInitializer
    {

        public ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class UnreachableConnectionDrain : ICovenantConnectionDrain
    {

        public IDisposable Register(SqliteConnection connection) =>
            throw new NotSupportedException();

        public Task<Result> DrainAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Result ClearExactPoolAfterClose(SqliteConnection connection) =>
            throw new NotSupportedException();

    }

}
