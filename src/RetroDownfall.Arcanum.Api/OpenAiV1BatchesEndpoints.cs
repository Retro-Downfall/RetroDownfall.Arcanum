using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api;

/// <summary>
/// OpenAI-compatible <c>/v1/batches</c> asynchronous bulk chat-completion processing (DESIGN.md
/// §11.21). These endpoints only create/read/cancel batch metadata — the actual JSONL processing
/// happens out-of-band in <see cref="BatchProcessingService"/>.
/// (<see cref="ExcludeFromCodeCoverageAttribute"/> is applied once on the primary
/// <c>OpenAiV1Endpoints.cs</c> partial declaration and covers this file too.)
/// </summary>
internal static partial class OpenAiV1Endpoints
{

    /// <summary>Phase 1 supports only chat-completion batches — mirrors the JSONL body contract <c>BatchProcessingService</c> understands.</summary>
    private const string SupportedBatchEndpoint = "/v1/chat/completions";

    private const string BatchIdPrefix = "batch_";

    internal static void MapOpenAiV1Batches(this RouteGroupBuilder v1)
    {

        _ = v1.MapPost("/batches", HandleCreateBatchAsync)
            .WithName("PostOpenAiBatches");

        _ = v1.MapGet("/batches", HandleListBatchesAsync)
            .WithName("GetOpenAiBatches");

        _ = v1.MapGet("/batches/{id}", HandleGetBatchAsync)
            .WithName("GetOpenAiBatch");

        _ = v1.MapPost("/batches/{id}/cancel", HandleCancelBatchAsync)
            .WithName("PostOpenAiBatchCancel");

    }

    private static async Task<IResult> HandleCreateBatchAsync(
        OpenAiBatchRequest? body,
        IBatchRepository batches,
        IUploadedFileRepository files,
        CancellationToken cancellationToken)
    {

        if (body is null || string.IsNullOrWhiteSpace(body.InputFileId))
        {

            return JsonError("Missing required parameter: 'input_file_id'.", "invalid_request_error", "missing_required_parameter", "input_file_id", StatusCodes.Status400BadRequest);

        }

        if (string.IsNullOrWhiteSpace(body.Endpoint))
        {

            return JsonError("Missing required parameter: 'endpoint'.", "invalid_request_error", "missing_required_parameter", "endpoint", StatusCodes.Status400BadRequest);

        }

        if (!string.Equals(body.Endpoint, SupportedBatchEndpoint, StringComparison.Ordinal))
        {

            return JsonError(
                $"'endpoint' must be '{SupportedBatchEndpoint}'. Arcanum does not yet support batches for other endpoints.",
                "invalid_request_error",
                "invalid_value",
                "endpoint",
                StatusCodes.Status400BadRequest);

        }

        if (!TryParseFileId(body.InputFileId, out Guid inputFileId))
        {

            return JsonError($"No such file: '{body.InputFileId}'.", "invalid_request_error", "not_found", "input_file_id", StatusCodes.Status404NotFound);

        }

        UploadedFileRecord? inputFile = await files.GetByIdAsync(inputFileId, cancellationToken).ConfigureAwait(false);

        if (inputFile is null)
        {

            return JsonError($"No such file: '{body.InputFileId}'.", "invalid_request_error", "not_found", "input_file_id", StatusCodes.Status404NotFound);

        }

        BatchRecord record = new(
            Id: Guid.NewGuid(),
            InputFileId: inputFileId,
            Endpoint: body.Endpoint,
            Status: BatchStatuses.Validating,
            CreatedAt: DateTimeOffset.UtcNow,
            CompletedAt: null,
            OutputFileId: null,
            ErrorFileId: null);

        await batches.CreateAsync(record, cancellationToken).ConfigureAwait(false);

        OpenAiBatchObject wire = OpenAiBatchObject.FromRecord(record, OpenAiBatchRequestCounts.Empty);

        return Results.Json(wire, ArcanumJsonContext.Default.OpenAiBatchObject, statusCode: StatusCodes.Status200OK);

    }

