using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Diagnostics;

/// <summary>
/// Opens the encrypted Grimoire read-only for the database diagnostics.
///
/// <para>Every member fails closed and never throws: <c>arcanum doctor</c> is the command an
/// operator reaches for when the database will not open, so an unopenable database must produce a
/// finding, never an unhandled exception. The connection is opened in
/// <see cref="SqliteOpenMode.ReadOnly"/> and pooling is disabled, so a diagnostic can never take a
/// write lock, upgrade a schema, or leave a pooled handle behind for the running host to trip on.</para>
/// </summary>
internal static class GrimoireProbe
{

    internal enum OpenState
    {

        NoDatabase,

        Opened,

        MissingKey,

        Unopenable,

    }

    internal sealed record OpenResult(OpenState State, SqliteConnection? Connection, string? FailureType)
        : IAsyncDisposable
    {

        public async ValueTask DisposeAsync()
        {

            if (Connection is not null)
            {

                await Connection.DisposeAsync().ConfigureAwait(false);

            }

        }

    }

    internal static async Task<OpenResult> OpenReadOnlyAsync(
        ISecretStore secretStore,
        CancellationToken cancellationToken)
    {

        string path = ArcanumPaths.GrimoireDatabaseFile;

        if (!File.Exists(path))
        {

            return new OpenResult(OpenState.NoDatabase, null, null);

        }

        string? secret;

        try
        {

            secret = await secretStore.GetGrimoireEncryptionSecretAsync().ConfigureAwait(false);

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            return new OpenResult(OpenState.MissingKey, null, exception.GetType().Name);

        }

        if (string.IsNullOrEmpty(secret))
        {

            return new OpenResult(OpenState.MissingKey, null, null);

        }

        string? apiKey = null;

        try
        {

            SecretStoreReadResult apiKeyRead = await secretStore
                .PeekApiKeyReadResultAsync()
                .ConfigureAwait(false);

            apiKey = apiKeyRead.Status is SecretStoreReadStatus.Ok
                ? apiKeyRead.Value
                : null;

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            // The legacy api-key derivation is one candidate among several; losing it is not fatal.
        }

        IReadOnlyList<string> candidates = BuildPassphraseCandidates(path, secret, apiKey);

        if (candidates.Count == 0)
        {

            return new OpenResult(OpenState.Unopenable, null, "NoDerivableKey");

        }

        string? lastFailure = null;

        foreach (string passphrase in candidates)
        {

            SqliteConnection connection = new(
                new SqliteConnectionStringBuilder
                {

                    DataSource = path,

                    Mode = SqliteOpenMode.ReadOnly,

                    Pooling = false,

                    Password = passphrase,

                }.ToString());

            try
            {

                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                // SQLCipher defers key verification to the first page read, so an open alone proves
                // nothing. One trivial read is what actually distinguishes "wrong key" from "usable".
                await using SqliteCommand probe = connection.CreateCommand();

                probe.CommandText = "SELECT count(*) FROM sqlite_master;";

                _ = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                return new OpenResult(OpenState.Opened, connection, null);

            }
            catch (OperationCanceledException)
            {

                await connection.DisposeAsync().ConfigureAwait(false);

                throw;

            }
            catch (Exception exception)
            {

                await connection.DisposeAsync().ConfigureAwait(false);

                lastFailure = exception.GetType().Name;

            }

        }

        return new OpenResult(OpenState.Unopenable, null, lastFailure);

    }

