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
    string? EncryptionKeyId,
    long? CatalogRowId = null);

public enum BlobEncryptionMetadataIssue
{
    InvalidIdentity,
    InvalidPath,
    InvalidLength,
    InvalidVersion,
    InvalidHash,
    InvalidKey,
    InvalidPurpose,
}

/// <summary>A content-free locator and cause; an invalid row never carries a usable file path.</summary>
public sealed record BlobEncryptionInvalidRow(
    BlobEncryptionRecordKind Kind,
    long CatalogRowId,
    BlobEncryptionMetadataIssue Issue,
    long? KnownPlaintextLength);

public sealed record BlobEncryptionInventory(
    IReadOnlyList<BlobEncryptionCandidate> Candidates,
    IReadOnlyList<BlobEncryptionInvalidRow> InvalidRows);

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

    /// <summary>The blob could not be read: an I/O or access-denied failure on this one file.</summary>
    IoError,

    /// <summary>
    /// The envelope authenticates and matches its metadata, but predates the current format: it binds no
    /// final-chunk marker or total length, so a chunk-boundary truncation of it would still authenticate.
    /// <c>migrate</c> re-encrypts it to the current version.
    /// </summary>
    OutdatedEnvelopeVersion,

    /// <summary>The catalog row cannot safely identify and describe a file.</summary>
    InvalidMetadata,

    /// <summary>The catalog key or version disagrees with the authenticated envelope.</summary>
    MetadataEnvelopeMismatch,
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
    Task<BlobEncryptionInventory> ListAsync(
        CancellationToken cancellationToken = default);

    Task UpdateEncryptionMetadataAsync(
        BlobEncryptionCandidate candidate,
        EncryptedBlobDescriptor descriptor,
        string plaintextSha256,
        CancellationToken cancellationToken = default);
}

public sealed record BlobEncryptionStatus(
    int TotalFiles,
    long? TotalBytes,
    int EncryptedFiles,
    long? EncryptedBytes,
    int LegacyPlaintextFiles,
    long? LegacyPlaintextBytes,
    int FilesNeedingReconciliation,
    int InvalidFiles,
    IReadOnlyDictionary<string, int> FilesByKeyId);

public sealed record BlobEncryptionOperationResult(
    Guid OperationId,
    int ProcessedFiles,
    long? ProcessedBytes,
    int RemainingFiles,
    long? RemainingBytes,
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
