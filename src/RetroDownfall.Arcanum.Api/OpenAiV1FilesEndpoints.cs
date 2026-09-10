using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Api.Streaming;

namespace RetroDownfall.Arcanum.Api;

/// <summary>
/// OpenAI-compatible <c>/v1/files</c> upload storage (<c>docs/Arcanum.DESIGN.md</c> §11.20). File bytes live on disk
/// under <see cref="ArcanumPaths.FilesDirectory"/>, named by a fresh <see cref="Guid"/> (never the
/// client-supplied filename) — path traversal and filename collisions are structurally impossible.
/// The database row is metadata only.
/// (<see cref="ExcludeFromCodeCoverageAttribute"/> is applied once on the primary
/// <c>OpenAiV1Endpoints.cs</c> partial declaration and covers this file too.)
/// </summary>
internal static partial class OpenAiV1Endpoints
{

    private const int MaxUploadFilenameChars = 255;

    private const long MultipartEnvelopeAllowanceBytes = 1L * 1024L * 1024L;

    private const string FileIdPrefix = "file-";

    internal static void MapOpenAiV1Files(this RouteGroupBuilder v1)
    {

        _ = v1.MapPost("/files", HandleUploadAsync)
            .WithName("PostOpenAiFiles")
            .WithFileUploadRequestBody(ResolveMaxMultipartBodyBytes())
            .DisableAntiforgery();

        _ = v1.MapGet("/files", HandleListAsync)
            .WithName("GetOpenAiFiles");

        _ = v1.MapGet("/files/{id}", HandleGetAsync)
            .WithName("GetOpenAiFile");

        _ = v1.MapDelete("/files/{id}", HandleDeleteAsync)
            .WithName("DeleteOpenAiFile");

        _ = v1.MapGet("/files/{id}/content", HandleContentAsync)
            .WithName("GetOpenAiFileContent")
            .WithMetadata(GrimoireStreamRouteMetadata.FiniteDrain);

    }

    private static async Task<IResult> HandleUploadAsync(
        IFormFile? file,
        [FromForm] string? purpose,
        IUploadedFileRepository repository,
        IEncryptedBlobStore blobStore,
        IOptionsSnapshot<ArcanumSettings> settings,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {

        if (file is null || file.Length == 0)
        {

            return JsonError("Missing required parameter: 'file'.", "invalid_request_error", "missing_required_parameter", "file", StatusCodes.Status400BadRequest);

        }

        if (string.IsNullOrWhiteSpace(purpose))
        {

            return JsonError("Missing required parameter: 'purpose'.", "invalid_request_error", "missing_required_parameter", "purpose", StatusCodes.Status400BadRequest);

        }

        string requestedPurpose = purpose.Trim();

        // The bytes below are always written with EncryptedBlobPurpose.UploadedFile, while the read
        // purpose is derived from the stored string. A reserved batch-artifact purpose would
        // therefore store bytes GET /v1/files/{id}/content could never open again.
        if (UploadedFileStorage.IsReservedEncryptionPurpose(requestedPurpose))
        {

            return JsonError(
                $"'purpose' value '{requestedPurpose}' is reserved for batch artifacts published by /v1/batches.",
                "invalid_request_error",
                "invalid_value",
                "purpose",
                StatusCodes.Status400BadRequest);

        }

        string filename = string.IsNullOrWhiteSpace(file.FileName) ? "upload.bin" : file.FileName;

        if (filename.Contains('\0', StringComparison.Ordinal))
        {

            return JsonError("'file' filename must not contain embedded null bytes.", "invalid_request_error", "invalid_value", "file", StatusCodes.Status400BadRequest);

        }

        if (filename.Length > MaxUploadFilenameChars)
        {

            return JsonError($"'file' filename must not exceed {MaxUploadFilenameChars} characters.", "invalid_request_error", "invalid_value", "file", StatusCodes.Status400BadRequest);

        }

        FilesSettings filesSettings = settings.Value.ResolveFiles();
        long maxUploadBytes = ResolveMaxUploadBytes();

        if (!IsUploadSizeAllowed(file.Length))
        {

            return JsonError(
                $"'file' ({file.Length} bytes) exceeds the internal upload limit ({maxUploadBytes} bytes).",
                "invalid_request_error",
                "invalid_value",
                "file",
                StatusCodes.Status413PayloadTooLarge);

        }

        string declaredMimeType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

        if (UploadedFileMimeValidator.IsExtensionMimeMismatch(filename, declaredMimeType))
        {

            return JsonError(
                $"'file' extension does not match its declared content type ('{declaredMimeType}').",
                "invalid_request_error",
                "invalid_value",
                "file",
                StatusCodes.Status400BadRequest);

        }

        if (filesSettings.AllowedMimeTypes.Length > 0
            && !Array.Exists(filesSettings.AllowedMimeTypes, m => string.Equals(m, declaredMimeType, StringComparison.OrdinalIgnoreCase)))
        {

            return JsonError(
                $"Content type '{declaredMimeType}' is not permitted by this server's Arcanum:Security:AllowedUploadMimeTypes policy.",
                "invalid_request_error",
                "invalid_value",
                "file",
                StatusCodes.Status400BadRequest);

        }

        Guid id = Guid.NewGuid();

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(ArcanumPaths.FilesDirectory);

        string path = UploadedFileStorage.ResolvePath(id);

        bool publicationOwnsCleanup = false;

        try
        {

            await using Stream plaintext = file.OpenReadStream();
            EncryptedBlobDescriptor descriptor = await blobStore.WriteAsync(
                    path,
                    plaintext,
                    EncryptedBlobPurpose.UploadedFile,
                    id.ToByteArray(),
                    file.Length,
                    cancellationToken)
                .ConfigureAwait(false);
            await using Stream verification = await blobStore.OpenReadAsync(
                    path,
                    EncryptedBlobPurpose.UploadedFile,
                    cancellationToken)
                .ConfigureAwait(false);
            string plaintextSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(verification, cancellationToken).ConfigureAwait(false));

            UploadedFileRecord record = new(
                id,
                filename,
                file.Length,
                requestedPurpose,
                declaredMimeType,
                DateTimeOffset.UtcNow,
                descriptor.Version,
                descriptor.KeyId,
                plaintextSha256);

            publicationOwnsCleanup = true;

            await repository
                .CreateForOwnedFileAsync(record, cancellationToken)
                .ConfigureAwait(false);

            return Results.Json(
                OpenAiFileObject.FromRecord(record),
                ArcanumJsonContext.Default.OpenAiFileObject,
                statusCode: StatusCodes.Status201Created);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            // A caller that walked away is not a storage failure: no error log, and no 500 written to a
            // connection that is already gone.
            if (!publicationOwnsCleanup)
            {

                TryDeleteFile(path);

            }

            throw;

        }
        catch (Exception ex)
        {

            ILogger logger = loggerFactory.CreateLogger(typeof(OpenAiV1Endpoints));

            logger.LogError(ex, "Failed to persist uploaded file {FileId} to disk.", id);

            if (!publicationOwnsCleanup)
            {

                TryDeleteFile(path);

            }

            return JsonError("Failed to store the uploaded file.", "server_error", "internal_error", param: null, StatusCodes.Status500InternalServerError);

        }

    }

