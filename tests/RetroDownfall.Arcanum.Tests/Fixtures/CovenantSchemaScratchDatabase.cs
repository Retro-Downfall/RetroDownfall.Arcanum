using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal interface IDesignTimeGrimoireConnectionFactory
    : IStoppedHostGrimoireConnectionFactory
{

    string DatabasePath { get; }

    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken);

    Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken);

    Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(
        CancellationToken cancellationToken);

    Task<SqliteConnection> OpenSideFileAsync(
        string path,
        CancellationToken cancellationToken);

    Task AttachSideFileAsync(
        SqliteConnection connection,
        string alias,
        string path,
        CancellationToken cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    async Task<Result<IStoppedHostGrimoireConnectionLease>>
        IStoppedHostGrimoireConnectionFactory
            .OpenStoppedHostInstallationResetPlanReadAsync(
                IStoppedHostGrimoireConnectionAuthority authority,
                CancellationToken cancellationToken) =>
        await OpenStoppedAsync(
            writable: false,
            cancellationToken).ConfigureAwait(false);

    [GrimoireConnectionAcquisitionRoute]
    async Task<Result<IStoppedHostGrimoireConnectionLease>>
        IStoppedHostGrimoireConnectionFactory
            .OpenStoppedHostInstallationResetWorkspaceResolutionAsync(
                IStoppedHostGrimoireConnectionAuthority authority,
                CancellationToken cancellationToken) =>
        await OpenStoppedAsync(
            writable: false,
            cancellationToken).ConfigureAwait(false);

    [GrimoireConnectionAcquisitionRoute]
    async Task<Result<IStoppedHostGrimoireConnectionLease>>
        IStoppedHostGrimoireConnectionFactory
            .OpenStoppedHostInstallationResetIdentityReadAsync(
                IStoppedHostGrimoireConnectionAuthority authority,
                CancellationToken cancellationToken) =>
        await OpenStoppedAsync(
            writable: false,
            cancellationToken).ConfigureAwait(false);

    [GrimoireConnectionAcquisitionRoute]
    async Task<Result<IStoppedHostGrimoireConnectionLease>>
        IStoppedHostGrimoireConnectionFactory
            .OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync(
                IStoppedHostGrimoireConnectionAuthority authority,
                CancellationToken cancellationToken) =>
        await OpenStoppedAsync(
            writable: false,
            cancellationToken).ConfigureAwait(false);

    [GrimoireConnectionAcquisitionRoute]
    async Task<Result<IStoppedHostGrimoireConnectionLease>>
        IStoppedHostGrimoireConnectionFactory
            .OpenStoppedHostInstallationResetApplyAsync(
                IStoppedHostGrimoireConnectionAuthority authority,
                CancellationToken cancellationToken) =>
        await OpenStoppedAsync(
            writable: true,
            cancellationToken).ConfigureAwait(false);

    [GrimoireConnectionAcquisitionRoute]
    async Task<Result<IStoppedHostGrimoireConnectionLease>>
        IStoppedHostGrimoireConnectionFactory.OpenStoppedHostMarkerPairResetAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken) =>
        await OpenStoppedAsync(
            writable: true,
            cancellationToken).ConfigureAwait(false);

    private async Task<Result<IStoppedHostGrimoireConnectionLease>> OpenStoppedAsync(
        bool writable,
        CancellationToken cancellationToken)
    {

        SqliteConnection? connection = null;

        try
        {

            connection = writable
                ? await OpenAsync(cancellationToken).ConfigureAwait(false)
                : await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);

            await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
                connection,
                writable
                    ? CovenantSqliteConnectionMode.ReadWrite
                    : CovenantSqliteConnectionMode.ReadOnly,
                cancellationToken).ConfigureAwait(false);

            return Result<IStoppedHostGrimoireConnectionLease>.Success(
                new DesignTimeStoppedHostGrimoireConnectionLease(connection));

        }
        catch (Exception exception)
        {

            if (connection is not null)
            {

                await connection.DisposeAsync().ConfigureAwait(false);

            }

            return Result<IStoppedHostGrimoireConnectionLease>.Failure(new Error(
                ErrorCodes.Covenant.MaintenanceFailed,
                exception.Message));

        }

    }

}

internal sealed class DesignTimeStoppedHostGrimoireConnectionLease(
    SqliteConnection connection) : IStoppedHostGrimoireConnectionLease
{

    public SqliteConnection Connection { get; } = connection;

    public ValueTask DisposeAsync() => Connection.DisposeAsync();

}

