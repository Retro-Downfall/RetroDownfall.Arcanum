using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class BlobEncryptionLifecycleServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-blob-lifecycle-" + Guid.NewGuid().ToString("N"));

    public BlobEncryptionLifecycleServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // `BlobEncryptionVerificationIssue.None` is the healthy state, and legacy plaintext is a
    // migration state, not a defect. Only the genuine failures belong in `InvalidFiles`; counting a
    // correctly encrypted blob there makes the reported number meaningless and forces
    // `arcanum data encryption status` to exit 1 on a perfectly healthy installation.
    [Fact]
    public async Task Status_counts_only_genuinely_invalid_files_as_invalid()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate healthy = await WriteEncryptedAsync(blobs, "healthy-one");
        BlobEncryptionCandidate alsoHealthy = await WriteEncryptedAsync(blobs, "healthy-two");
        BlobEncryptionCandidate legacy = await WriteLegacyAsync("legacy");
        BlobEncryptionCandidate missing = Candidate(
            Path.Combine(_root, "missing"),
            expectedLength: 0,
            expectedSha256: null,
            encryptionVersion: EncryptedBlobFormat.CurrentVersion,
            encryptionKeyId: null);
        BlobEncryptionLifecycleService service = CreateService(
            blobs,
            healthy,
            alsoHealthy,
            legacy,
            missing);

        BlobEncryptionStatus status = await service.GetStatusAsync();

        Assert.Equal(4, status.TotalFiles);
        Assert.Equal(2, status.EncryptedFiles);
        Assert.Equal(1, status.LegacyPlaintextFiles);
        Assert.Equal(0, status.FilesNeedingReconciliation);
        Assert.Equal(1, status.InvalidFiles);
    }

    private async Task<BlobEncryptionCandidate> WriteEncryptedAsync(
        EncryptedBlobStore blobs,
        string name)
    {
        byte[] plaintext = Encoding.UTF8.GetBytes("encrypted " + name);
        string path = Path.Combine(_root, name);
        EncryptedBlobDescriptor descriptor = await blobs.WriteAsync(
            path,
            new MemoryStream(plaintext),
            EncryptedBlobPurpose.UploadedFile);
        return Candidate(
            path,
            plaintext.Length,
            Convert.ToHexString(SHA256.HashData(plaintext)),
            descriptor.Version,
            descriptor.KeyId);
    }

    private async Task<BlobEncryptionCandidate> WriteLegacyAsync(string name)
    {
        byte[] plaintext = Encoding.UTF8.GetBytes("legacy " + name);
        string path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, plaintext);
        return Candidate(
            path,
            plaintext.Length,
            Convert.ToHexString(SHA256.HashData(plaintext)),
            encryptionVersion: 0,
            encryptionKeyId: null);
    }

    private static BlobEncryptionCandidate Candidate(
        string path,
        long expectedLength,
        string? expectedSha256,
        int encryptionVersion,
        string? encryptionKeyId) =>
        new(
            BlobEncryptionRecordKind.UploadedFile,
            Guid.NewGuid().ToString("D"),
            path,
            EncryptedBlobPurpose.UploadedFile,
            expectedLength,
            expectedSha256,
            encryptionVersion,
            encryptionKeyId);

    // GetStatusAsync reads metadata, verifies content, and probes envelopes; it never leases an
    // operation or touches the key ring, so those dependencies stay null and a regression that
    // starts using them fails loudly instead of passing silently.
    private static BlobEncryptionLifecycleService CreateService(
        EncryptedBlobStore blobs,
        params BlobEncryptionCandidate[] candidates)
    {
        ListOnlyMetadataStore metadata = new(candidates);
        return new BlobEncryptionLifecycleService(
            metadata,
            new BlobEncryptionFileProcessor(metadata, blobs),
            blobs,
            keyRing: null!,
            operationCoordinator: null!,
            operationStore: null!,
            TimeProvider.System);
    }

    private sealed class ListOnlyMetadataStore(BlobEncryptionCandidate[] candidates)
        : IBlobEncryptionMetadataStore
    {
        public Task<IReadOnlyList<BlobEncryptionCandidate>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BlobEncryptionCandidate>>(candidates);

        public Task UpdateEncryptionMetadataAsync(
            BlobEncryptionCandidate candidate,
            EncryptedBlobDescriptor descriptor,
            string plaintextSha256,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Status never commits metadata.");
    }

    private sealed class InMemoryKeyRing : IFileEncryptionKeyProvider
    {
        private readonly FileEncryptionKeyMaterial _material =
            FileEncryptionKeyMaterial.Create(RandomNumberGenerator.GetBytes(32));

        public ValueTask<FileEncryptionKeyMaterial> GetForWriteAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_material);

        public ValueTask<FileEncryptionKeyMaterial> GetForReadAsync(
            string keyId,
            CancellationToken cancellationToken = default) =>
            string.Equals(keyId, _material.KeyId, StringComparison.Ordinal)
                ? ValueTask.FromResult(_material)
                : ValueTask.FromException<FileEncryptionKeyMaterial>(
                    new EncryptedBlobKeyException("Unknown key."));
    }
}
