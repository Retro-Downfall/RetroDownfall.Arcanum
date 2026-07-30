using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class FileEncryptionKeyProvider : IFileEncryptionKeyProvider, IDisposable
{
    private const string MissingRecoveryMessage =
        "The dedicated file-encryption secret is missing. Restore the OS credential "
        + "'file-encryption-master-key', or restore file-encryption-key.dat and the matching "
        + "Data Protection key ring from backup. Arcanum will not treat "
        + "encrypted attachment, upload, or batch bytes as plaintext.";

    private readonly ISecretStore _secretStore;
    private readonly Func<bool> _encryptedBlobsExist;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileEncryptionKeyMaterial? _cached;

    public FileEncryptionKeyProvider(
        ISecretStore secretStore,
        Func<bool>? encryptedBlobsExist = null)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _encryptedBlobsExist = encryptedBlobsExist ?? HasEncryptedBlobFiles;
    }

    public void Dispose()
    {
        _cached?.Dispose();
        _gate.Dispose();
    }

    public async ValueTask<FileEncryptionKeyMaterial> GetForWriteAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            SecretStoreReadResult result = await _secretStore
                .GetFileEncryptionSecretReadResultAsync()
                .ConfigureAwait(false);
            if (result.Status == SecretStoreReadStatus.Corrupted)
            {
                throw new EncryptedBlobKeyException(
                    result.Message ?? MissingRecoveryMessage);
            }

            if (result.Status == SecretStoreReadStatus.Missing)
            {
                if (_encryptedBlobsExist())
                {
                    throw new EncryptedBlobKeyException(MissingRecoveryMessage);
                }

                byte[] generated = RandomNumberGenerator.GetBytes(32);
                string encoded = Convert.ToBase64String(generated);
                try
                {
                    await _secretStore.SaveFileEncryptionSecretAsync(encoded)
                        .ConfigureAwait(false);
                    _cached = FileEncryptionKeyMaterial.Create(generated);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(generated);
                }

                return _cached;
            }

            _cached = Decode(result.Value);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<FileEncryptionKeyMaterial> GetForReadAsync(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        FileEncryptionKeyMaterial material = _cached
            ?? await LoadExistingAsync(cancellationToken).ConfigureAwait(false);
        byte[] expected = System.Text.Encoding.ASCII.GetBytes(material.KeyId);
        byte[] actual = System.Text.Encoding.ASCII.GetBytes(keyId);
        bool matches = expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
        CryptographicOperations.ZeroMemory(expected);
        CryptographicOperations.ZeroMemory(actual);
        if (!matches)
        {
            throw new EncryptedBlobKeyException(
                $"Encrypted blob key '{keyId}' is unavailable. Restore the matching "
                + "file-encryption-key.dat and Data Protection key ring from backup.");
        }

        return material;
    }

    private async ValueTask<FileEncryptionKeyMaterial> LoadExistingAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            SecretStoreReadResult result = await _secretStore
                .GetFileEncryptionSecretReadResultAsync()
                .ConfigureAwait(false);
            if (result.Status == SecretStoreReadStatus.Missing)
            {
                throw new EncryptedBlobKeyException(MissingRecoveryMessage);
            }

            if (result.Status == SecretStoreReadStatus.Corrupted)
            {
                throw new EncryptedBlobKeyException(
                    result.Message ?? MissingRecoveryMessage);
            }

            _cached = Decode(result.Value);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static FileEncryptionKeyMaterial Decode(string? encoded)
    {
        try
        {
            byte[] key = Convert.FromBase64String(encoded ?? string.Empty);
            try
            {
                return FileEncryptionKeyMaterial.Create(key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (FormatException ex)
        {
            throw new EncryptedBlobKeyException(
                "file-encryption-key.dat contains invalid key material. Restore it and the "
                + "matching Data Protection key ring from backup.",
                ex);
        }
        catch (ArgumentException ex)
        {
            throw new EncryptedBlobKeyException(
                "file-encryption-key.dat does not contain a 256-bit key. Restore it and the "
                + "matching Data Protection key ring from backup.",
                ex);
        }
    }

    private static bool HasEncryptedBlobFiles() =>
        DirectoryContainsEncryptedBlob(ArcanumPaths.AttachmentsDirectory, SearchOption.AllDirectories)
        || DirectoryContainsEncryptedBlob(ArcanumPaths.FilesDirectory, SearchOption.TopDirectoryOnly);

    private static bool DirectoryContainsEncryptedBlob(
        string directory,
        SearchOption searchOption)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            Span<byte> magic = stackalloc byte[8];
            foreach (string path in Directory.EnumerateFiles(directory, "*", searchOption))
            {
                magic.Clear();
                try
                {
                    using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Read(magic) == magic.Length
                        && CryptographicOperations.FixedTimeEquals(magic, "ARCABLOB"u8))
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }
}
