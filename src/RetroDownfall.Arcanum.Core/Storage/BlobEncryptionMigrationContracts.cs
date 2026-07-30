namespace RetroDownfall.Arcanum.Core.Storage;

public enum BlobEncryptionRecordKind
{
    UploadedFile,
    SessionAttachment,
}

public sealed record BlobEncryptionCandidate(
    BlobEncryptionRecordKind Kind,
    string RecordId,
    string Path,
    EncryptedBlobPurpose Purpose,
    long ExpectedPlaintextLength,
    string? ExpectedPlaintextSha256,
    int EncryptionVersion,
    string? EncryptionKeyId);

public enum BlobEncryptionVerificationIssue
{
    None,
    LegacyPlaintext,
    MetadataEncryptedFilePlaintext,
    MetadataPlaintextFileEncrypted,
    UnknownKeyId,
    MissingFile,
    CorruptEnvelope,
    PlaintextLengthMismatch,
    PlaintextHashMismatch,
}

public sealed record BlobEncryptionVerificationResult(
    BlobEncryptionVerificationIssue Issue,
    long PlaintextLength = 0,
    string? PlaintextSha256 = null,
    EncryptedBlobDescriptor? Descriptor = null)
{
    public bool IsValid => Issue == BlobEncryptionVerificationIssue.None;
}

public sealed record BlobEncryptionFileResult(
    long PlaintextLength,
    string PlaintextSha256,
    EncryptedBlobDescriptor Descriptor);

public interface IBlobEncryptionMetadataStore
{
    Task<IReadOnlyList<BlobEncryptionCandidate>> ListAsync(
        CancellationToken cancellationToken = default);

    Task UpdateEncryptionMetadataAsync(
        BlobEncryptionCandidate candidate,
        EncryptedBlobDescriptor descriptor,
        string plaintextSha256,
        CancellationToken cancellationToken = default);
}

public sealed record BlobEncryptionStatus(
    int TotalFiles,
    long TotalBytes,
    int EncryptedFiles,
    long EncryptedBytes,
    int LegacyPlaintextFiles,
    long LegacyPlaintextBytes,
    int FilesNeedingReconciliation,
    int InvalidFiles,
    IReadOnlyDictionary<string, int> FilesByKeyId);

public sealed record BlobEncryptionOperationResult(
    Guid OperationId,
    int ProcessedFiles,
    long ProcessedBytes,
    int RemainingFiles,
    long RemainingBytes,
    int FailedFiles,
    IReadOnlyDictionary<BlobEncryptionVerificationIssue, int> Issues);

public interface IBlobEncryptionLifecycleService
{
    Task<BlobEncryptionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);

    Task<BlobEncryptionOperationResult> MigrateAsync(
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken = default);

    Task<BlobEncryptionOperationResult> VerifyAsync(
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken = default);

    Task<BlobEncryptionOperationResult> RotateKeyAsync(
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken = default);
}
