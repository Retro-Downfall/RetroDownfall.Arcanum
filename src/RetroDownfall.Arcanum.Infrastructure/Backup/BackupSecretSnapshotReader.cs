using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal interface IBackupSecretSnapshotReader
{
    Task<SecretStoreReadResult> ReadGrimoireSecretAsync();

    Task<SecretStoreReadResult> ReadFileEncryptionKeysAsync();

    Task<SecretStoreReadResult> ReadMasterApiKeyAsync();
}

internal sealed class BackupSecretSnapshotReader(
    IOsCredentialStore osStore,
    DataProtectionSecretStore dataProtectionStore) : IBackupSecretSnapshotReader
{
    public Task<SecretStoreReadResult> ReadGrimoireSecretAsync() =>
        dataProtectionStore.GetGrimoireEncryptionSecretReadResultAsync();

    public Task<SecretStoreReadResult> ReadFileEncryptionKeysAsync() =>
        ReadWithoutHealingAsync(
            ArcanumCredentialIdentity.FileEncryptionKeyAccount,
            ArcanumPaths.FileEncryptionKeyStoreFile,
            "the file-encryption key ring",
            OsKeychainSecretStore.FileEncryptionKeyRecoveryHint,
            dataProtectionStore.GetFileEncryptionSecretReadResultAsync);

    public Task<SecretStoreReadResult> ReadMasterApiKeyAsync() =>
        ReadWithoutHealingAsync(
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            ArcanumPaths.ApiKeyStoreFile,
            "the master API key",
            OsKeychainSecretStore.MasterApiKeyRecoveryHint,
            dataProtectionStore.GetApiKeyReadResultAsync);

    /// <summary>
    /// Prefers the OS copy and otherwise answers from the mirror, repairing neither. The mirror is
    /// refused while it is marked stale (DESIGN §11.2 item 4): exporting it would archive a superseded,
    /// possibly revoked, master key or a key ring without its active key, and a restore would
    /// reinstate it. The refusal gives the remedy that applies, as the credential's own read does: while
    /// OS key storage cannot answer, unlocking or repairing it; when it answers that it holds no copy,
    /// storing the credential again, since nothing is there to re-synchronize the mirror from.
    /// </summary>
    /// <remarks>
    /// This reader holds no credential gate, and a save in another process can replace the mirror and
    /// clear its marker while the superseded file is open here. The marker is therefore looked for
    /// both before the mirror is read and after, and a marker seen at either point refuses the export:
    /// only a mirror that was unmarked for the whole read is known not to be the superseded one.
    /// </remarks>
    private async Task<SecretStoreReadResult> ReadWithoutHealingAsync(
        string account,
        string mirrorPath,
        string description,
        string recoveryHint,
        Func<Task<SecretStoreReadResult>> readMirror)
    {
        OsCredentialStoreResult os = osStore.TryGet(
            ArcanumCredentialIdentity.Service,
            account);

        if (os.Status == OsCredentialStoreStatus.Ok
            && !string.IsNullOrWhiteSpace(os.Value))
        {
            return SecretStoreReadResult.Ok(os.Value);
        }

        bool markedBeforeRead = MirroredOsCredential.IsMirrorMarkedStale(mirrorPath);

        SecretStoreReadResult mirror = await readMirror().ConfigureAwait(false);

        if (mirror.Status == SecretStoreReadStatus.Ok
            && (markedBeforeRead || MirroredOsCredential.IsMirrorMarkedStale(mirrorPath)))
        {
            const string Stale = "may be older than the OS credential (a mirror write after a change failed)";

            return SecretStoreReadResult.Corrupted(
                os.Status == OsCredentialStoreStatus.Failed
                    ? $"The encrypted mirror of {description} {Stale}, so it is not backed up while OS key "
                        + "storage cannot answer. Unlock or repair OS key storage and retry the backup."
                    : $"The encrypted mirror of {description} {Stale}, and OS key storage holds no copy to "
                        + "confirm it, so it is not backed up. Store the credential again to replace it. "
                        + recoveryHint);
        }

        if (mirror.Status != SecretStoreReadStatus.Missing)
        {
            return mirror;
        }

        return os.Status == OsCredentialStoreStatus.Failed
            ? SecretStoreReadResult.Corrupted(
                "The operating-system credential store could not be read for backup.")
            : SecretStoreReadResult.Missing();
    }
}

internal sealed class BackupRecoveryKeySnapshot : IDisposable
{
    public BackupRecoveryKeySnapshot(
        string? activeKeyId,
        PortableBackupFileKey[] keys)
    {
        ActiveKeyId = activeKeyId;

        Keys = keys;
    }

