using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Operations;
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

    // The foreground migration loop catches IOException/InvalidDataException/CryptographicException
    // per candidate, counts the file as failed, and carries on. Crash recovery walked the same
    // candidates with no try/catch at all, so a single blob deleted out-of-band unwound the whole
    // pass and left every later file legacy plaintext — and BlobEncryptionMigration is not one of
    // the kinds FindExpiredAsync re-selects, so nothing ever retried it.
    [Fact]
    public async Task Recovery_migrates_every_healthy_candidate_past_a_missing_blob()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate missing = Candidate(
            Path.Combine(_root, "deleted-out-of-band"),
            expectedLength: 11,
            expectedSha256: null,
            encryptionVersion: 0,
            encryptionKeyId: null);
        BlobEncryptionCandidate legacy = await WriteLegacyAsync("still-plaintext");
        RecordingMetadataStore metadata = new(missing, legacy);
        BlobEncryptionLifecycleService service = new(
            metadata,
            new BlobEncryptionFileProcessor(metadata, blobs),
            blobs,
            keyRing: null!,
            operationCoordinator: null!,
            operationStore: null!,
            TimeProvider.System);

        LongRunningOperationRecoveryResult result = await service.RecoverAsync(
            Operation(LongRunningOperationKinds.BlobEncryptionMigration),
            CancellationToken.None);

        Assert.True(blobs.HasEnvelope(legacy.Path));
        Assert.Equal(legacy.Path, Assert.Single(metadata.Updated));
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);
        Assert.Equal(LongRunningOperationErrorCodes.RecoveryFailed, result.ErrorCode);
    }

    // UnauthorizedAccessException is what a locked or ACL-denied blob throws, and it is not an
    // IOException. Recovery must count that one blob as failed and carry on to the next candidate,
    // and the verification pass after it must record the blob rather than unwind.
    [Fact]
    public async Task Recover_continues_past_a_blob_that_throws_unauthorized_access()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate denied = await WriteEncryptedAsync(blobs, "access-denied");
        BlobEncryptionCandidate legacy = await WriteLegacyAsync("still-plaintext");
        ObservedBlobStore observed = new(blobs, denied.Path);
        RecordingMetadataStore metadata = new(denied, legacy);
        BlobEncryptionLifecycleService service = new(
            metadata,
            new BlobEncryptionFileProcessor(metadata, observed),
            observed,
            keyRing: null!,
            operationCoordinator: null!,
            operationStore: null!,
            TimeProvider.System);

        LongRunningOperationRecoveryResult result = await service.RecoverAsync(
            Operation(LongRunningOperationKinds.BlobEncryptionMigration),
            CancellationToken.None);

        Assert.True(blobs.HasEnvelope(legacy.Path));
        Assert.Equal(legacy.Path, Assert.Single(metadata.Updated));
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);
        Assert.Equal(LongRunningOperationErrorCodes.RecoveryFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Verify_records_an_access_denied_blob_as_an_io_error()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate denied = await WriteEncryptedAsync(blobs, "access-denied");
        BlobEncryptionCandidate healthy = await WriteEncryptedAsync(blobs, "healthy");
        ObservedBlobStore observed = new(blobs, denied.Path);
        ListOnlyMetadataStore metadata = new([denied, healthy]);
        BlobEncryptionLifecycleService service = new(
            metadata,
            new BlobEncryptionFileProcessor(metadata, observed),
            observed,
            keyRing: null!,
            operationCoordinator: null!,
            operationStore: null!,
            TimeProvider.System);

        BlobEncryptionOperationResult result = await service.VerifyAsync(
            maxConcurrency: 1,
            maxBytesPerSecond: long.MaxValue);

        Assert.Equal(1, result.ProcessedFiles);
        Assert.Equal(1, result.FailedFiles);
        Assert.Equal(1, result.Issues[BlobEncryptionVerificationIssue.IoError]);
    }

    private static LongRunningOperation Operation(string kind) =>
        new(
            Guid.NewGuid(),
            kind,
            LongRunningOperationState.Running,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            RootOperationId: null,
            ParentOperationId: null,
            SessionId: null,
            RunId: null,
            InferenceRunId: null,
            BudgetReservationId: null,
            IdempotencyClaimId: null,
            CreatedAt: DateTimeOffset.UnixEpoch,
            StartedAt: DateTimeOffset.UnixEpoch,
            HeartbeatAt: null,
            CompletedAt: null,
            LeaseOwner: "reconciler",
            LeaseExpiresAt: null,
            AttemptCount: 1,
            CheckpointVersion: 1,
            CheckpointPayload: null,
            CheckpointReference: null,
            PublicSummary: "Blob encryption migration is recovering.",
            TerminalErrorCode: null,
            Revision: 1);

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

    private sealed class RecordingMetadataStore(params BlobEncryptionCandidate[] candidates)
        : IBlobEncryptionMetadataStore
    {
        private readonly List<string> _updated = [];

        public IReadOnlyList<string> Updated => _updated;

        public Task<IReadOnlyList<BlobEncryptionCandidate>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BlobEncryptionCandidate>>(candidates);

        public Task UpdateEncryptionMetadataAsync(
            BlobEncryptionCandidate candidate,
            EncryptedBlobDescriptor descriptor,
            string plaintextSha256,
            CancellationToken cancellationToken = default)
        {
            _updated.Add(candidate.Path);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Delegates to a real store, counts every body read, and makes one path throw the way an
    /// access-denied file does.
    /// </summary>
    private sealed class ObservedBlobStore(IEncryptedBlobStore inner, string? deniedPath = null)
        : IEncryptedBlobStore
    {
        private int _openReadCalls;

        private int _inspectCalls;

        public int OpenReadCalls => Volatile.Read(ref _openReadCalls);

        public int InspectCalls => Volatile.Read(ref _inspectCalls);

        public Task<EncryptedBlobDescriptor> WriteAsync(
            string destinationPath,
            Stream plaintext,
            EncryptedBlobPurpose purpose,
            ReadOnlyMemory<byte> authenticatedMetadata = default,
            long? plaintextLength = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDenied(destinationPath);
            return inner.WriteAsync(
                destinationPath,
                plaintext,
                purpose,
                authenticatedMetadata,
                plaintextLength,
                cancellationToken);
        }

        public Task<Stream> OpenReadAsync(
            string path,
            EncryptedBlobPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openReadCalls);
            ThrowIfDenied(path);
            return inner.OpenReadAsync(path, purpose, cancellationToken);
        }

        public Task<EncryptedBlobWriter> CreateWriterAsync(
            string destinationPath,
            EncryptedBlobPurpose purpose,
            ReadOnlyMemory<byte> authenticatedMetadata = default,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Blob lifecycle work never streams a new writer.");

        public Task<EncryptedBlobDescriptor> InspectAsync(
            string path,
            EncryptedBlobPurpose purpose,
            bool verifyAllChunks,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _inspectCalls);
            ThrowIfDenied(path);
            return inner.InspectAsync(path, purpose, verifyAllChunks, cancellationToken);
        }

        public bool HasEnvelope(string path) => inner.HasEnvelope(path);

        private void ThrowIfDenied(string path)
        {
            if (deniedPath is not null && string.Equals(path, deniedPath, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Access to the blob is denied.");
            }
        }
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