internal sealed class DesignTimeGrimoireConnectionFactory(
    IGrimoireDbPassphraseSource passphrase,
    string? databasePath = null) : IDesignTimeGrimoireConnectionFactory
{

    private readonly string _databasePath = Path.GetFullPath(
        databasePath ?? ArcanumPaths.GrimoireDatabaseFile);

    public string DatabasePath => _databasePath;

    public Task<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken) =>
        OpenCoreAsync(
            new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,

                Password = passphrase.Passphrase,

                Pooling = false,
            },
            cancellationToken);

    public Task<SqliteConnection> OpenReadOnlyAsync(
        CancellationToken cancellationToken) =>
        OpenCoreAsync(
            new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,

                Password = passphrase.Passphrase,

                Pooling = false,

                Mode = SqliteOpenMode.ReadOnly,

                Cache = SqliteCacheMode.Private,
            },
            cancellationToken);

    public Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(
        CancellationToken cancellationToken) =>
        OpenCoreAsync(
            new SqliteConnectionStringBuilder
            {
                DataSource = "file:" + DatabasePath + "?immutable=1",

                Password = passphrase.Passphrase,

                Pooling = false,

                Mode = SqliteOpenMode.ReadOnly,
            },
            cancellationToken);

    public Task<SqliteConnection> OpenSideFileAsync(
        string path,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(path);

        return OpenCoreAsync(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,

                Password = passphrase.Passphrase,

                Pooling = false,
            },
            cancellationToken);

    }

    public async Task AttachSideFileAsync(
        SqliteConnection connection,
        string alias,
        string path,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentException.ThrowIfNullOrEmpty(alias);

        ArgumentException.ThrowIfNullOrEmpty(path);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"ATTACH DATABASE $path AS {alias} KEY $key;";

        _ = command.Parameters.AddWithValue("$path", path);

        _ = command.Parameters.AddWithValue("$key", passphrase.Passphrase);

        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);

    }

    private static async Task<SqliteConnection> OpenCoreAsync(
        SqliteConnectionStringBuilder builder,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = new(builder.ToString());

        try
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            return connection;

        }
        catch
        {

            await connection.DisposeAsync().ConfigureAwait(false);

            throw;

        }

    }

}

/// <summary>
/// One encrypted, file-backed scratch Grimoire that the Covenant canonical and accelerator schema
/// suites install their tiers into.
/// </summary>
/// <remarks>
/// The database is a real file rather than <c>:memory:</c> on purpose. FTS5 secure delete is a
/// page-level property of the database the index lives in, so the accelerator initializer's
/// read-back only proves anything against storage that actually has pages to free.
///
/// <para>Every connection also goes through <see cref="CovenantSqliteConnectionInitializer"/>, which
/// registers the authorization functions the canonical triggers consult. Without it, a trigger whose
/// guard calls <c>arcanum_owner_cleanup_authorized()</c> fails with "no such function" instead of
/// denying the write, and a test would pass for the wrong reason.</para>
/// </remarks>
public sealed class CovenantSchemaScratchDatabase : IAsyncDisposable
{

    /// <summary>
    /// Fixed passphrase: these databases are scratch files under the OS temp root that live for one
    /// test, so a per-instance secret would buy nothing and only make a failure harder to reproduce.
    /// </summary>
    private const string Passphrase = "covenant-schema-scratch-key";

    private const string OwnerDeletionEventsObjectName = "owner_deletion_events";

    /// <summary>
    /// The provider has to be installed before the first connection is constructed. Doing it here
    /// rather than in each suite keeps a filtered run from depending on some earlier test in the run
    /// having initialized it.
    /// </summary>
    static CovenantSchemaScratchDatabase() => SqliteNativeRuntime.Instance.Initialize();

    private readonly string _path;

    private bool _disposed;

    private CovenantSchemaScratchDatabase(string path, SqliteConnection connection)
    {

        _path = path;

        Connection = connection;

    }

    /// <summary>
    /// The open, initialized read-write connection every helper and every test writes through.
    /// </summary>
    public SqliteConnection Connection { get; }

    /// <summary>
    /// Absolute path of the scratch database file, for tests that need to reopen it. Deliberately
    /// not named <c>Path</c>: that would shadow <see cref="System.IO.Path"/> throughout this type.
    /// </summary>
    public string DatabasePath => _path;