    /// <summary>
    /// Every passphrase this database could legitimately be encrypted under, in the order
    /// <c>GrimoireDatabaseBootstrapper</c> would resolve them.
    ///
    /// <para>Deriving only the committed-sidecar (PBKDF2) form would report a perfectly healthy
    /// installation as unopenable in two real states: one created before the KDF upgrade, which has
    /// no sidecar at all and is still on the legacy HKDF derivation, and one whose upgrade was
    /// interrupted, which has only a <c>.kdf.pending</c> salt. Both are recoverable states the host
    /// handles on its next start, so a diagnostic that called them "the encryption secret could not
    /// be read" and told the operator to restore from backup would be actively dangerous advice.</para>
    ///
    /// <para>Trying candidates is not a brute force: each is a derivation this installation's own
    /// key material supports, and a wrong one simply fails to decrypt page one.</para>
    /// </summary>
    private static IReadOnlyList<string> BuildPassphraseCandidates(
        string databasePath,
        string secret,
        string? apiKey)
    {

        List<string> candidates = [];

        AddSidecarCandidate(candidates, secret, () => GrimoireKdfSidecarFile.Exists(databasePath)
            ? GrimoireKdfSidecarFile.Read(databasePath)
            : null);

        AddSidecarCandidate(candidates, secret, () => GrimoireKdfSidecarFile.PendingExists(databasePath)
            ? GrimoireKdfSidecarFile.ReadPending(databasePath)
            : null);

        TryAdd(candidates, () => GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecretLegacy(secret));

        if (!string.IsNullOrEmpty(apiKey))
        {

            TryAdd(candidates, () => GrimoireKeyDerivation.DerivePassphraseFromApiKeyLegacy(apiKey));

        }

        return candidates;

    }

    private static void AddSidecarCandidate(
        List<string> candidates,
        string secret,
        Func<GrimoireKdfSidecar?> read)
    {

        byte[]? salt = null;

        try
        {

            salt = read()?.GetSaltBytes();

            if (salt is null)
            {

                return;

            }

            candidates.Add(GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(secret, salt));

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            // An unreadable or unsupported sidecar drops one candidate; the others still apply.
        }
        finally
        {

            if (salt is not null)
            {

                CryptographicOperations.ZeroMemory(salt);

            }

        }

    }

    private static void TryAdd(List<string> candidates, Func<string> derive)
    {

        try
        {

            candidates.Add(derive());

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            // Not derivable from this key material; the remaining candidates still apply.
        }

    }

    /// <summary>
    /// The same failure description for a caller whose subject is not the database itself — the
    /// durable-operation checks, which live in the Grimoire but are not about it. A missing database
    /// is <see cref="DoctorOutcome.Skipped"/> for them rather than a finding of their own, and an
    /// unopenable one defers to <c>grimoire.key_material</c> instead of repeating its remedy.
    /// </summary>
    internal static DoctorFinding DescribeUnreadable(OpenResult result, string becauseClause) =>
        result.State == OpenState.NoDatabase
            ? new DoctorFinding(
                DoctorOutcome.Skipped,
                $"No Grimoire database exists yet, {becauseClause}.")
            : new DoctorFinding(
                DoctorOutcome.Unavailable,
                "The Grimoire database could not be opened, so this state could not be read. "
                + "'grimoire.key_material' and 'grimoire.integrity' report why.");

    internal static DoctorFinding DescribeUnopenable(OpenResult result) => result.State switch
    {
        OpenState.NoDatabase => new DoctorFinding(
            DoctorOutcome.Skipped,
            "No Grimoire database exists yet; it is created on the first 'arcanum serve'."),

        OpenState.MissingKey => new DoctorFinding(
            DoctorOutcome.Unhealthy,
            "The Grimoire database exists but its encryption secret could not be read, so it cannot "
            + "be opened. Arcanum will not derive a replacement key over existing ciphertext.",
            [
                new DoctorRemedy(
                    "grimoire.restore_key",
                    null,
                    DoctorRemedyCommands.BackupRestore,
                    "Restore the secret store and Data Protection key ring alongside the database. A new key cannot read the existing pages."),
            ]),

        _ => new DoctorFinding(
            DoctorOutcome.Unhealthy,
            $"The Grimoire database could not be opened ({result.FailureType ?? "unknown failure"}). "
            + "The most common causes are a mismatched encryption secret and a truncated file.",
            [
                new DoctorRemedy(
                    "grimoire.restore_database",
                    null,
                    DoctorRemedyCommands.BackupRestore,
                    "Restore the Grimoire from a verified backup. Take a copy of the current file first if it may hold unbacked-up data."),
            ]),
    };

}

/// <summary>
/// Whether the encryption secret and the KDF sidecar that together open the Grimoire are coherent.
/// Runs before the integrity checks because it explains their failure: without this, an unopenable
/// database and a stranded key upgrade look identical.
/// </summary>
public sealed class GrimoireKeyMaterialCheck(ISecretStore secretStore) : IDoctorCheck
{

