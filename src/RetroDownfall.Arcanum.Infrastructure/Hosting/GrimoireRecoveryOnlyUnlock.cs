using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// One open connection to an existing catalog, physically closed when the recovery pass lets go.
/// </summary>
/// <remarks>
/// Unpooled and drained on disposal rather than merely closed. A pooled handle returns to the pool
/// still open against the pinned <c>Microsoft.Data.Sqlite</c>, which leaves <c>-wal</c> and
/// <c>-shm</c> on disk after the caller believes it let go — and the transition this connection was
/// opened to recover is about to prove exactly those files absent.
/// </remarks>
internal sealed class GrimoireRecoveryUnlockedCatalog(SqliteConnection connection) : IAsyncDisposable
{

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    internal SqliteConnection Connection => _connection;

    public async ValueTask DisposeAsync()
    {

        try
        {

            await _connection.CloseAsync().ConfigureAwait(false);

        }
        finally
        {

            await _connection.DisposeAsync().ConfigureAwait(false);

            SqliteConnection.ClearAllPools();

        }

    }

}

/// <summary>
/// Opens an existing SQLCipher catalog for recovery evidence, and can do nothing else to it.
/// </summary>
/// <remarks>
/// The ordinary bootstrap answers every one of this component's refusals by mutating: it creates a
/// database when none is there, derives from the master API key and rekeys a legacy one, promotes an
/// interrupted key-derivation upgrade, and installs schema. Each of those changes the thing a
/// recovery pass came to read. So this is a separate opener rather than a flag on that one — a flag
/// would put five mutations one boolean away from the path whose whole purpose is to perform none of
/// them.
/// </remarks>
internal interface IGrimoireRecoveryOnlyUnlock
{

    /// <summary>
    /// Opens the existing catalog at <paramref name="databasePath"/>, or refuses.
    /// </summary>
    /// <remarks>
    /// The held installation maintenance lock is asserted, never acquired and never disposed. The
    /// caller owns it for the whole of startup, and an opener that took a second one would be waiting
    /// on its own process.
    /// </remarks>
    Task<Result<GrimoireRecoveryUnlockedCatalog>> OpenExistingAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        string databasePath,
        CancellationToken cancellationToken);

}

/// <summary>The production recovery-only unlock, over the installation's own credential store.</summary>
internal sealed class GrimoireRecoveryOnlyUnlock(
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource) : IGrimoireRecoveryOnlyUnlock
{

    private readonly ISecretStore _secretStore =
        secretStore ?? throw new ArgumentNullException(nameof(secretStore));

    private readonly IGrimoireDbPassphraseSource _passphraseSource =
        passphraseSource ?? throw new ArgumentNullException(nameof(passphraseSource));

    public async Task<Result<GrimoireRecoveryUnlockedCatalog>> OpenExistingAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        string databasePath,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        SqliteNativeRuntime.Instance.Initialize();

        // An absent database under an active journal is evidence that disagrees with itself, and the
        // ordinary bootstrap would answer it by creating an empty one. Refusing is the whole point.
        if (!File.Exists(databasePath))
        {

            return Refusal();

        }

        // A pending sidecar is an interrupted PRAGMA rekey. Finishing one is a mutation of the exact
        // catalog this pass came to read, and an absent sidecar means the database is legacy — whose
        // only forward path is also a rekey.
        if (GrimoireKdfSidecarFile.PendingExists(databasePath)
            || !GrimoireKdfSidecarFile.Exists(databasePath))
        {

            return Refusal();

        }

        string passphrase;

        try
        {

            GrimoireKdfSidecar sidecar = GrimoireKdfSidecarFile.Read(databasePath);

            string? secret = await _secretStore.GetGrimoireEncryptionSecretAsync().ConfigureAwait(false);

            if (string.IsNullOrEmpty(secret) || sidecar.Version != GrimoireKeyDerivation.KdfVersion2)
            {

                return Refusal();

            }

            byte[] salt = sidecar.GetSaltBytes();

            try
            {

                passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(secret, salt);

            }
            finally
            {

                System.Security.Cryptography.CryptographicOperations.ZeroMemory(salt);

            }

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            return Refusal();

        }

        // ReadWrite rather than ReadWriteCreate, so the one mutation a connection string can perform
        // on its own is not available to this one. Unpooled so the handle this pass lets go of is a
        // handle the operating system has actually closed.
        string connectionString = new SqliteConnectionStringBuilder
        {

            DataSource = databasePath,

            Password = passphrase,

            Mode = SqliteOpenMode.ReadWrite,

            Pooling = false,

        }.ToString();

        SqliteConnection connection = new(connectionString);

        try
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // ReadOnly initialization never attempts a journal-mode change. A transition part way
            // through a compaction has a journal mode this pass has no business restating.
            await CovenantSqliteConnectionInitializer.Instance
                .InitializeAsync(connection, CovenantSqliteConnectionMode.ReadOnly, cancellationToken)
                .ConfigureAwait(false);

            await using (SqliteCommand probe = connection.CreateCommand())
            {

                probe.CommandText = "SELECT 1;";

                _ = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            await connection.DisposeAsync().ConfigureAwait(false);

            throw;

        }
        catch (Exception)
        {

            await connection.DisposeAsync().ConfigureAwait(false);

            return Refusal();

        }

        // Published only once the key has proved it opens this catalog. The handler that follows opens
        // the same database through the ordinary factory, and the bootstrap that would otherwise have
        // set the passphrase has not run.
        _passphraseSource.SetPassphrase(passphrase);

        return new GrimoireRecoveryUnlockedCatalog(connection);

    }

    private static Result<GrimoireRecoveryUnlockedCatalog> Refusal() =>
        Result<GrimoireRecoveryUnlockedCatalog>.Failure(
            new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "The existing Grimoire catalog could not be opened for recovery."));

}
