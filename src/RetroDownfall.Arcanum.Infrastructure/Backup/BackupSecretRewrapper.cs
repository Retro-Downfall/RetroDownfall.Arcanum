using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed record BackupSecretRewrapResult(
    bool GrimoireSecretWritten,
    int FileEncryptionKeysWritten,
    bool MasterApiKeyWritten,
    BackupVerifyIssue[] Issues)
{
    public static BackupSecretRewrapResult Failed(BackupVerifyIssue issue) =>
        new(
            GrimoireSecretWritten: false,
            FileEncryptionKeysWritten: 0,
            MasterApiKeyWritten: false,
            [issue]);
}

/// <summary>
/// One of the destination's secrets as it stood before the restore: its value, proof that it did not
/// exist, or neither.
/// </summary>
/// <remarks>
/// Absence is a value of its own because a rollback has to undo a write over nothing by deleting
/// what the restore created. Only <see cref="SecretStoreReadStatus.Ok"/> and
/// <see cref="SecretStoreReadStatus.Missing"/> are proof of anything; a corrupted or unreadable read
/// says nothing about what is there, so it is neither reinstated nor deleted.
/// </remarks>
/// <param name="Description">Names the secret in messages, e.g. "the master API key".</param>
internal sealed record BackupCapturedSecret(
    string Description,
    SecretStoreReadStatus Status,
    string? Value)
{
    public bool IsPresent => Status == SecretStoreReadStatus.Ok && Value is not null;

    public bool IsAbsent => Status == SecretStoreReadStatus.Missing;

    /// <summary>True when a rollback can return this secret to its prior state, one way or the other.</summary>
    public bool IsReinstatable => IsPresent || IsAbsent;

    public static BackupCapturedSecret From(string description, SecretStoreReadResult read) =>
        new(
            description,
            read.Status,
            read.Status == SecretStoreReadStatus.Ok ? read.Value : null);
}

/// <summary>The destination's own secret material, captured so a rollback can reinstate it.</summary>
internal sealed record BackupSecretSnapshot(
    BackupCapturedSecret GrimoireSecret,
    BackupCapturedSecret FileEncryptionSecret,
    BackupCapturedSecret MasterApiKey)
{
    /// <summary>
    /// The first secret a replacement restore may overwrite whose prior state a rollback could not
    /// reinstate, or <see langword="null"/> when every one of them can be.
    /// </summary>
    /// <remarks>
    /// The master API key is only overwritten on explicit request, so its prior state only matters
    /// then. The Grimoire secret is always written, and the key ring is written whenever the archive
    /// carries keys, which is not known until after the capture.
    /// </remarks>
    public BackupCapturedSecret? FirstUnreinstatable(bool restoreMasterApiKey)
    {
        if (!GrimoireSecret.IsReinstatable)
        {
            return GrimoireSecret;
        }

        if (!FileEncryptionSecret.IsReinstatable)
        {
            return FileEncryptionSecret;
        }

        return restoreMasterApiKey && !MasterApiKey.IsReinstatable ? MasterApiKey : null;
    }
}

/// <summary>
/// Rebuilds machine-local secret protection from a backup's portable recovery payload.
/// </summary>
/// <remarks>
/// This is what makes a backup restorable on a *clean* machine: the archive carries the Grimoire
/// encryption secret and the referenced file-encryption keys in a portable wrapped form, and this
/// re-wraps them with the destination platform's own Data Protection / credential-store material.
/// The source machine's OS credential store is never consulted. The master API key is separate and
/// off by default — it authenticates callers to this installation, and adopting the old one on a new
/// machine is a decision, not a side effect of restoring data.
/// </remarks>
internal sealed class BackupSecretRewrapper(ISecretStore secretStore)
{
    private const string KeyRingHeader = "ARCANUM-KEYRING-1";

    private const long MaximumRecoveryBytes = 8L * 1024 * 1024;

    private const string GrimoireSecretDescription = "the Grimoire encryption secret";

    private const string FileEncryptionSecretDescription = "the file-encryption key ring";

    private const string MasterApiKeyDescription = "the master API key";

    // What this instance has written, or tried to: a write that threw may still have landed in part,
    // so the flag is raised before the call. A rollback reinstates exactly these and nothing else.
    private bool _grimoireSecretWritten;

    private bool _fileEncryptionSecretWritten;

    private bool _masterApiKeyWritten;

    public async Task<BackupSecretRewrapResult> RewrapAsync(
        string portableRecoveryPath,
        bool restoreMasterApiKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portableRecoveryPath);

        string fullPath = Path.GetFullPath(portableRecoveryPath);