    public const string CheckId = "grimoire.key_material";

    public string Id => CheckId;

    public string Name => "Grimoire key material";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Grimoire;

    public bool RequiresNetwork => false;

    public async Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        string path = ArcanumPaths.GrimoireDatabaseFile;

        if (!File.Exists(path))
        {

            return new DoctorFinding(
                DoctorOutcome.Skipped,
                "No Grimoire database exists yet, so there is no key material to check.");

        }

        SecretStoreReadResult secret;

        try
        {

            secret = await secretStore.GetGrimoireEncryptionSecretReadResultAsync().ConfigureAwait(false);

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            return new DoctorFinding(
                DoctorOutcome.Unavailable,
                $"The Grimoire encryption secret could not be consulted ({exception.GetType().Name}).");

        }

        if (secret.Status == SecretStoreReadStatus.Corrupted)
        {

            return new DoctorFinding(
                DoctorOutcome.Unhealthy,
                "The stored Grimoire encryption secret exists but cannot be decrypted. The database "
                + "cannot be opened, and no replacement key is generated while ciphertext exists.",
                [
                    new DoctorRemedy(
                        "grimoire.restore_key",
                        null,
                        DoctorRemedyCommands.BackupRestore,
                        "Restore the secret store and Data Protection key ring together with the database."),
                ]);

        }

        if (secret.Status != SecretStoreReadStatus.Ok)
        {

            return new DoctorFinding(
                DoctorOutcome.Unhealthy,
                "A Grimoire database exists but no encryption secret is stored, so it cannot be opened.",
                [
                    new DoctorRemedy(
                        "grimoire.restore_key",
                        null,
                        DoctorRemedyCommands.BackupRestore,
                        "Restore the secret store that holds this database's key. A new key cannot read the existing pages."),
                ]);

        }

        if (GrimoireKdfSidecarFile.PendingExists(path))
        {

            return new DoctorFinding(
                DoctorOutcome.Degraded,
                "A pending key-derivation upgrade is stranded next to the database. The host promotes "
                + "it on the next start once it verifies the database opens with the new salt.",
                [
                    new DoctorRemedy(
                        "grimoire.complete_kdf_upgrade",
                        null,
                        DoctorRemedyCommands.Serve,
                        "Start the host once so it can verify and promote the pending key-derivation upgrade."),
                ]);

        }

        return new DoctorFinding(
            DoctorOutcome.Healthy,
            GrimoireKdfSidecarFile.Exists(path)
                ? "The encryption secret is readable and the key-derivation sidecar is current. No key value is read by this check."
                : "The encryption secret is readable. No key value is read by this check.");

    }

}

/// <summary>
/// <c>PRAGMA quick_check</c> on the encrypted Grimoire. There is deliberately no repair: corruption
/// inside an authenticated encrypted database is not something a diagnostic can safely rewrite, and
/// the only correct next step is a verified restore.
/// </summary>
public sealed class GrimoireIntegrityCheck(ISecretStore secretStore) : IDoctorCheck
{

    public const string CheckId = "grimoire.integrity";

    public string Id => CheckId;

    public string Name => "Grimoire integrity";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Grimoire;

    public bool RequiresNetwork => false;

    public async Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        await using GrimoireProbe.OpenResult opened =
            await GrimoireProbe.OpenReadOnlyAsync(secretStore, cancellationToken).ConfigureAwait(false);

        if (opened.State != GrimoireProbe.OpenState.Opened)
        {

            return GrimoireProbe.DescribeUnopenable(opened);

        }

        await using SqliteCommand command = opened.Connection!.CreateCommand();

        command.CommandText = "PRAGMA quick_check;";

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(result as string, "ok", StringComparison.OrdinalIgnoreCase))
        {

            return new DoctorFinding(DoctorOutcome.Healthy, "PRAGMA quick_check reported ok.");

        }

        return new DoctorFinding(
            DoctorOutcome.Unhealthy,
            "PRAGMA quick_check reported structural damage in the Grimoire database.",
            [
                new DoctorRemedy(
                    "grimoire.restore_from_backup",
                    null,
                    DoctorRemedyCommands.BackupCreateGrimoire,
                    "Snapshot the damaged database first, then restore a known-good archive with 'arcanum backup restore'."),
            ]);

    }

}