    public string? ActiveKeyId { get; }

    public PortableBackupFileKey[] Keys { get; }

    public void Dispose()
    {
        foreach (PortableBackupFileKey key in Keys)
        {
            CryptographicOperations.ZeroMemory(key.KeyBytes);
        }
    }
}

internal static class BackupFileEncryptionKeyExporter
{
    private const string KeyRingHeader = "ARCANUM-KEYRING-1";

    public static BackupRecoveryKeySnapshot Export(
        string encoded,
        IReadOnlySet<string> requiredKeyIds,
        bool includeActiveKey)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        ArgumentNullException.ThrowIfNull(requiredKeyIds);

        Dictionary<string, byte[]> decoded = new(StringComparer.Ordinal);

        string activeKeyId;

        try
        {
            // The canonical encoding is LF-delimited, but a ring persisted by an older Windows build
            // used Environment.NewLine and `FileEncryptionKeyProvider` deliberately still loads it —
            // so this must too. An exporter that refuses what the runtime accepts tells the operator
            // their intact key ring is malformed and hands back no archive at all. The trailing '\r'
            // has to come off every line, not just the header: Convert.FromBase64String skips it, so
            // the keys would decode while the active id carried an invisible '\r' and stopped
            // matching any of them.
            if (encoded.StartsWith(KeyRingHeader + "\n", StringComparison.Ordinal)
                || encoded.StartsWith(KeyRingHeader + "\r\n", StringComparison.Ordinal))
            {
                string[] lines = encoded
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(static line => line.TrimEnd('\r'))
                    .Where(static line => line.Length > 0)
                    .ToArray();

                if (lines.Length < 3
                    || !string.Equals(lines[0], KeyRingHeader, StringComparison.Ordinal)
                    || !lines[1].StartsWith("active=", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The file-encryption key ring is malformed.");
                }

                activeKeyId = lines[1]["active=".Length..];

                foreach (string line in lines[2..])
                {
                    int separator = line.IndexOf('=');

                    if (separator <= 0)
                    {
                        throw new InvalidDataException("The file-encryption key ring is malformed.");
                    }

                    string declaredId = line[..separator];

                    byte[] key = DecodeKey(line[(separator + 1)..], declaredId);

                    if (!decoded.TryAdd(declaredId, key))
                    {
                        CryptographicOperations.ZeroMemory(key);

                        throw new InvalidDataException("The file-encryption key ring contains duplicate ids.");
                    }
                }

                if (!decoded.ContainsKey(activeKeyId))
                {
                    throw new InvalidDataException("The active file-encryption key is missing.");
                }
            }
            else
            {
                byte[] key = Convert.FromBase64String(encoded);

                if (key.Length != 32)
                {
                    CryptographicOperations.ZeroMemory(key);

                    throw new InvalidDataException("The file-encryption key must be 256 bits.");
                }

                activeKeyId = ComputeKeyId(key);

                decoded.Add(activeKeyId, key);
            }

            HashSet<string> selected = new(requiredKeyIds, StringComparer.Ordinal);

            if (includeActiveKey)
            {
                _ = selected.Add(activeKeyId);
            }

            foreach (string required in selected)
            {
                if (!decoded.ContainsKey(required))
                {
                    throw new InvalidDataException(
                        $"A file-encryption key required by included data is unavailable: {required}");
                }
            }

            PortableBackupFileKey[] exported = selected
                .Order(StringComparer.Ordinal)
                .Select(keyId => new PortableBackupFileKey(
                    keyId,
                    decoded[keyId].ToArray()))
                .ToArray();

            string? exportedActive = selected.Contains(activeKeyId)
                ? activeKeyId
                : exported.FirstOrDefault()?.KeyId;

            return new BackupRecoveryKeySnapshot(exportedActive, exported);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The file-encryption key ring is malformed.", ex);
        }
        finally
        {
            foreach (byte[] key in decoded.Values)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private static byte[] DecodeKey(string encoded, string declaredId)
    {
        byte[] key = Convert.FromBase64String(encoded);

        if (key.Length != 32
            || !string.Equals(ComputeKeyId(key), declaredId, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(key);

            throw new InvalidDataException("The file-encryption key ring contains an invalid key.");
        }

        return key;
    }

    private static string ComputeKeyId(ReadOnlySpan<byte> key)
    {
        Span<byte> digest = stackalloc byte[32];

        SHA256.HashData(key, digest);

        string id = Convert.ToHexString(digest[..8]).ToLowerInvariant();

        CryptographicOperations.ZeroMemory(digest);

        return id;
    }
}
