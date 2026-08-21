using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Opens the one unpooled handle a Covenant exclusive maintenance step is allowed to hold, and is the
/// only thing that knows where this installation's Grimoire is and which key opens it.
/// </summary>
/// <remarks>
/// Separate from <see cref="ICovenantConnectionSource"/> because the two answer opposite questions.
/// The source hands back the scoped Grimoire connection an ordinary read or write shares; this opens
/// a connection nothing else can be holding, which is the only kind an exclusive lock can be taken
/// on. A maintenance step that reached for the scoped connection would be asking the drain to close
/// the handle it is about to work through.
///
/// <para>Pooling is off by construction. A pooled maintenance handle would be returned to a pool on
/// close and handed back out later carrying <c>locking_mode=EXCLUSIVE</c>, which is a mode an
/// ordinary repository connection must never inherit.</para>
///
/// <para>The passphrase is never handed out. A storage-health proof needs three things that require
/// it — a sidecar-free read-only handle, a keyed side file to export into, and that side file
/// attached to a live connection — and each is a method here rather than a getter that would put the
/// Grimoire key into a caller's local variable, its stack trace, and its heap dump.</para>
/// </remarks>
internal interface ICovenantMaintenanceConnectionFactory
{

    /// <summary>
    /// The Grimoire file every handle from this factory opens.
    /// </summary>
    /// <remarks>
    /// A path rather than a connection, because a proof about a file cannot be made through a
    /// connection to it. Sidecar absence, file length, and an atomic replace are all statements about
    /// the path, and a caller that had to derive it a second way would eventually derive a different
    /// one.
    /// </remarks>
    string DatabasePath { get; }

    /// <summary>Opens one unpooled, uninitialized connection to this installation's Grimoire.</summary>
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens one distinct, unpooled, private-cache, uninitialized read-only connection to this
    /// installation's live Grimoire, including any committed write-ahead log.
    /// </summary>
    Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens one unpooled, uninitialized read-only handle that can create neither a write-ahead log
    /// nor a wal-index shared-memory file.
    /// </summary>
    /// <remarks>
    /// An ordinary read-only connection is not enough. SQLite reads a write-ahead-log database
    /// through a wal-index and creates that file when it is absent, so a plain read-only reopen undoes
    /// the sidecar absence it was opened to confirm. This handle is opened immutable instead, which
    /// builds the wal-index in heap memory and writes nothing.
    ///
    /// <para>Immutable is a promise the caller has to have earned. It tells the engine the file
    /// cannot change underneath it, so a write-ahead log that <i>did</i> exist would be ignored and
    /// the read would answer from superseded pages without saying so. It is therefore only opened
    /// after a proof that no sidecar exists, by a caller holding the exclusive gate.</para>
    /// </remarks>
    Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens one unpooled, uninitialized handle to a side file under this installation's own key.
    /// </summary>
    /// <remarks>
    /// For verifying an export before it is installed. The file has to be opened with the key the
    /// Grimoire uses, or the verification would prove a database nothing in this installation can
    /// subsequently read.
    /// </remarks>
    Task<SqliteConnection> OpenSideFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Attaches a side file to an open connection under this installation's own key.
    /// </summary>
    /// <remarks>
    /// <paramref name="alias"/> is a fixed internal identifier rather than caller input, because an
    /// attachment name is a SQL identifier and cannot be parameterized. The file name and the key
    /// both are.
    /// </remarks>
    Task AttachSideFileAsync(
        SqliteConnection connection,
        string alias,
        string path,
        CancellationToken cancellationToken);

}

/// <summary>
/// The installation's own Grimoire file, keyed by the passphrase the host already resolved.
/// </summary>
internal sealed class CovenantMaintenanceConnectionFactory(IGrimoireDbPassphraseSource passphrase)
    : ICovenantMaintenanceConnectionFactory
{

    private readonly IGrimoireDbPassphraseSource _passphrase =
        passphrase ?? throw new ArgumentNullException(nameof(passphrase));

    public string DatabasePath => ArcanumPaths.GrimoireDatabaseFile;

    public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
        OpenCoreAsync(
            new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,

                Password = _passphrase.Passphrase,

                Pooling = false,
            },
            cancellationToken);

    public Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken) =>
        OpenCoreAsync(ReadOnly(DatabasePath, _passphrase.Passphrase), cancellationToken);

    public Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(CancellationToken cancellationToken) =>
        OpenCoreAsync(SidecarFreeReadOnly(DatabasePath, _passphrase.Passphrase), cancellationToken);

    public Task<SqliteConnection> OpenSideFileAsync(string path, CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(path);

        return OpenCoreAsync(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,

                Password = _passphrase.Passphrase,

                Pooling = false,
            },
            cancellationToken);

    }

    public Task AttachSideFileAsync(
        SqliteConnection connection,
        string alias,
        string path,
        CancellationToken cancellationToken) =>
        AttachSideFileCoreAsync(connection, alias, path, _passphrase.Passphrase, cancellationToken);

    /// <summary>
    /// The connection string that opens a database read-only without creating a sidecar.
    /// </summary>
    /// <remarks>
    /// Shared with the scratch factory the suites run against, so no second opinion about these flags
    /// can exist: a test that opened its own approximation of this handle would prove the absence of
    /// sidecars a different connection string does not create.
    ///
    /// <para><c>immutable=1</c> has to travel as a URI query parameter, which is why the data source
    /// is a <c>file:</c> URI rather than a bare path.</para>
    /// </remarks>
    internal static SqliteConnectionStringBuilder SidecarFreeReadOnly(string databasePath, string passphrase) =>
        new()
        {
            DataSource = "file:" + Path.GetFullPath(databasePath) + "?immutable=1",

            Password = passphrase,

            Pooling = false,

            Mode = SqliteOpenMode.ReadOnly,
        };

    /// <summary>
    /// A live-catalog reader. Unlike <see cref="SidecarFreeReadOnly"/>, this is deliberately not
    /// immutable: a health proof must observe committed definitions still resident in the WAL.
    /// </summary>
    internal static SqliteConnectionStringBuilder ReadOnly(string databasePath, string passphrase) =>
        new()
        {
            DataSource = databasePath,

            Password = passphrase,

            Pooling = false,

            Mode = SqliteOpenMode.ReadOnly,

            Cache = SqliteCacheMode.Private,
        };

    /// <summary>Attaches one keyed side file, shared with the scratch factory for the same reason.</summary>
    internal static async Task AttachSideFileCoreAsync(
        SqliteConnection connection,
        string alias,
        string path,
        string passphrase,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentException.ThrowIfNullOrEmpty(alias);

        ArgumentException.ThrowIfNullOrEmpty(path);

        await using SqliteCommand command = connection.CreateCommand();

        // The alias is an identifier and cannot be a parameter; the file name and the key are both
        // bound, so neither is ever composed into a statement by hand.
        command.CommandText = $"ATTACH DATABASE $path AS {alias} KEY $key;";

        _ = command.Parameters.AddWithValue("$path", path);

        _ = command.Parameters.AddWithValue("$key", passphrase);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

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