    internal static long ResolveMaxUploadBytes() =>
        ArcanumSettingClamps.FilesMaxUploadSizeBytes(
            ArcanumRuntimeDefaults.Files.MaxUploadSizeBytes);

    /// <summary>
    /// The multipart ceiling for <c>POST /v1/files</c>: the code-owned upload envelope plus one allowance
    /// for boundaries, part headers, and the sibling <c>purpose</c> field. Sized so a file at the envelope
    /// still binds and the handler's own <see cref="IsUploadSizeAllowed"/> check owns the documented 413,
    /// while a grossly oversized body is still cut off before it is buffered to disk.
    /// </summary>
    internal static long ResolveMaxMultipartBodyBytes() =>
        ResolveMaxUploadBytes() + MultipartEnvelopeAllowanceBytes;

    internal static bool IsUploadSizeAllowed(long fileLength) =>
        fileLength <= ResolveMaxUploadBytes();

    private static async Task<IResult> HandleListAsync(
        string? purpose,
        IUploadedFileRepository repository,
        CancellationToken cancellationToken)
    {

        IReadOnlyList<UploadedFileRecord> records = await repository.ListAsync(purpose, cancellationToken).ConfigureAwait(false);

        List<OpenAiFileObject> data = new(records.Count);

        foreach (UploadedFileRecord record in records)
        {

            data.Add(OpenAiFileObject.FromRecord(record));

        }

        return Results.Json(new OpenAiFileListResponse(data), ArcanumJsonContext.Default.OpenAiFileListResponse);

    }

