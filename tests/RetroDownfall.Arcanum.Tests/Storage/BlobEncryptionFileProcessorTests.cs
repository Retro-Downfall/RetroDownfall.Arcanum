using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class BlobEncryptionFileProcessorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-blob-migration-" + Guid.NewGuid().ToString("N"));

    public BlobEncryptionFileProcessorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Metadata_failure_after_atomic_replace_is_reconciled_on_retry()
    {
        byte[] plaintext = Encoding.UTF8.GetBytes("legacy attachment");
        string path = Path.Combine(_root, "attachment");
        await File.WriteAllBytesAsync(path, plaintext);
        BlobEncryptionCandidate candidate = new(
            BlobEncryptionRecordKind.SessionAttachment,
            Guid.NewGuid().ToString("D"),
            path,
            EncryptedBlobPurpose.SessionAttachment,
            plaintext.Length,
            Convert.ToHexString(SHA256.HashData(plaintext)),
            EncryptionVersion: 0,
            EncryptionKeyId: null);
        FailingMetadataStore metadata = new(candidate);
        EncryptedBlobStore blobs = new(new InMemoryKeyRing());
        BlobEncryptionFileProcessor processor = new(metadata, blobs);

        await Assert.ThrowsAsync<IOException>(
            () => processor.MigrateAsync(candidate));

        Assert.True(blobs.HasEnvelope(path));
        Assert.Equal(0, metadata.Current.EncryptionVersion);

        BlobEncryptionFileResult result = await processor.MigrateAsync(metadata.Current);

        Assert.Equal(EncryptedBlobFormat.CurrentVersion, metadata.Current.EncryptionVersion);
        Assert.Equal(plaintext.Length, result.PlaintextLength);
        await using Stream decrypted = await blobs.OpenReadAsync(
            path,
            EncryptedBlobPurpose.SessionAttachment);
        using MemoryStream output = new();
        await decrypted.CopyToAsync(output);
        Assert.Equal(plaintext, output.ToArray());
    }

    [Fact]
    public async Task Verify_classifies_metadata_file_mismatches_missing_files_and_corrupt_envelopes()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        FailingMetadataStore metadata = new();
        BlobEncryptionFileProcessor processor = new(metadata, blobs);
        string plaintextPath = Path.Combine(_root, "plaintext");
        await File.WriteAllTextAsync(plaintextPath, "legacy");
        BlobEncryptionCandidate encryptedMetadata = Candidate(
            plaintextPath,
            encryptionVersion: EncryptedBlobFormat.CurrentVersion);
        BlobEncryptionCandidate missing = Candidate(
            Path.Combine(_root, "missing"),
            encryptionVersion: 0);
        string corruptPath = Path.Combine(_root, "corrupt");
        await File.WriteAllBytesAsync(corruptPath, "ARCABLOBbad"u8.ToArray());
        BlobEncryptionCandidate corrupt = Candidate(corruptPath, encryptionVersion: 1);

        Assert.Equal(
            BlobEncryptionVerificationIssue.MetadataEncryptedFilePlaintext,
            (await processor.VerifyAsync(encryptedMetadata)).Issue);
        Assert.Equal(
            BlobEncryptionVerificationIssue.MissingFile,
            (await processor.VerifyAsync(missing)).Issue);
        Assert.Equal(
            BlobEncryptionVerificationIssue.CorruptEnvelope,
            (await processor.VerifyAsync(corrupt)).Issue);
    }

    private static BlobEncryptionCandidate Candidate(string path, int encryptionVersion) =>
        new(
            BlobEncryptionRecordKind.UploadedFile,
            Guid.NewGuid().ToString("D"),
            path,
            EncryptedBlobPurpose.UploadedFile,
            ExpectedPlaintextLength: 6,
            ExpectedPlaintextSha256: null,
            encryptionVersion,
            EncryptionKeyId: null);

    private sealed class FailingMetadataStore(params BlobEncryptionCandidate[] candidates)
        : IBlobEncryptionMetadataStore
    {
        private bool _failNextUpdate = candidates.Length > 0;

        public BlobEncryptionCandidate Current { get; private set; } =
            candidates.FirstOrDefault()!;

        public Task<IReadOnlyList<BlobEncryptionCandidate>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BlobEncryptionCandidate>>(candidates);

        public Task UpdateEncryptionMetadataAsync(
            BlobEncryptionCandidate candidate,
            EncryptedBlobDescriptor descriptor,
            string plaintextSha256,
            CancellationToken cancellationToken = default)
        {
            if (_failNextUpdate)
            {
                _failNextUpdate = false;
                throw new IOException("simulated metadata commit failure");
            }

            Current = candidate with
            {
                EncryptionVersion = descriptor.Version,
                EncryptionKeyId = descriptor.KeyId,
                ExpectedPlaintextSha256 = plaintextSha256,
            };
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryKeyRing : IFileEncryptionKeyRing
    {
        private readonly Dictionary<string, FileEncryptionKeyMaterial> _keys = [];
        private FileEncryptionKeyMaterial _active;

        public InMemoryKeyRing()
        {
            _active = FileEncryptionKeyMaterial.Create(RandomNumberGenerator.GetBytes(32));
            _keys.Add(_active.KeyId, _active);
        }

        public ValueTask<FileEncryptionKeyMaterial> GetForWriteAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_active);

        public ValueTask<FileEncryptionKeyMaterial> GetForReadAsync(
            string keyId,
            CancellationToken cancellationToken = default) =>
            _keys.TryGetValue(keyId, out FileEncryptionKeyMaterial? key)
                ? ValueTask.FromResult(key)
                : ValueTask.FromException<FileEncryptionKeyMaterial>(
                    new EncryptedBlobKeyException("Unknown key."));

        public Task<FileEncryptionKeyMaterial> RotateAsync(
            CancellationToken cancellationToken = default)
        {
            _active = FileEncryptionKeyMaterial.Create(RandomNumberGenerator.GetBytes(32));
            _keys.Add(_active.KeyId, _active);
            return Task.FromResult(_active);
        }

        public Task RetireAsync(string keyId, CancellationToken cancellationToken = default)
        {
            _ = _keys.Remove(keyId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveKeyIdsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_keys.Keys.ToArray());
    }
}
