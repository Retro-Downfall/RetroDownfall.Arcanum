using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class FileEncryptionKeyProvider : IFileEncryptionKeyRing, IDisposable
{
    private const string KeyRingHeader = "ARCANUM-KEYRING-1";
    private const string MissingRecoveryMessage =
        "The dedicated file-encryption secret is missing. Restore the OS credential "
        + "'file-encryption-master-key', or restore file-encryption-key.dat and the matching "
        + "Data Protection key ring from backup. Arcanum will not treat "
        + "encrypted attachment, upload, or batch bytes as plaintext.";

    private readonly ISecretStore _secretStore;

    private readonly Func<bool> _encryptedBlobsExist;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, FileEncryptionKeyMaterial>? _keys;

    private string? _activeKeyId;

    private bool _keysLoadedByPeek;

    /// <summary>
    /// Material dropped from the ring that a reader may still hold, zeroized only at disposal.
    /// </summary>
    /// <remarks>
    /// <see cref="FileEncryptionKeyMaterial.MasterKey"/> is a view over the instance's own array and
    /// Dispose zeroizes it in place, so disposing a retired key while a reader holds the same instance
    /// rewrites the bytes under it. Readers leave the gate before touching the span, so retirement
    /// cannot know whether one is mid-flight. Removing the entry is what makes the key unreachable for
    /// new reads; zeroizing waits until the whole provider goes away.
    /// </remarks>
    private readonly List<FileEncryptionKeyMaterial> _retired = [];

    public FileEncryptionKeyProvider(
        ISecretStore secretStore,
        Func<bool>? encryptedBlobsExist = null)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _encryptedBlobsExist = encryptedBlobsExist ?? HasEncryptedBlobFiles;
    }

    public void Dispose()
    {
        if (_keys is not null)
        {
            foreach (FileEncryptionKeyMaterial material in _keys.Values)
            {
                material.Dispose();
            }
        }

        foreach (FileEncryptionKeyMaterial material in _retired)
        {
            material.Dispose();
        }

        _retired.Clear();

        _gate.Dispose();
    }

    public async ValueTask<FileEncryptionKeyMaterial> GetForWriteAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys is null || _keysLoadedByPeek)
            {
                await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            }

            return _keys![_activeKeyId!];
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys is null || _keysLoadedByPeek)
            {
                await LoadExistingCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            if (TryFindKey(keyId, out FileEncryptionKeyMaterial? material))
            {
                return material!;
            }

            throw UnknownKey(keyId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<FileEncryptionKeyMaterial> PeekForReadAsync(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys is null)
            {
                await LoadExistingCoreAsync(cancellationToken, peek: true).ConfigureAwait(false);
            }

            if (TryFindKey(keyId, out FileEncryptionKeyMaterial? material))
            {
                return material!;
            }

            throw UnknownKey(keyId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FileEncryptionKeyMaterial> RotateAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys is null || _keysLoadedByPeek)
            {
                await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            }

            byte[] generated = RandomNumberGenerator.GetBytes(32);
            FileEncryptionKeyMaterial next;
            try
            {
                next = FileEncryptionKeyMaterial.Create(generated);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(generated);
            }

            Dictionary<string, FileEncryptionKeyMaterial> updated =
                new(_keys!, StringComparer.Ordinal)
                {
                    [next.KeyId] = next,
                };
            try
            {
                await PersistAsync(updated, next.KeyId).ConfigureAwait(false);
            }
            catch
            {
                next.Dispose();
                throw;
            }

            _keys = updated;
            _activeKeyId = next.KeyId;
            _keysLoadedByPeek = false;
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RetireAsync(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys is null || _keysLoadedByPeek)
            {
                await LoadExistingCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(keyId, _activeKeyId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The active file-encryption write key cannot be retired.");
            }

            if (!TryFindKey(keyId, out FileEncryptionKeyMaterial? retired))
            {
                throw UnknownKey(keyId);
            }

            Dictionary<string, FileEncryptionKeyMaterial> updated =
                new(_keys!, StringComparer.Ordinal);
            _ = updated.Remove(retired!.KeyId);
            await PersistAsync(updated, _activeKeyId!).ConfigureAwait(false);
            _keys = updated;
            _keysLoadedByPeek = false;
            _retired.Add(retired);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetActiveKeyIdsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys is null || _keysLoadedByPeek)
            {
                await LoadExistingCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            return _keys!.Keys.Order(StringComparer.Ordinal).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        SecretStoreReadResult result = await _secretStore
            .GetFileEncryptionSecretReadResultAsync()
            .ConfigureAwait(false);
        if (result.Status == SecretStoreReadStatus.Corrupted)
        {
            throw new EncryptedBlobKeyException(result.Message ?? MissingRecoveryMessage);
        }

        if (result.Status != SecretStoreReadStatus.Missing)
        {
            Load(result.Value);
            _keysLoadedByPeek = false;
            return;
        }

        if (_encryptedBlobsExist())
        {
            throw new EncryptedBlobKeyException(MissingRecoveryMessage);
        }

        byte[] generated = RandomNumberGenerator.GetBytes(32);
        try
        {
            FileEncryptionKeyMaterial material = FileEncryptionKeyMaterial.Create(generated);
            string legacyEncoded = Convert.ToBase64String(generated);
            try
            {
                await _secretStore.SaveFileEncryptionSecretAsync(legacyEncoded)
                    .ConfigureAwait(false);
            }
            catch
            {
                material.Dispose();
                throw;
            }

            ReplaceLoadedKeys(
                new Dictionary<string, FileEncryptionKeyMaterial>(StringComparer.Ordinal)
                {
                    [material.KeyId] = material,
                },
                material.KeyId);
            _keysLoadedByPeek = false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generated);
        }
    }

    private async Task LoadExistingCoreAsync(
        CancellationToken cancellationToken,
        bool peek = false)
    {
        SecretStoreReadResult result = peek
            ? await _secretStore
                .PeekFileEncryptionSecretReadResultAsync()
                .ConfigureAwait(false)
            : await _secretStore
                .GetFileEncryptionSecretReadResultAsync()
                .ConfigureAwait(false);
        if (result.Status == SecretStoreReadStatus.Missing)
        {
            throw new EncryptedBlobKeyException(MissingRecoveryMessage);
        }

        if (result.Status == SecretStoreReadStatus.Corrupted)
        {
            throw new EncryptedBlobKeyException(result.Message ?? MissingRecoveryMessage);
        }

        Load(result.Value);

        _keysLoadedByPeek = peek;
    }

    private void Load(string? encoded)
    {
        if (encoded is not null && IsKeyRing(encoded))
        {
            LoadKeyRing(encoded);
            return;
        }

        FileEncryptionKeyMaterial material = Decode(encoded);
        ReplaceLoadedKeys(
            new Dictionary<string, FileEncryptionKeyMaterial>(StringComparer.Ordinal)
            {
                [material.KeyId] = material,
            },
            material.KeyId);
    }

    private void ReplaceLoadedKeys(
        Dictionary<string, FileEncryptionKeyMaterial> loaded,
        string activeKeyId)
    {
        if (_keys is not null)
        {
            _retired.AddRange(_keys.Values);
        }

        _keys = loaded;
        _activeKeyId = activeKeyId;
    }

    // The canonical key-ring encoding is LF-delimited (see BackupSecretRewrapper). A ring persisted
    // by an older Windows build used Environment.NewLine, so every line may carry a trailing '\r';
    // those rings must still load or the install loses access to every encrypted blob.
    private static bool IsKeyRing(string encoded) =>
        encoded.StartsWith(KeyRingHeader + "\n", StringComparison.Ordinal)
        || encoded.StartsWith(KeyRingHeader + "\r\n", StringComparison.Ordinal);

    private void LoadKeyRing(string encoded)
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
            throw new EncryptedBlobKeyException("The file-encryption key ring is malformed; restore backup.");
        }

        string active = lines[1]["active=".Length..];
        Dictionary<string, FileEncryptionKeyMaterial> loaded = new(StringComparer.Ordinal);
        try
        {
            foreach (string line in lines[2..])
            {
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    throw new EncryptedBlobKeyException(
                        "The file-encryption key ring is malformed; restore backup.");
                }

                string declaredId = line[..separator];
                FileEncryptionKeyMaterial material = Decode(line[(separator + 1)..]);
                if (!string.Equals(declaredId, material.KeyId, StringComparison.Ordinal)
                    || !loaded.TryAdd(material.KeyId, material))
                {
                    material.Dispose();
                    throw new EncryptedBlobKeyException(
                        "The file-encryption key ring contains invalid key identifiers; restore backup.");
                }
            }

            if (!loaded.ContainsKey(active))
            {
                throw new EncryptedBlobKeyException(
                    "The active file-encryption key is missing from the key ring; restore backup.");
            }
        }
        catch
        {
            foreach (FileEncryptionKeyMaterial material in loaded.Values)
            {
                material.Dispose();
            }

            throw;
        }

        ReplaceLoadedKeys(loaded, active);
    }

    private async Task PersistAsync(
        IReadOnlyDictionary<string, FileEncryptionKeyMaterial> keys,
        string activeKeyId)
    {
        // Always LF, never AppendLine, which emits CRLF on Windows. Every key-ring reader (this type,
        // BackupSecretSnapshotReader, BackupSecretRewrapper) tolerates CRLF so that rings an older
        // Windows build already persisted still load — but tolerance is for what is already on disk.
        // This writer emits the one canonical form, so no ring it produces ever depends on it.
        System.Text.StringBuilder encoded = new();
        _ = encoded.Append(KeyRingHeader).Append('\n');
        _ = encoded.Append("active=").Append(activeKeyId).Append('\n');
        foreach ((string keyId, FileEncryptionKeyMaterial material) in
                 keys.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            _ = encoded.Append(keyId)
                .Append('=')
                .Append(Convert.ToBase64String(material.MasterKey.Span))
                .Append('\n');
        }

        await _secretStore.SaveFileEncryptionSecretAsync(encoded.ToString())
            .ConfigureAwait(false);
    }

    // W7-8: key ids are public lookup identifiers persisted in blob metadata (DESIGN §5.4.6),
    // not secrets, and _keys is already a Dictionary<string, FileEncryptionKeyMaterial> built with
    // StringComparer.Ordinal (see the constructions above), so a hash lookup is a drop-in
    // replacement for the per-candidate byte[]-allocating fixed-time scan this used to run on
    // every encrypted-blob read.
    private bool TryFindKey(string requestedKeyId, out FileEncryptionKeyMaterial? material) =>
        _keys!.TryGetValue(requestedKeyId, out material);

    private static EncryptedBlobKeyException UnknownKey(string keyId) =>
        new(
            $"Encrypted blob key '{keyId}' is unavailable. Restore the matching "
            + "portable file-encryption key ring and Data Protection key ring from backup.");

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