    private static async Task<IResult> HandleGetAsync(string id, IUploadedFileRepository repository, CancellationToken cancellationToken)
    {

        if (!TryParseFileId(id, out Guid fileGuid))
        {

            return FileNotFoundResult(id);

        }

        UploadedFileRecord? record = await repository.GetByIdAsync(fileGuid, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {

            return FileNotFoundResult(id);

        }

        return Results.Json(OpenAiFileObject.FromRecord(record), ArcanumJsonContext.Default.OpenAiFileObject);

    }

    private static async Task<IResult> HandleDeleteAsync(
        string id,
        IUploadedFileRepository repository,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {

        if (!TryParseFileId(id, out Guid fileGuid))
        {

            return FileNotFoundResult(id);

        }

        UploadedFileDeleteStatus status;

        try
        {

            status = await repository
                .TryDeleteUnreferencedAsync(fileGuid, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex)
        {

            ILogger logger = loggerFactory.CreateLogger(typeof(OpenAiV1Endpoints));

            logger.LogError(ex, "Failed to delete uploaded file {FileId} safely.", fileGuid);

            return JsonError(
                "Failed to delete the uploaded file safely.",
                "server_error",
                "file_delete_failed",
                "id",
                StatusCodes.Status500InternalServerError);

        }

        if (status == UploadedFileDeleteStatus.NotFound)
        {

            return FileNotFoundResult(id);

        }

        if (status == UploadedFileDeleteStatus.ReferencedByBatch)
        {

            return JsonError(
                $"File '{id}' is referenced by a batch and cannot be deleted while that reference is retained.",
                "invalid_request_error",
                "file_referenced_by_batch",
                "id",
                StatusCodes.Status409Conflict);

        }

        if (status == UploadedFileDeleteStatus.StorageConflict)
        {

            return JsonError(
                $"File '{id}' changed while deletion was being prepared; metadata and bytes were preserved.",
                "server_error",
                "file_delete_storage_conflict",
                "id",
                StatusCodes.Status500InternalServerError);

        }

        if (status == UploadedFileDeleteStatus.RecoveryRequired)
        {

            return JsonError(
                $"File '{id}' could not be deleted completely; storage recovery is required.",
                "server_error",
                "file_delete_recovery_required",
                "id",
                StatusCodes.Status500InternalServerError);

        }

        if (status != UploadedFileDeleteStatus.Deleted)
        {

            return JsonError(
                $"File '{id}' could not be deleted safely.",
                "server_error",
                "file_delete_failed",
                "id",
                StatusCodes.Status500InternalServerError);

        }

        return Results.Json(
            new OpenAiFileDeleteResponse(id, Deleted: true),
            ArcanumJsonContext.Default.OpenAiFileDeleteResponse);

    }

    private static async Task<IResult> HandleContentAsync(
        string id,
        IUploadedFileRepository repository,
        IEncryptedBlobStore blobStore,
        CancellationToken cancellationToken)
    {

        if (!TryParseFileId(id, out Guid fileGuid))
        {

            return FileNotFoundResult(id);

        }

        UploadedFileRecord? record = await repository.GetByIdAsync(fileGuid, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {

            return FileNotFoundResult(id);

        }

        string path = UploadedFileStorage.ResolvePath(fileGuid);

        if (!File.Exists(path))
        {

            return FileNotFoundResult(id);

        }

        // Always `attachment`, never `inline` — the primary XSS mitigation for arbitrary uploaded
        // content (an uploaded .html/.svg mislabeled as an image must never be rendered by a browser
        // that happens to fetch this endpoint directly).
        string mimeType = string.IsNullOrWhiteSpace(record.MimeType) ? "application/octet-stream" : record.MimeType;

        try
        {
            Stream plaintext = await blobStore.OpenCompatibleReadAsync(
                    path,
                    UploadedFileStorage.ResolveEncryptionPurpose(record.Purpose),
                    record.EncryptionVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Stream(
                plaintext,
                mimeType,
                fileDownloadName: record.Filename,
                enableRangeProcessing: false);
        }
        catch (EncryptedBlobKeyException ex)
        {
            return JsonError(
                ex.Message,
                "server_error",
                "file_encryption_key_unavailable",
                param: null,
                StatusCodes.Status500InternalServerError);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            return JsonError(
                "Stored file authentication failed; the encrypted blob is corrupt.",
                "server_error",
                "encrypted_file_corrupt",
                param: null,
                StatusCodes.Status500InternalServerError);
        }

    }

    private static bool TryParseFileId(string wireId, out Guid id)
    {

        id = Guid.Empty;

        if (string.IsNullOrEmpty(wireId) || !wireId.StartsWith(FileIdPrefix, StringComparison.Ordinal))
        {

            return false;

        }

        return Guid.TryParseExact(wireId.AsSpan(FileIdPrefix.Length), "N", out id);

    }

    private static IResult FileNotFoundResult(string wireId) =>
        JsonError($"No such file: '{wireId}'.", "invalid_request_error", "not_found", param: "id", StatusCodes.Status404NotFound);

    private static void TryDeleteFile(string path)
    {

        try
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }
        catch (IOException)
        {

            // Best-effort cleanup; a stray orphaned file is preferable to throwing from a cleanup path.

        }
        catch (UnauthorizedAccessException)
        {

        }

    }

}
