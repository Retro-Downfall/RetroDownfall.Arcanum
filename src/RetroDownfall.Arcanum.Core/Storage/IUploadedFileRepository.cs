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

    Task<UploadedFileRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UploadedFileRecord>> ListAsync(string? purpose, CancellationToken cancellationToken = default);

    /// <summary>Deletes the metadata row only — callers are responsible for also deleting the on-disk file (see <see cref="UploadedFileStorage.ResolvePath"/>).</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

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

    public static EncryptedBlobPurpose ResolveEncryptionPurpose(string purpose) =>
        purpose is "batch_output" or "error"
            ? EncryptedBlobPurpose.BatchArtifact
            : EncryptedBlobPurpose.UploadedFile;

}
