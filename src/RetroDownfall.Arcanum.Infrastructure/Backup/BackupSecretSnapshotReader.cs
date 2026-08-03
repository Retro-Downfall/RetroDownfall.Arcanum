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
            dataProtectionStore.GetFileEncryptionSecretReadResultAsync);

    public Task<SecretStoreReadResult> ReadMasterApiKeyAsync() =>
        ReadWithoutHealingAsync(
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            dataProtectionStore.GetApiKeyReadResultAsync);

    private async Task<SecretStoreReadResult> ReadWithoutHealingAsync(
        string account,
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

        SecretStoreReadResult mirror = await readMirror().ConfigureAwait(false);

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

            if (encoded.StartsWith(KeyRingHeader + "\n", StringComparison.Ordinal))
            {

                string[] lines = encoded.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length < 3
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