        if (!File.Exists(fullPath))
        {
            return BackupSecretRewrapResult.Failed(
                new BackupVerifyIssue(
                    "backup.restore_recovery_material_missing",
                    "The archive does not carry the portable recovery material required to rebuild "
                    + "local secret protection.",
                    BackupArchivePaths.PortableRecoveryKeys));
        }

        if (new FileInfo(fullPath).Length > MaximumRecoveryBytes)
        {
            return BackupSecretRewrapResult.Failed(
                new BackupVerifyIssue(
                    "backup.restore_recovery_material_invalid",
                    "The portable recovery material is larger than the supported bound.",
                    BackupArchivePaths.PortableRecoveryKeys));
        }

        byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);

        PortableBackupRecoveryMaterial? recovery = null;

        try
        {
            try
            {
                recovery = JsonSerializer.Deserialize(
                    bytes,
                    BackupJsonContext.Default.PortableBackupRecoveryMaterial);
            }
            catch (Exception exception) when (
                exception is JsonException or NotSupportedException)
            {
                return InvalidMaterial();
            }

            if (recovery is null
                || recovery.Version != 1
                || recovery.GrimoireEncryptionSecretUtf8.Length == 0)
            {
                return InvalidMaterial();
            }

            if (!TryBuildKeyRing(recovery, out string? keyRing, out int keyCount))
            {
                return InvalidMaterial();
            }

            string grimoireSecret = Encoding.UTF8.GetString(recovery.GrimoireEncryptionSecretUtf8);

            _grimoireSecretWritten = true;

            await secretStore.SaveGrimoireEncryptionSecretAsync(grimoireSecret).ConfigureAwait(false);

            if (keyRing is not null)
            {
                _fileEncryptionSecretWritten = true;

                await secretStore.SaveFileEncryptionSecretAsync(keyRing).ConfigureAwait(false);
            }

            List<BackupVerifyIssue> issues = [];

            bool masterWritten = false;

            if (restoreMasterApiKey)
            {
                if (recovery.MasterApiKeyUtf8 is { Length: > 0 } masterBytes)
                {
                    _masterApiKeyWritten = true;

                    await secretStore
                        .SaveApiKeyAsync(Encoding.UTF8.GetString(masterBytes))
                        .ConfigureAwait(false);

                    masterWritten = true;
                }
                else
                {
                    issues.Add(new BackupVerifyIssue(
                        "backup.restore_master_api_key_absent",
                        "Restoring the master API key was requested, but the archive does not carry "
                        + "one. Set a key on this machine with `arcanum key set`.",
                        BackupArchivePaths.MasterApiKey));
                }
            }

            return new BackupSecretRewrapResult(
                GrimoireSecretWritten: true,
                keyCount,
                masterWritten,
                [.. issues]);
        }
        finally
        {
            recovery?.Dispose();

            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Renders the destination-local key-ring representation, re-deriving every key id from its own
    /// bytes so a tampered payload cannot install a key under a name the blob store already trusts.
    /// </summary>
    private static bool TryBuildKeyRing(
        PortableBackupRecoveryMaterial recovery,
        out string? keyRing,
        out int keyCount)
    {
        keyRing = null;

        keyCount = 0;

        if (recovery.FileEncryptionKeys.Length == 0)
        {
            return recovery.ActiveFileEncryptionKeyId is null;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);

        StringBuilder builder = new();

        _ = builder.Append(KeyRingHeader).Append('\n');

        string active = recovery.ActiveFileEncryptionKeyId
            ?? recovery.FileEncryptionKeys[0].KeyId;

        _ = builder.Append("active=").Append(active).Append('\n');

        foreach (PortableBackupFileKey key in recovery.FileEncryptionKeys)
        {
            if (key.KeyBytes.Length != 32
                || !string.Equals(ComputeKeyId(key.KeyBytes), key.KeyId, StringComparison.Ordinal)
                || !seen.Add(key.KeyId))
            {
                return false;
            }

            _ = builder
                .Append(key.KeyId)
                .Append('=')
                .Append(Convert.ToBase64String(key.KeyBytes))
                .Append('\n');
        }

        if (!seen.Contains(active))
        {
            return false;
        }

        keyRing = builder.ToString();

        keyCount = recovery.FileEncryptionKeys.Length;

        return true;
    }

    private static string ComputeKeyId(ReadOnlySpan<byte> key)
    {
        Span<byte> digest = stackalloc byte[32];

        SHA256.HashData(key, digest);

        string id = Convert.ToHexString(digest[..8]).ToLowerInvariant();

        CryptographicOperations.ZeroMemory(digest);

        return id;
    }

    /// <summary>
    /// Adds the archive's file-encryption keys to this machine's ring without disturbing the active
    /// key. Importing Sessions brings ciphertext that only those keys can open, but the destination
    /// keeps writing new blobs under its own active key.
    /// </summary>
    public async Task<BackupSecretRewrapResult> MergeFileEncryptionKeysAsync(
        string portableRecoveryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portableRecoveryPath);

        string fullPath = Path.GetFullPath(portableRecoveryPath);

        if (!File.Exists(fullPath) || new FileInfo(fullPath).Length > MaximumRecoveryBytes)
        {
            return BackupSecretRewrapResult.Failed(
                new BackupVerifyIssue(
                    "backup.restore_recovery_material_missing",
                    "The archive does not carry the portable recovery material required to read its "
                    + "encrypted attachment bytes.",
                    BackupArchivePaths.PortableRecoveryKeys));
        }

        byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);

        PortableBackupRecoveryMaterial? recovery = null;

        try
        {
            try
            {
                recovery = JsonSerializer.Deserialize(
                    bytes,
                    BackupJsonContext.Default.PortableBackupRecoveryMaterial);
            }
            catch (Exception exception) when (
                exception is JsonException or NotSupportedException)
            {
                return InvalidMaterial();
            }

            if (recovery is null || recovery.Version != 1)
            {
                return InvalidMaterial();
            }

            if (recovery.FileEncryptionKeys.Length == 0)
            {
                return new BackupSecretRewrapResult(
                    GrimoireSecretWritten: false,
                    FileEncryptionKeysWritten: 0,
                    MasterApiKeyWritten: false,
                    []);
            }

            SecretStoreReadResult existing = await secretStore
                .GetFileEncryptionSecretReadResultAsync()
                .ConfigureAwait(false);

            if (existing.Status is SecretStoreReadStatus.Corrupted or SecretStoreReadStatus.Unreadable)
            {
                return BackupSecretRewrapResult.Failed(
                    new BackupVerifyIssue(
                        "backup.restore_key_ring_unreadable",
                        "This machine's file-encryption key ring could not be read, so imported keys "
                        + "cannot be merged into it."));
            }

            if (!TryMergeRing(existing.Value, recovery, out string? merged, out int added))
            {
                return InvalidMaterial();
            }

            _fileEncryptionSecretWritten = true;

            await secretStore.SaveFileEncryptionSecretAsync(merged).ConfigureAwait(false);

            return new BackupSecretRewrapResult(
                GrimoireSecretWritten: false,
                added,
                MasterApiKeyWritten: false,
                []);
        }
        finally
        {
            recovery?.Dispose();

            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryMergeRing(
        string? existingEncoded,
        PortableBackupRecoveryMaterial recovery,
        out string merged,
        out int added)
    {
        merged = string.Empty;

        added = 0;

        Dictionary<string, string> keys = new(StringComparer.Ordinal);

        string? active = null;

        if (!string.IsNullOrEmpty(existingEncoded))
        {
            // The canonical encoding is LF-delimited, but a ring persisted by an older Windows build
            // used Environment.NewLine and both FileEncryptionKeyProvider and
            // BackupSecretSnapshotReader deliberately still load it — so this must too. A machine
            // whose ring carries CRLF is running normally, and refusing it here fails the one path
            // that exists to repair such a machine. The trailing '\r' has to come off every line, not
            // just the header: Convert.FromBase64String skips it, so the keys would decode while the
            // active id carried an invisible '\r' and matched none of them.
            if (existingEncoded.StartsWith(KeyRingHeader + "\n", StringComparison.Ordinal)
                || existingEncoded.StartsWith(KeyRingHeader + "\r\n", StringComparison.Ordinal))
            {
                string[] lines = existingEncoded
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(static line => line.TrimEnd('\r'))
                    .Where(static line => line.Length > 0)
                    .ToArray();

                if (lines.Length < 2 || !lines[1].StartsWith("active=", StringComparison.Ordinal))
                {
                    return false;
                }

                active = lines[1]["active=".Length..];

                foreach (string line in lines[2..])
                {
                    int separator = line.IndexOf('=', StringComparison.Ordinal);

                    if (separator <= 0)
                    {
                        return false;
                    }

                    keys[line[..separator]] = line[(separator + 1)..];
                }
            }
            else
            {
                byte[] legacy;

                try
                {
                    legacy = Convert.FromBase64String(existingEncoded);
                }
                catch (FormatException)
                {
                    return false;
                }

                if (legacy.Length != 32)
                {
                    CryptographicOperations.ZeroMemory(legacy);

                    return false;
                }

                active = ComputeKeyId(legacy);

                keys[active] = Convert.ToBase64String(legacy);

                CryptographicOperations.ZeroMemory(legacy);
            }
        }

        foreach (PortableBackupFileKey key in recovery.FileEncryptionKeys)
        {
            if (key.KeyBytes.Length != 32
                || !string.Equals(ComputeKeyId(key.KeyBytes), key.KeyId, StringComparison.Ordinal))
            {
                return false;
            }

            if (keys.ContainsKey(key.KeyId))
            {
                continue;
            }

            keys[key.KeyId] = Convert.ToBase64String(key.KeyBytes);

            added++;
        }

        active ??= recovery.ActiveFileEncryptionKeyId ?? recovery.FileEncryptionKeys[0].KeyId;

        if (!keys.ContainsKey(active))
        {
            return false;
        }

        StringBuilder builder = new();

        _ = builder.Append(KeyRingHeader).Append('\n');

        _ = builder.Append("active=").Append(active).Append('\n');

        foreach ((string keyId, string encoded) in keys.OrderBy(
                     static pair => pair.Key,
                     StringComparer.Ordinal))
        {
            _ = builder.Append(keyId).Append('=').Append(encoded).Append('\n');
        }

        merged = builder.ToString();

        return true;
    }

    /// <summary>
    /// Captures this machine's current secret material so a rolled-back restore can put it back.
    /// On platforms where the secret store lives outside the Grimoire tree, reversing the directory
    /// swap alone would leave the restored secrets attached to the old database.
    /// </summary>
    public async Task<BackupSecretSnapshot> CaptureAsync()
    {
        SecretStoreReadResult grimoire = await secretStore
            .GetGrimoireEncryptionSecretReadResultAsync()
            .ConfigureAwait(false);

        SecretStoreReadResult fileKeys = await secretStore
            .GetFileEncryptionSecretReadResultAsync()
            .ConfigureAwait(false);

        SecretStoreReadResult apiKey = await secretStore
            .GetApiKeyReadResultAsync()
            .ConfigureAwait(false);

        return new BackupSecretSnapshot(
            BackupCapturedSecret.From(GrimoireSecretDescription, grimoire),
            BackupCapturedSecret.From(FileEncryptionSecretDescription, fileKeys),
            BackupCapturedSecret.From(MasterApiKeyDescription, apiKey));
    }

    /// <summary>
    /// Returns every secret this instance wrote to its captured state: the prior value where there
    /// was one, and no secret at all where the capture proved there was none.
    /// </summary>
    /// <remarks>
    /// Every secret is attempted even after one fails, so a single failure costs only that secret,
    /// and the failure names each secret that was not reinstated. A secret whose prior state the
    /// capture could not read is reported rather than guessed at: it is never deleted, because an
    /// unreadable credential may still exist.
    /// </remarks>
    public async Task<Result> RestoreAsync(BackupSecretSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        List<string> failures = [];

        if (_grimoireSecretWritten)
        {
            await ReinstateAsync(
                    snapshot.GrimoireSecret,
                    secretStore.SaveGrimoireEncryptionSecretAsync,
                    secretStore.DeleteGrimoireEncryptionSecretAsync,
                    failures)
                .ConfigureAwait(false);
        }

        if (_fileEncryptionSecretWritten)
        {
            await ReinstateAsync(
                    snapshot.FileEncryptionSecret,
                    secretStore.SaveFileEncryptionSecretAsync,
                    secretStore.DeleteFileEncryptionSecretAsync,
                    failures)
                .ConfigureAwait(false);
        }

        if (_masterApiKeyWritten)
        {
            await ReinstateAsync(
                    snapshot.MasterApiKey,
                    secretStore.SaveApiKeyAsync,
                    secretStore.DeleteApiKeyAsync,
                    failures)
                .ConfigureAwait(false);
        }

        return failures.Count == 0
            ? Result.Success()
            : Result.Failure(new Error(
                "backup.restore_secret_reinstatement_failed",
                "The prior installation's local secrets could not all be reinstated: "
                + string.Join("; ", failures)
                + "."));
    }

    private static async Task ReinstateAsync(
        BackupCapturedSecret prior,
        Func<string, Task> save,
        Func<Task> delete,
        List<string> failures)
    {
        try
        {
            if (prior.IsPresent)
            {
                await save(prior.Value!).ConfigureAwait(false);
            }
            else if (prior.IsAbsent)
            {
                await delete().ConfigureAwait(false);
            }
            else
            {
                failures.Add($"{prior.Description} (its prior state could not be read: {prior.Status})");
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidOperationException
                or CryptographicException)
        {
            failures.Add($"{prior.Description} ({exception.GetType().Name})");
        }
    }

    private static BackupSecretRewrapResult InvalidMaterial() =>
        BackupSecretRewrapResult.Failed(
            new BackupVerifyIssue(
                "backup.restore_recovery_material_invalid",
                "The portable recovery material is missing, unsupported, or internally inconsistent.",
                BackupArchivePaths.PortableRecoveryKeys));
}
