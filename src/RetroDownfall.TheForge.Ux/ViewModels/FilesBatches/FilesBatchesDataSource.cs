using RetroDownfall.TheForge.Core.Models.OpenAi;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.FilesBatches;

/// <summary>
/// Data-source seam for Files &amp; Batches (Phase 9). Wraps <see cref="FilesBatchesService"/> /
/// <see cref="OpenAiCompatApiClient"/>. Tests fake this interface. Returns
/// <see cref="OpenAiResult{T}"/> — never <c>ApiResponse&lt;T&gt;</c>.
/// </summary>
public interface IFilesBatchesDataSource
{

    Task<OpenAiResult<OpenAiFileListResponse>> ListFilesAsync(string? purpose, CancellationToken cancellationToken);

    Task<OpenAiResult<OpenAiFileObject>> UploadFileAsync(string filePath, string purpose, CancellationToken cancellationToken);

    Task<OpenAiResult<OpenAiFileDeleteResponse>> DeleteFileAsync(string fileId, CancellationToken cancellationToken);

    Task<OpenAiResult<bool>> DownloadFileContentAsync(string fileId, string destinationPath, CancellationToken cancellationToken);

    Task<OpenAiResult<JsonlPreviewResult>> PreviewFileJsonlAsync(
        string fileId,
        int maxLines,
        int maxBytes,
        CancellationToken cancellationToken);

    Task<OpenAiResult<OpenAiBatchListResponse>> ListBatchesAsync(string? status, CancellationToken cancellationToken);

    Task<OpenAiResult<OpenAiBatchObject>> GetBatchAsync(string batchId, CancellationToken cancellationToken);

    Task<OpenAiResult<OpenAiBatchObject>> CreateBatchAsync(
        string inputFileId,
        string? endpoint,
        string? completionWindow,
        CancellationToken cancellationToken);

    Task<OpenAiResult<OpenAiBatchObject>> CancelBatchAsync(string batchId, CancellationToken cancellationToken);

    Task<OpenAiResult<OpenAiBatchObject>> ResetBatchAsync(string batchId, CancellationToken cancellationToken);

}

/// <summary>API-backed <see cref="IFilesBatchesDataSource"/>.</summary>
public sealed class FilesBatchesDataSource : IFilesBatchesDataSource
{

    private readonly FilesBatchesService _service;

    public FilesBatchesDataSource(FilesBatchesService service)
    {

        _service = service;

    }

    public Task<OpenAiResult<OpenAiFileListResponse>> ListFilesAsync(string? purpose, CancellationToken cancellationToken) =>
        _service.ListFilesAsync(purpose, cancellationToken);

    public Task<OpenAiResult<OpenAiFileObject>> UploadFileAsync(string filePath, string purpose, CancellationToken cancellationToken) =>
        _service.UploadFileAsync(filePath, purpose, cancellationToken);

    public Task<OpenAiResult<OpenAiFileDeleteResponse>> DeleteFileAsync(string fileId, CancellationToken cancellationToken) =>
        _service.DeleteFileAsync(fileId, cancellationToken);

    public Task<OpenAiResult<bool>> DownloadFileContentAsync(string fileId, string destinationPath, CancellationToken cancellationToken) =>
        _service.DownloadFileContentAsync(fileId, destinationPath, cancellationToken);

    public Task<OpenAiResult<JsonlPreviewResult>> PreviewFileJsonlAsync(
        string fileId,
        int maxLines,
        int maxBytes,
        CancellationToken cancellationToken) =>
        _service.PreviewFileJsonlAsync(fileId, maxLines, maxBytes, cancellationToken);

    public Task<OpenAiResult<OpenAiBatchListResponse>> ListBatchesAsync(string? status, CancellationToken cancellationToken) =>
        _service.ListBatchesAsync(status, cancellationToken);

    public Task<OpenAiResult<OpenAiBatchObject>> GetBatchAsync(string batchId, CancellationToken cancellationToken) =>
        _service.GetBatchAsync(batchId, cancellationToken);

    public Task<OpenAiResult<OpenAiBatchObject>> CreateBatchAsync(
        string inputFileId,
        string? endpoint,
        string? completionWindow,
        CancellationToken cancellationToken) =>
        _service.CreateBatchAsync(inputFileId, endpoint, completionWindow, cancellationToken);

    public Task<OpenAiResult<OpenAiBatchObject>> CancelBatchAsync(string batchId, CancellationToken cancellationToken) =>
        _service.CancelBatchAsync(batchId, cancellationToken);

    public Task<OpenAiResult<OpenAiBatchObject>> ResetBatchAsync(string batchId, CancellationToken cancellationToken) =>
        _service.ResetBatchAsync(batchId, cancellationToken);

}
