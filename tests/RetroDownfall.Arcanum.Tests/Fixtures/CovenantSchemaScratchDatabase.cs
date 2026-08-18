using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

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
    /// The database file and every sidecar WAL mode can leave beside it. Deleting only the database
    /// leaves two orphans per test under the temp directory.
    /// </summary>
    private static readonly string[] Sidecars = ["", "-wal", "-shm", "-journal"];

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

        string path = Path.Combine(Path.GetTempPath(), $"covenant-schema-{Guid.NewGuid():N}.db");

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

            DeleteFiles(path);

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

        DeleteFiles(_path);

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

    private static void DeleteFiles(string path)
    {

        foreach (string suffix in Sidecars)
        {

            try
            {

                string sidecar = path + suffix;

                if (File.Exists(sidecar))
                {

                    File.Delete(sidecar);

                }

            }
            catch (IOException)
            {

                // Scratch under the OS temp root; a scanner still holding the handle must not fail
                // a test that has already made its assertions.

            }
            catch (UnauthorizedAccessException)
            {

                // Same.

            }

        }

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
