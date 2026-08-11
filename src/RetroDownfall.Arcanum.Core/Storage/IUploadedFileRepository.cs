namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Grimoire-backed metadata for <c>POST /v1/files</c> uploads
/// (<c>docs/Arcanum.DESIGN.md</c> §11.20). The row is
/// metadata only — authenticated encrypted file envelopes live on disk under
/// <see cref="ArcanumPaths.FilesDirectory"/>, named by <see cref="UploadedFileRecord.Id"/> (never
/// the client-supplied filename), resolved via <see cref="UploadedFileStorage.ResolvePath"/>.
/// </summary>
public interface IUploadedFileRepository
{

    Task CreateAsync(UploadedFileRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes metadata for bytes already written to the id-derived uploaded-files path. The
    /// implementation captures the owned regular-file identity before waiting for the database
    /// writer, revalidates that exact identity under the writer transaction, and commits metadata
    /// only while the expected bytes remain present.
    /// </summary>
    Task CreateForOwnedFileAsync(
        UploadedFileRecord record,
        CancellationToken cancellationToken = default);

    Task<UploadedFileRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UploadedFileRecord>> ListAsync(string? purpose, CancellationToken cancellationToken = default);

    /// <summary>
    /// Conditionally deletes metadata and owned bytes only when no batch retains the file as its
    /// input, output, or error artifact. This is the user-facing deletion seam: it serializes the
    /// reference check and metadata mutation, moves identity-verified bytes to reversible
    /// same-parent quarantine before that mutation, restores them when the mutation is rejected or
    /// fails, and finalizes the quarantined bytes only after commit.
    /// <see cref="UploadedFileDeleteStatus.Deleted"/> means both metadata and bytes are absent.
    /// </summary>
    Task<UploadedFileDeleteStatus> TryDeleteUnreferencedAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unconditionally deletes the metadata row only. Reserved for internal workflows that already
    /// own and clear the corresponding batch references, such as stuck-batch recovery. User-facing
    /// deletion must use <see cref="TryDeleteUnreferencedAsync"/>.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

}

public sealed class UploadedFilePublicationException(Guid fileId)
    : IOException(
        $"Uploaded file '{fileId:D}' could not be published because its owned bytes changed or disappeared.")
{

    public Guid FileId { get; } = fileId;

}

public enum UploadedFileDeleteStatus
{

    Deleted,

    NotFound,

    ReferencedByBatch,

    StorageConflict,

    RecoveryRequired,

}

public sealed record UploadedFileRecord(
    Guid Id,
    string Filename,
    long Bytes,
    string Purpose,
    string MimeType,
    DateTimeOffset CreatedAt,
    int EncryptionVersion = 0,
    string? EncryptionKeyId = null,
    string? PlaintextSha256 = null);

/// <summary>
/// Pure path computation for uploaded file bytes — no DB or disk access. Safe to call from any
/// layer, including <c>/v1/batches</c>' processor, which reads uploaded input files directly off
/// disk through <see cref="IEncryptedBlobStore"/> rather than through the files HTTP API.
/// </summary>
public static class UploadedFileStorage
{

    /// <summary><c>{ArcanumPaths.FilesDirectory}/{id:N}</c> — never the client-supplied filename.</summary>
    public static string ResolvePath(Guid id) => Path.Combine(ArcanumPaths.FilesDirectory, id.ToString("N"));

    /// <summary>
    /// Purposes owned by <c>/v1/batches</c>' artifact publisher, whose envelopes are written with
    /// <see cref="EncryptedBlobPurpose.BatchArtifact"/>. <c>POST /v1/files</c> always writes
    /// <see cref="EncryptedBlobPurpose.UploadedFile"/>, so an upload must never claim one of these
    /// — its bytes would be stored under a purpose the reader could never match again.
    /// </summary>
    public static bool IsReservedEncryptionPurpose(string purpose) =>
        purpose is "batch_output" or "error";

    public static EncryptedBlobPurpose ResolveEncryptionPurpose(string purpose) =>
        IsReservedEncryptionPurpose(purpose)
            ? EncryptedBlobPurpose.BatchArtifact
            : EncryptedBlobPurpose.UploadedFile;

}