    private static async Task<IResult> HandleGetBatchAsync(
        string id,
        IBatchRepository batches,
        CancellationToken cancellationToken)
    {

        if (!TryParseBatchId(id, out Guid batchId))
        {

            return BatchNotFoundResult(id);

        }

        BatchRecord? record = await batches.GetByIdAsync(batchId, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {

            return BatchNotFoundResult(id);

        }

        OpenAiBatchRequestCounts counts = await BatchRequestCounter.ComputeAsync(record, cancellationToken).ConfigureAwait(false);

        return Results.Json(OpenAiBatchObject.FromRecord(record, counts), ArcanumJsonContext.Default.OpenAiBatchObject);

    }

    private static async Task<IResult> HandleListBatchesAsync(
        string? status,
        IBatchRepository batches,
        CancellationToken cancellationToken)
    {

        IReadOnlyList<BatchRecord> records = await batches.ListAsync(status, cancellationToken).ConfigureAwait(false);

        List<OpenAiBatchObject> data = new(records.Count);

        foreach (BatchRecord record in records)
        {

            OpenAiBatchRequestCounts counts = await BatchRequestCounter.ComputeAsync(record, cancellationToken).ConfigureAwait(false);

            data.Add(OpenAiBatchObject.FromRecord(record, counts));

        }

        return Results.Json(new OpenAiBatchListResponse(data), ArcanumJsonContext.Default.OpenAiBatchListResponse);

    }

    private static async Task<IResult> HandleCancelBatchAsync(
        string id,
        IBatchRepository batches,
        CancellationToken cancellationToken)
    {

        if (!TryParseBatchId(id, out Guid batchId))
        {

            return BatchNotFoundResult(id);

        }

        BatchRecord? record = await batches.GetByIdAsync(batchId, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {

            return BatchNotFoundResult(id);

        }

        // Idempotent: cancelling an already-terminal batch is a no-op that just returns its current
        // state, matching OpenAI's own behavior rather than erroring on a double-cancel.
        if (!BatchStatuses.IsTerminal(record.Status))
        {

            await batches.UpdateStatusAsync(
                batchId,
                BatchStatuses.Cancelled,
                DateTimeOffset.UtcNow,
                record.OutputFileId,
                record.ErrorFileId,
                cancellationToken).ConfigureAwait(false);

            record = await batches.GetByIdAsync(batchId, cancellationToken).ConfigureAwait(false) ?? record;

        }

        OpenAiBatchRequestCounts counts = await BatchRequestCounter.ComputeAsync(record, cancellationToken).ConfigureAwait(false);

        return Results.Json(OpenAiBatchObject.FromRecord(record, counts), ArcanumJsonContext.Default.OpenAiBatchObject);

    }

    private static bool TryParseBatchId(string wireId, out Guid id)
    {

        id = Guid.Empty;

        if (string.IsNullOrEmpty(wireId) || !wireId.StartsWith(BatchIdPrefix, StringComparison.Ordinal))
        {

            return false;

        }

        return Guid.TryParseExact(wireId.AsSpan(BatchIdPrefix.Length), "N", out id);

    }

    private static IResult BatchNotFoundResult(string wireId) =>
        JsonError($"No such batch: '{wireId}'.", "invalid_request_error", "not_found", param: "id", StatusCodes.Status404NotFound);

    /// <summary>
    /// Executes a single <c>/v1/batches</c> JSONL request line's <see cref="OpenAiChatRequest"/>
    /// body through the same mapper (<see cref="OpenAiChatCompletionMapper.ToPingRequest"/>) and
    /// buffered response shape (<see cref="OpenAiChatResponse"/>) as <c>POST /v1/chat/completions</c>
    /// non-streaming — but returns a <see cref="Result{T}"/> instead of an <see cref="IResult"/> so
    /// <c>BatchProcessingService</c> (a background worker, not an HTTP handler) can record a
    /// per-line success/failure without needing an <see cref="HttpContext"/>. Deliberately skips the
    /// HTTP handler's request-shape pre-checks (multimodal part limits, `tools`/`tool_choice`
    /// rejection, Scrying image gating) — a batch line that trips one of those still gets a clean
    /// per-line failure via <see cref="IArcanumIntelligenceProvider.ExecutePromptAsync"/>'s own
    /// validation/model-resolution, it just surfaces a less specific error code than the live HTTP
    /// path would for the same malformed request.
    /// </summary>
    internal static async Task<Result<OpenAiChatResponse>> ExecuteChatRequestForBatchAsync(
        OpenAiChatRequest body,
        IArcanumIntelligenceProvider intelligence,
        ArcanumSettings settings,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(body.Model))
        {

            return Result<OpenAiChatResponse>.Failure(new Error(ErrorCodes.Validation.InvalidPrompt, "`model` is required."));

        }

        if (body.Messages is null || body.Messages.Count == 0)
        {

            return Result<OpenAiChatResponse>.Failure(new Error(ErrorCodes.Validation.InvalidPrompt, "`messages` is required and must be non-empty."));

        }

        PingRequest ping = OpenAiChatCompletionMapper.ToPingRequest(body);

        Result<PromptTurnResult> result = await intelligence.ExecutePromptAsync(ping, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            return Result<OpenAiChatResponse>.Failure(result.Error);

        }

        PromptTurnResult turn = result.Value;

        OpenAiChatAssistantMessage message = new(
            Role: "assistant",
            Content: turn.Text,
            ToolCalls: MapBufferedToolCalls(turn.ToolCalls),
            Refusal: null);

        OpenAiChatResponse response = new(
            Id: "chatcmpl-" + Guid.NewGuid().ToString("N"),
            ObjectKind: "chat.completion",
            Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model: body.Model.Trim(),
            Choices:
            [
                new OpenAiChatChoice(
                    Index: 0,
                    Message: message,
                    FinishReason: ResolveFinishReason(turn.FinishReason),
                    Logprobs: null),
            ],
            Usage: turn.Usage,
            SystemFingerprint: ResolveSystemFingerprint(settings),
            ServiceTier: null);

        return Result<OpenAiChatResponse>.Success(response);

    }

}