    public static async Task<CovenantSchemaScratchDatabase> CreateAsync(CancellationToken cancellationToken)
    {

        // One directory per instance, holding nothing but this database and whatever it leaves beside
        // it. A suite that proves an erasure left no residual artifact has to be able to enumerate
        // the database's own directory, and a shared temp root would hand it every other test's
        // leftovers as evidence.
        string directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"covenant-schema-{Guid.NewGuid():N}")).FullName;

        string path = Path.Combine(directory, "arcanum.db");

        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,

                Password = Passphrase,

                // Pooling would hand the same native handle back out with its authorization state
                // already set by whichever test released it, which is exactly what these suites
                // assert against.
                Pooling = false,
            }.ToString());

        try
        {

            await connection.OpenAsync(cancellationToken);

            await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
                connection,
                CovenantSqliteConnectionMode.ReadWrite,
                cancellationToken);

        }
        catch
        {

            await connection.DisposeAsync();

            DeleteDirectory(directory);

            throw;

        }

        return new CovenantSchemaScratchDatabase(path, connection);

    }

    /// <summary>
    /// Installs the Covenant canonical tier: the core deletion journal it reads, then every canonical
    /// object in catalog order, then the tier's seed inside one committed transaction.
    /// </summary>
    /// <remarks>
    /// The core tier is deliberately not installed. Only <c>owner_deletion_events</c> is needed,
    /// because <see cref="CovenantCanonicalSchemaDataInitializer"/> reads its maximum sequence per
    /// owner kind, and installing the rest would hide a canonical object that had grown an
    /// undeclared dependency on a core table.
    /// </remarks>
    public async Task InstallCanonicalAsync(CancellationToken cancellationToken)
    {

        await ExecuteAsync(ReadCoreObjectSql(OwnerDeletionEventsObjectName), cancellationToken);

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.CovenantCanonicalObjects)
        {

            await ExecuteAsync(
                GrimoireSchemaCatalog.Resolve(definition, embeddingDimensions: null),
                cancellationToken);

        }

        await RunInitializerAsync(new CovenantCanonicalSchemaDataInitializer(), cancellationToken);

    }

    /// <summary>
    /// Installs named core schema objects a Covenant suite genuinely depends on.
    /// </summary>
    /// <remarks>
    /// Named rather than wholesale. Installing the entire core tier would hide a Covenant object
    /// that had grown an undeclared dependency on a core table, which is precisely the drift these
    /// suites exist to catch.
    /// </remarks>
    public async Task InstallCoreObjectsAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(names);

        foreach (string name in names)
        {

            await ExecuteAsync(ReadCoreObjectSql(name), cancellationToken);

        }

    }

    /// <summary>
    /// Installs the Covenant accelerator tier over an already-installed canonical tier.
    /// </summary>
    public async Task InstallAcceleratorAsync(CancellationToken cancellationToken)
    {

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.CovenantAcceleratorObjects)
        {

            await ExecuteAsync(
                GrimoireSchemaCatalog.Resolve(definition, embeddingDimensions: null),
                cancellationToken);

        }

        await RunInitializerAsync(new CovenantAcceleratorSchemaDataInitializer(), cancellationToken);

    }

    /// <summary>
    /// Installs the core metadata table and one complete, inspector-proven Covenant catalog.
    /// </summary>
    internal async Task InstallHealthyCovenantCatalogAsync(
        bool withAccelerator,
        CancellationToken cancellationToken)
    {

        await InstallCoreObjectsAsync(["grimoire_feature_schemas"], cancellationToken);

        await InstallCanonicalAsync(cancellationToken);

        await RecordHealthyTierAsync(GrimoireSchemaManifests.CovenantCanonical, cancellationToken);

        if (withAccelerator)
        {

            await InstallAcceleratorAsync(cancellationToken);

            await RecordHealthyTierAsync(GrimoireSchemaManifests.CovenantAccelerator, cancellationToken);

        }

    }

    /// <summary>
    /// Opens a second initialized connection to the same scratch file.
    /// </summary>
    /// <remarks>
    /// Contention suites need genuinely separate connections: two commands on one connection
    /// serialize inside SQLite before they ever reach the write lock, so a single-connection "race"
    /// proves nothing about the lock this tier actually relies on.
    /// </remarks>
    public async Task<SqliteConnection> OpenAdditionalConnectionAsync(CancellationToken cancellationToken)
    {

        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = _path,

                Password = Passphrase,

                Pooling = false,
            }.ToString());

        try
        {

            await connection.OpenAsync(cancellationToken);

            await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
                connection,
                CovenantSqliteConnectionMode.ReadWrite,
                cancellationToken);

            return connection;

        }
        catch
        {

            await connection.DisposeAsync();

            throw;

        }

    }

    /// <summary>
    /// Opens an unpooled handle to the same scratch file and deliberately does not initialize it.
    /// </summary>
    /// <remarks>
    /// For the one kind of suite that owns initialization itself. An exclusive maintenance connection
    /// applies its own mode and proves its own <c>secure_delete</c>, and handing it a connection some
    /// other component had already initialized would hide exactly the step under test.
    /// </remarks>
    public async Task<SqliteConnection> OpenUninitializedConnectionAsync(CancellationToken cancellationToken)
    {

        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = _path,

                Password = Passphrase,

                Pooling = false,
            }.ToString());

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

    /// <summary>
    /// Hands a maintenance path its own factory over this same scratch file.
    /// </summary>
    /// <remarks>
    /// The factory rather than a connection, because the production seam owns three things a test
    /// cannot substitute without changing what is under test: the database's path, the key every
    /// handle is opened under, and the exact flags that make a read-only handle unable to create a
    /// sidecar. The scratch factory reaches for the production helpers for the last two, so a suite
    /// proving sidecar absence proves it about the connection string production actually opens.
    /// </remarks>
    internal IDesignTimeGrimoireConnectionFactory MaintenanceConnections() => new ScratchMaintenance(this);

    /// <summary>
    /// Reopens and reinitializes <see cref="Connection"/> after a maintenance path drained it.
    /// </summary>
    /// <remarks>
    /// A drained handle is closed rather than disposed, so the same object can carry a suite's
    /// assertions afterwards. Reinitializing is not optional: closing drops every pragma, and the
    /// canonical triggers consult authorization functions an uninitialized connection does not have.
    /// </remarks>
    public async Task ReopenAsync(CancellationToken cancellationToken)
    {

        if (Connection.State != System.Data.ConnectionState.Closed)
        {

            return;

        }

        await Connection.OpenAsync(cancellationToken);

        await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
            Connection,
            CovenantSqliteConnectionMode.ReadWrite,
            cancellationToken);

    }

    public async Task<bool> ObjectExistsAsync(string name, string type, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE "type" = $type AND "name" = $name
            LIMIT 1;
            """;

        _ = command.Parameters.AddWithValue("$type", type);

        _ = command.Parameters.AddWithValue("$name", name);

        object? result = await command.ExecuteScalarAsync(cancellationToken);

        return result is not null and not DBNull;

    }

    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    public async Task<long> ScalarLongAsync(string sql, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        object? result = await command.ExecuteScalarAsync(cancellationToken);

        return result is null or DBNull
            ? 0L
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);

    }

    public async Task<string?> ScalarStringAsync(string sql, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        object? result = await command.ExecuteScalarAsync(cancellationToken);

        return result is null or DBNull
            ? null
            : Convert.ToString(result, CultureInfo.InvariantCulture);

    }

    public async ValueTask DisposeAsync()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        await Connection.CloseAsync();

        await Connection.DisposeAsync();

        SqliteConnection.ClearAllPools();

        DeleteDirectory(Path.GetDirectoryName(_path)!);

    }

    /// <summary>
    /// The installation-local facts both tier initializers are handed. Every value is fixed rather
    /// than read from a clock or a secret store, so two runs of the same suite seed byte-identical
    /// rows and a diff in an assertion is a real difference.
    /// </summary>
    private static GrimoireSchemaInitializationContext CreateInitializationContext() =>
        new(
            InstallationIdentity: "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90",
            AuthorityEpoch: 1,
            MasterKeyVersion: 1,
            MasterKeyFingerprint: CreateFingerprint(),
            RecoveryEnvelopeEpoch: 1,
            InstalledAtUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// A content-free stand-in for the real 32-byte fingerprint digest. <c>covenant_state</c> checks
    /// only the length, so the bytes just have to be stable and the right count.
    /// </summary>
    private static byte[] CreateFingerprint()
    {

        byte[] fingerprint = new byte[32];

        for (int index = 0; index < fingerprint.Length; index++)
        {

            fingerprint[index] = (byte)(index + 1);

        }

        return fingerprint;

    }

    private static string ReadCoreObjectSql(string name)
    {

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.CoreObjects)
        {

            if (string.Equals(definition.Name, name, StringComparison.Ordinal))
            {

                return GrimoireSchemaCatalog.Resolve(definition, embeddingDimensions: null);

            }

        }

        throw new InvalidOperationException(
            $"The core Grimoire schema catalog declares no object named '{name}'.");

    }

    private static void DeleteDirectory(string directory)
    {

        try
        {

            if (Directory.Exists(directory))
            {

                Directory.Delete(directory, recursive: true);

            }

        }
        catch (IOException)
        {

            // Scratch under the OS temp root; a scanner still holding a handle must not fail a test
            // that has already made its assertions.

        }
        catch (UnauthorizedAccessException)
        {

            // Same.

        }

    }

    /// <summary>
    /// The scratch file's own maintenance factory, keyed exactly as every other handle to it.
    /// </summary>
    private sealed class ScratchMaintenance(CovenantSchemaScratchDatabase database)
        : IDesignTimeGrimoireConnectionFactory
    {

        public string DatabasePath => database.DatabasePath;

        public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
            database.OpenUninitializedConnectionAsync(cancellationToken);

        public async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken)
        {

            SqliteConnection connection = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = database.DatabasePath,

                    Password = Passphrase,

                    Pooling = false,

                    Mode = SqliteOpenMode.ReadOnly,

                    Cache = SqliteCacheMode.Private,
                }.ToString());

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

        public async Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(CancellationToken cancellationToken)
        {

            SqliteConnection connection = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = "file:"
                        + Path.GetFullPath(database.DatabasePath)
                        + "?immutable=1",

                    Password = Passphrase,

                    Pooling = false,

                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString());

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

        public async Task<SqliteConnection> OpenSideFileAsync(string path, CancellationToken cancellationToken)
        {

            SqliteConnection connection = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,

                    Password = Passphrase,

                    Pooling = false,
                }.ToString());

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

        public async Task AttachSideFileAsync(
            SqliteConnection connection,
            string alias,
            string path,
            CancellationToken cancellationToken)
        {

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"ATTACH DATABASE $path AS {alias} KEY $key;";

            _ = command.Parameters.AddWithValue("$path", path);

            _ = command.Parameters.AddWithValue("$key", Passphrase);

            _ = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);

        }

    }

    private async Task RecordHealthyTierAsync(
        GrimoireSchemaManifest manifest,
        CancellationToken cancellationToken)
    {

        GrimoireSchemaManifestInspector inspector =
            new(GrimoireSchemaTierOwnershipRegistry.CreateDefault());

        GrimoireSchemaInspectionResult inspected = await inspector
            .InspectAsync(Connection, transaction: null, manifest, cancellationToken)
            .ConfigureAwait(false);

        if (!inspected.IsValid || inspected.InstalledCatalogFingerprint is null)
        {

            throw new InvalidOperationException(
                "The scratch Covenant catalog was not healthy before metadata recording.");

        }

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO grimoire_feature_schemas (
                FamilyCode, TransactionTierCode, SchemaVersion, SourceDefinitionFingerprint,
                InstalledCatalogFingerprint, InstalledAtUtc, HealthCode, HealthDetailCode)
            VALUES ($family, $tier, $version, $source, $installed, $installedAt, 0, NULL);
            """;

        _ = command.Parameters.AddWithValue("$family", (long)manifest.Family);

        _ = command.Parameters.AddWithValue("$tier", (long)manifest.TransactionTier);

        _ = command.Parameters.AddWithValue("$version", manifest.Version);

        _ = command.Parameters.AddWithValue("$source", manifest.SourceDefinitionFingerprint);

        _ = command.Parameters.AddWithValue("$installed", inspected.InstalledCatalogFingerprint);

        _ = command.Parameters.AddWithValue("$installedAt", "2026-08-20T00:00:00.0000000Z");

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private async Task RunInitializerAsync(
        IGrimoireSchemaDataInitializer initializer,
        CancellationToken cancellationToken)
    {

        await using SqliteTransaction transaction =
            (SqliteTransaction)await Connection.BeginTransactionAsync(cancellationToken);

        await initializer.InitializeAsync(
            Connection,
            transaction,
            CreateInitializationContext(),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

    }

}
