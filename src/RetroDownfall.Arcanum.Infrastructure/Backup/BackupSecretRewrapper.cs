using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Backup;

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

/// <summary>The destination's own secret material, captured so a rollback can reinstate it.</summary>
internal sealed record BackupSecretSnapshot(
    string? GrimoireSecret,
    string? FileEncryptionSecret,
    string? MasterApiKey);

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

            await secretStore.SaveGrimoireEncryptionSecretAsync(grimoireSecret).ConfigureAwait(false);

            if (keyRing is not null)
            {

                await secretStore.SaveFileEncryptionSecretAsync(keyRing).ConfigureAwait(false);

            }

            List<BackupVerifyIssue> issues = [];

            bool masterWritten = false;

            if (restoreMasterApiKey)
            {

                if (recovery.MasterApiKeyUtf8 is { Length: > 0 } masterBytes)
                {

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

            if (existing.Status == SecretStoreReadStatus.Corrupted)
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

            if (existingEncoded.StartsWith(KeyRingHeader + "\n", StringComparison.Ordinal))
            {

                string[] lines = existingEncoded.Split('\n', StringSplitOptions.RemoveEmptyEntries);

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
            grimoire.Status == SecretStoreReadStatus.Ok ? grimoire.Value : null,
            fileKeys.Status == SecretStoreReadStatus.Ok ? fileKeys.Value : null,
            apiKey.Status == SecretStoreReadStatus.Ok ? apiKey.Value : null);

    }

    /// <summary>Best-effort reinstatement of a captured snapshot during rollback.</summary>
    public async Task RestoreAsync(BackupSecretSnapshot snapshot)
    {

        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {

            if (snapshot.GrimoireSecret is not null)
            {

                await secretStore
                    .SaveGrimoireEncryptionSecretAsync(snapshot.GrimoireSecret)
                    .ConfigureAwait(false);

            }

            if (snapshot.FileEncryptionSecret is not null)
            {

                await secretStore
                    .SaveFileEncryptionSecretAsync(snapshot.FileEncryptionSecret)
                    .ConfigureAwait(false);

            }

            if (snapshot.MasterApiKey is not null)
            {

                await secretStore.SaveApiKeyAsync(snapshot.MasterApiKey).ConfigureAwait(false);

            }

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or CryptographicException)
        {

        }

    }

    private static BackupSecretRewrapResult InvalidMaterial() =>
        BackupSecretRewrapResult.Failed(
            new BackupVerifyIssue(
                "backup.restore_recovery_material_invalid",
                "The portable recovery material is missing, unsupported, or internally inconsistent.",
                BackupArchivePaths.PortableRecoveryKeys));

}
