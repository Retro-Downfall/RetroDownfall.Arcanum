using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Core.Storage;

public enum EncryptedBlobPurpose : byte
{
    SessionAttachment = 1,
    UploadedFile = 2,
    BatchArtifact = 3,
}

public enum EncryptedBlobAlgorithm : byte
{
    Aes256Gcm = 1,
}

public static class EncryptedBlobFormat
{
    /// <summary>
    /// Version 1 binds the canonical header (with the declared plaintext-length field zeroed so the
    /// streaming writer can back-patch it), the chunk index, and the chunk length into every chunk's
    /// associated data. It does not mark the final chunk, so a coordinated truncation at a chunk
    /// boundary that also rewrites the header length field still authenticates. Version 1 envelopes
    /// remain readable; they are never written.
    /// </summary>
    public const byte LegacyVersion1 = 1;

    /// <summary>
    /// Version 2 additionally binds a final-chunk marker and, on the final chunk, the total declared
    /// plaintext length. Truncating or extending at a chunk boundary therefore changes the associated
    /// data of the chunk that becomes final, and AES-GCM authentication fails.
    /// </summary>
    public const byte LengthBoundVersion2 = 2;

    public const byte CurrentVersion = LengthBoundVersion2;

    public const byte MinimumSupportedVersion = LegacyVersion1;

    public static bool IsSupportedVersion(byte version) =>
        version is >= MinimumSupportedVersion and <= CurrentVersion;
}

public sealed record EncryptedBlobDescriptor(
    byte Version,
    EncryptedBlobAlgorithm Algorithm,
    int ChunkSize,
    string KeyId,
    long PlaintextLength,
    int HeaderLength,
    EncryptedBlobPurpose Purpose,
    ReadOnlyMemory<byte> AuthenticatedMetadata);

public interface IEncryptedBlobStore
{
    Task<EncryptedBlobDescriptor> WriteAsync(
        string destinationPath,
        Stream plaintext,
        EncryptedBlobPurpose purpose,
        ReadOnlyMemory<byte> authenticatedMetadata = default,
        long? plaintextLength = null,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(
        string path,
        EncryptedBlobPurpose purpose,
        CancellationToken cancellationToken = default);

    Task<EncryptedBlobWriter> CreateWriterAsync(
        string destinationPath,
        EncryptedBlobPurpose purpose,
        ReadOnlyMemory<byte> authenticatedMetadata = default,
        CancellationToken cancellationToken = default);

    Task<EncryptedBlobDescriptor> InspectAsync(
        string path,
        EncryptedBlobPurpose purpose,
        bool verifyAllChunks,
        CancellationToken cancellationToken = default);

    bool HasEnvelope(string path);
}

public abstract class EncryptedBlobReader : Stream
{
    public abstract EncryptedBlobDescriptor Descriptor { get; }
}

public abstract class EncryptedBlobWriter : Stream
{
    public abstract Task<EncryptedBlobDescriptor> CompleteAsync(
        CancellationToken cancellationToken = default);
}

public sealed class EncryptedBlobKeyException : CryptographicException
{
    public EncryptedBlobKeyException(string message)
        : base(message)
    {
    }

    public EncryptedBlobKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class FileEncryptionKeyMaterial : IDisposable
{
    private readonly byte[] _masterKey;

    private FileEncryptionKeyMaterial(byte[] masterKey, string keyId)
    {
        _masterKey = masterKey;
        KeyId = keyId;
    }

    public ReadOnlyMemory<byte> MasterKey => _masterKey;

    public string KeyId { get; }

    public static FileEncryptionKeyMaterial Create(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != 32)
        {
            throw new ArgumentException("The file-encryption master key must be 256 bits.", nameof(masterKey));
        }

        byte[] ownedKey = masterKey.ToArray();
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(ownedKey, digest);
        string keyId = Convert.ToHexString(digest[..8]).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(digest);
        return new FileEncryptionKeyMaterial(ownedKey, keyId);
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_masterKey);
}

public interface IFileEncryptionKeyProvider
{
    ValueTask<FileEncryptionKeyMaterial> GetForWriteAsync(
        CancellationToken cancellationToken = default);

    ValueTask<FileEncryptionKeyMaterial> GetForReadAsync(
        string keyId,
        CancellationToken cancellationToken = default);
}

public interface IFileEncryptionKeyRing : IFileEncryptionKeyProvider
{
    Task<FileEncryptionKeyMaterial> RotateAsync(
        CancellationToken cancellationToken = default);

    Task RetireAsync(
        string keyId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetActiveKeyIdsAsync(
        CancellationToken cancellationToken = default);
}

public static class EncryptedBlobStoreCompatibilityExtensions
{
    public static Task<Stream> OpenCompatibleReadAsync(
        this IEncryptedBlobStore blobStore,
        string path,
        EncryptedBlobPurpose purpose,
        int encryptionVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blobStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (encryptionVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(encryptionVersion));
        }

        if (encryptionVersion > 0 || blobStore.HasEnvelope(path))
        {
            if (!blobStore.HasEnvelope(path))
            {
                throw new InvalidDataException(
                    "Blob metadata identifies encrypted content, but the file has no encrypted envelope.");
            }

            return blobStore.OpenReadAsync(path, purpose, cancellationToken);
        }

        Stream plaintext = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        return Task.FromResult(plaintext);
    }
}

public enum FileEncryptionSecretStatus
{
    Available,
    Missing,
    Corrupted,
}

public sealed record FileEncryptionDiagnostics(
    FileEncryptionSecretStatus SecretStatus,
    int EncryptedBlobCount,
    int LegacyPlaintextCount,
    int CorruptBlobCount,
    bool ScanTruncated,
    string Detail)
{
    public bool IsHealthy =>
        SecretStatus == FileEncryptionSecretStatus.Available
        && LegacyPlaintextCount == 0
        && CorruptBlobCount == 0;
}

public interface IEncryptedBlobDiagnostics
{
    Task<FileEncryptionDiagnostics> InspectAsync(
        CancellationToken cancellationToken = default);
}
