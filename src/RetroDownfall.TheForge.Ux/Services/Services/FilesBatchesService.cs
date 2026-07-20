using RetroDownfall.TheForge.Core.Models.OpenAi;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Ux.ViewModels;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>
/// Route service for OpenAI-compatible <c>/v1/files</c> and <c>/v1/batches</c>. Uses
/// <see cref="OpenAiCompatApiClient"/> (bare OpenAI shapes) — never <c>ApiResponse&lt;T&gt;</c>.
/// </summary>
public sealed class FilesBatchesService
{

    public const string DefaultBatchEndpoint = "/v1/chat/completions";

    public const string DefaultCompletionWindow = "24h";

    private readonly OpenAiCompatApiClient _client;

    public FilesBatchesService(OpenAiCompatApiClient client)
    {

        _client = client;

    }

    public Task<OpenAiResult<OpenAiFileListResponse>> ListFilesAsync(string? purpose, CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build("/v1/files", ("purpose", purpose));

        return _client.GetAsync(path, TheForgeJsonContext.Default.OpenAiFileListResponse, cancellationToken);

    }

    public Task<OpenAiResult<OpenAiFileObject>> UploadFileAsync(
        string filePath,
        string purpose,
        CancellationToken cancellationToken) =>
        _client.PostMultipartFileAsync(
            "/v1/files",
            filePath,
            purpose,
            TheForgeJsonContext.Default.OpenAiFileObject,
            cancellationToken);

    public Task<OpenAiResult<OpenAiFileDeleteResponse>> DeleteFileAsync(string fileId, CancellationToken cancellationToken)
    {

        string path = "/v1/files/" + Uri.EscapeDataString(fileId);

        return _client.DeleteAsync(path, TheForgeJsonContext.Default.OpenAiFileDeleteResponse, cancellationToken);

    }

    public Task<OpenAiResult<bool>> DownloadFileContentAsync(
        string fileId,
        string destinationPath,
        CancellationToken cancellationToken)
    {

        string path = "/v1/files/" + Uri.EscapeDataString(fileId) + "/content";

        return _client.DownloadToFileAsync(path, destinationPath, cancellationToken);

    }

    public async Task<OpenAiResult<JsonlPreviewResult>> PreviewFileJsonlAsync(
        string fileId,
        int maxLines,
        int maxBytes,
        CancellationToken cancellationToken)
    {

        string path = "/v1/files/" + Uri.EscapeDataString(fileId) + "/content";

        OpenAiResult<Stream> streamResult = await _client
            .OpenContentStreamAsync(path, cancellationToken)
            .ConfigureAwait(false);

        if (!streamResult.Success || streamResult.Data is null)
        {

            return OpenAiResult<JsonlPreviewResult>.Fail(
                streamResult.ErrorCode,
                streamResult.ErrorMessage ?? "Failed to open file content.");

        }

        await using Stream stream = streamResult.Data;

        JsonlPreviewResult preview = await JsonlBoundedPreview
            .ReadAsync(stream, maxLines, maxBytes, cancellationToken)
            .ConfigureAwait(false);

        return OpenAiResult<JsonlPreviewResult>.Ok(preview);

    }

    public Task<OpenAiResult<OpenAiBatchListResponse>> ListBatchesAsync(string? status, CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build("/v1/batches", ("status", status));

        return _client.GetAsync(path, TheForgeJsonContext.Default.OpenAiBatchListResponse, cancellationToken);

    }

    public Task<OpenAiResult<OpenAiBatchObject>> GetBatchAsync(string batchId, CancellationToken cancellationToken)
    {

        string path = "/v1/batches/" + Uri.EscapeDataString(batchId);

        return _client.GetAsync(path, TheForgeJsonContext.Default.OpenAiBatchObject, cancellationToken);

    }

    public Task<OpenAiResult<OpenAiBatchObject>> CreateBatchAsync(
        string inputFileId,
        string? endpoint,
        string? completionWindow,
        CancellationToken cancellationToken)
    {

        OpenAiBatchRequest body = new(
            InputFileId: inputFileId,
            Endpoint: string.IsNullOrWhiteSpace(endpoint) ? DefaultBatchEndpoint : endpoint.Trim(),
            CompletionWindow: string.IsNullOrWhiteSpace(completionWindow) ? DefaultCompletionWindow : completionWindow.Trim());

        return _client.PostAsync(
            "/v1/batches",
            body,
            TheForgeJsonContext.Default.OpenAiBatchRequest,
            TheForgeJsonContext.Default.OpenAiBatchObject,
            cancellationToken);

    }

    public Task<OpenAiResult<OpenAiBatchObject>> CancelBatchAsync(string batchId, CancellationToken cancellationToken)
    {

        string path = "/v1/batches/" + Uri.EscapeDataString(batchId) + "/cancel";

        return _client.PostAsync(path, TheForgeJsonContext.Default.OpenAiBatchObject, cancellationToken);

    }

    public Task<OpenAiResult<OpenAiBatchObject>> ResetBatchAsync(string batchId, CancellationToken cancellationToken)
    {

        string path = "/v1/batches/" + Uri.EscapeDataString(batchId) + "/reset";

        return _client.PostAsync(path, TheForgeJsonContext.Default.OpenAiBatchObject, cancellationToken);

    }

}