/// <summary>
/// <c>PRAGMA foreign_key_check</c>. Orphan rows are <see cref="DoctorOutcome.Degraded"/> rather than
/// unhealthy: the database still opens and serves, and deleting the offending rows is a data
/// decision that belongs to the operator, not to a diagnostic.
/// </summary>
public sealed class GrimoireForeignKeyCheck(ISecretStore secretStore) : IDoctorCheck
{

    public const string CheckId = "grimoire.foreign_keys";

    public string Id => CheckId;

    public string Name => "Grimoire referential integrity";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Grimoire;

    public bool RequiresNetwork => false;

    public async Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        await using GrimoireProbe.OpenResult opened =
            await GrimoireProbe.OpenReadOnlyAsync(secretStore, cancellationToken).ConfigureAwait(false);

        if (opened.State != GrimoireProbe.OpenState.Opened)
        {

            return GrimoireProbe.DescribeUnopenable(opened);

        }

        await using SqliteCommand command = opened.Connection!.CreateCommand();

        command.CommandText = "PRAGMA foreign_key_check;";

        HashSet<string> tables = new(StringComparer.Ordinal);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (!reader.IsDBNull(0))
            {

                tables.Add(reader.GetString(0));

            }

        }

        if (tables.Count == 0)
        {

            return new DoctorFinding(DoctorOutcome.Healthy, "PRAGMA foreign_key_check reported no orphan rows.");

        }

        return new DoctorFinding(
            DoctorOutcome.Degraded,
            $"PRAGMA foreign_key_check reported orphan rows in: {string.Join(", ", tables.Order(StringComparer.Ordinal))}. "
            + "The database still opens; no automatic repair deletes user rows.",
            [
                new DoctorRemedy(
                    "grimoire.snapshot_before_repair",
                    null,
                    DoctorRemedyCommands.BackupCreateGrimoire,
                    "Snapshot the database, then report the named tables so the owning subsystem can be corrected."),
            ]);

    }

}

/// <summary>
/// The size of the write-ahead log beside the database. A WAL that never shrinks means no clean
/// shutdown has checkpointed it; there is no repair because truncating a log that a running host
/// still owns is not something a diagnostic may do.
/// </summary>
public sealed class GrimoireWalCheck : IDoctorCheck
{

    public const string CheckId = "grimoire.wal_size";

    /// <summary>A WAL is normally a few MiB; beyond this, no clean shutdown has happened in a long while.</summary>
    internal const long DegradedBytes = 512L * 1024 * 1024;

    public string Id => CheckId;

    public string Name => "Grimoire write-ahead log";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Grimoire;

    public bool RequiresNetwork => false;

    public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        if (!File.Exists(ArcanumPaths.GrimoireDatabaseFile))
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Skipped,
                "No Grimoire database exists yet, so it has no write-ahead log."));

        }

        string wal = ArcanumPaths.GrimoireDatabaseFile + "-wal";

        if (!File.Exists(wal))
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Skipped,
                "No write-ahead log is present; the last shutdown checkpointed cleanly."));

        }

        long bytes;

        try
        {

            bytes = new FileInfo(wal).Length;

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Unavailable,
                "The write-ahead log could not be measured."));

        }

        string readable = $"{bytes / (1024.0 * 1024):F1} MiB";

        if (bytes < DegradedBytes)
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Healthy,
                $"The write-ahead log is {readable}."));

        }

        return Task.FromResult(new DoctorFinding(
            DoctorOutcome.Degraded,
            $"The write-ahead log has grown to {readable}, which means no clean host shutdown has "
            + "checkpointed it recently.",
            [
                new DoctorRemedy(
                    "grimoire.checkpoint_wal",
                    null,
                    DoctorRemedyCommands.DaemonStatus,
                    "Stop the host cleanly; shutdown truncates the log. Doctor will not checkpoint a log a live host may own."),
            ]));

    }

}
