using System.Buffers;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api;

internal static class OpenAiV1Endpoints
{

    private static readonly byte[] SseDataPrefix = "data: "u8.ToArray();

    private static readonly byte[] SseLineBreak = "\n\n"u8.ToArray();

    private static readonly byte[] SseDone = "data: [DONE]\n\n"u8.ToArray();

    private static readonly long ProcessStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static readonly string DefaultSystemFingerprint = BuildDefaultSystemFingerprint();

    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "user",
        "assistant",
        "tool",
        "developer",
    };

    internal static void MapOpenAiV1ChatCompletions(this RouteGroupBuilder v1)
    {
        _ = v1.MapPost("/chat/completions", HandleChatCompletionsAsync).WithName("PostOpenAiChatCompletions");
    }

    internal static void MapOpenAiV1Models(this RouteGroupBuilder v1)
    {
        _ = v1.MapGet("/models", HandleListModels).WithName("GetOpenAiModels");
    }

    private static IResult HandleListModels(IOptionsSnapshot<ArcanumSettings> settings)
    {
        ArcanumSettings arc = settings.Value;

        ProviderSettings[] providers = arc.Providers;

        List<OpenAiModel> data = [];

        Dictionary<string, bool> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProviderSettings provider in providers)
        {
            string ownedBy = string.IsNullOrWhiteSpace(provider.Name) ? "system" : provider.Name;

            foreach (string model in provider.Models)
            {
                if (string.IsNullOrWhiteSpace(model))
                {
                    continue;
                }

                string id = model.Trim();

                if (!seen.TryAdd(id, true))
                {
                    continue;
                }

                data.Add(new OpenAiModel(id, "model", ProcessStartUnix, ownedBy));
            }
        }

        OpenAiModelListResponse response = new(data);

        return Results.Json(response, ArcanumJsonContext.Default.OpenAiModelListResponse);
    }

    private static async Task<IResult> HandleChatCompletionsAsync(
        HttpContext httpContext,
        IArcanumIntelligenceProvider intelligence,
        IOptionsSnapshot<ArcanumSettings> settings,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        OpenAiChatRequest? body;

        try
        {
            body = await httpContext.Request
                .ReadFromJsonAsync(ArcanumJsonContext.Default.OpenAiChatRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return JsonError(
                "Request body could not be parsed as a chat completion request: " + ex.Message,
                "invalid_request_error",
                code: "invalid_json",
                param: null,
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (body is null)
        {
            return JsonError(
                "Request body is required.",
                "invalid_request_error",
                code: "missing_body",
                param: null,
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(body.Model))
        {
            return JsonError(
                "`model` is required.",
                "invalid_request_error",
                code: "missing_required_parameter",
                param: "model",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (body.Messages is null || body.Messages.Count == 0)
        {
            return JsonError(
                "`messages` is required and must be non-empty.",
                "invalid_request_error",
                code: "missing_required_parameter",
                param: "messages",
                statusCode: StatusCodes.Status400BadRequest);
        }

        for (int i = 0; i < body.Messages.Count; i++)
        {
            OpenAiChatMessage m = body.Messages[i];

            if (string.IsNullOrWhiteSpace(m.Role))
            {
                return JsonError(
                    $"messages[{i}].role is required.",
                    "invalid_request_error",
                    code: "missing_required_parameter",
                    param: $"messages[{i}].role",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!AllowedRoles.Contains(m.Role))
            {
                return JsonError(
                    $"messages[{i}].role '{m.Role}' is not one of {string.Join(", ", AllowedRoles)}.",
                    "invalid_request_error",
                    code: "invalid_value",
                    param: $"messages[{i}].role",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        PingRequest ping = OpenAiChatCompletionMapper.ToPingRequest(body);

        if (!ProviderResolver.TryResolveProviderForModel(settings.Value, body.Model, out _, out string resolvedModel)
            || string.IsNullOrWhiteSpace(resolvedModel))
        {
            return JsonError(
                $"Model '{body.Model}' is not configured on any provider.",
                "invalid_request_error",
                code: "model_not_found",
                param: "model",
                statusCode: StatusCodes.Status400BadRequest);
        }

        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        string completionId = "chatcmpl-" + Guid.NewGuid().ToString("N");

        string systemFingerprint = ResolveSystemFingerprint(settings.Value);

        if (!body.Stream)
        {
            return await HandleBufferedAsync(
                intelligence,
                ping,
                completionId,
                created,
                resolvedModel,
                systemFingerprint,
                cancellationToken)
                .ConfigureAwait(false);
        }

        bool includeUsage = body.StreamOptions?.IncludeUsage == true;

        ILogger streamLogger = loggerFactory.CreateLogger(typeof(OpenAiV1Endpoints));

        return await HandleStreamingAsync(
            httpContext,
            intelligence,
            ping,
            completionId,
            created,
            resolvedModel,
            systemFingerprint,
            includeUsage,
            streamLogger,
            cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> HandleBufferedAsync(
        IArcanumIntelligenceProvider intelligence,
        PingRequest ping,
        string completionId,
        long created,
        string resolvedModel,
        string systemFingerprint,
        CancellationToken cancellationToken)
    {
        Result<PromptTurnResult> result = await intelligence.ExecutePromptAsync(ping, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return JsonError(
                "Inference failed. See server logs for details.",
                "api_error",
                code: result.Error.Code,
                param: null,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        PromptTurnResult turn = result.Value;

        OpenAiToolCall[]? toolCalls = MapToolCalls(turn.ToolCalls);

        string finishReason = ResolveFinishReason(turn.FinishReason, toolCalls);

        OpenAiChatAssistantMessage message = new(
            Role: "assistant",
            Content: turn.Text,
            ToolCalls: toolCalls,
            Refusal: null);

        OpenAiChatResponse response = new(
            Id: completionId,
            ObjectKind: "chat.completion",
            Created: created,
            Model: resolvedModel,
            Choices:
            [
                new OpenAiChatChoice(
                    Index: 0,
                    Message: message,
                    FinishReason: finishReason,
                    Logprobs: null),
            ],
            Usage: turn.Usage,
            SystemFingerprint: systemFingerprint,
            ServiceTier: null);

        return Results.Json(response, ArcanumJsonContext.Default.OpenAiChatResponse);
    }

    private static async Task<IResult> HandleStreamingAsync(
        HttpContext httpContext,
        IArcanumIntelligenceProvider intelligence,
        PingRequest ping,
        string completionId,
        long created,
        string resolvedModel,
        string systemFingerprint,
        bool includeUsage,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        httpContext.Response.ContentType = "text/event-stream; charset=utf-8";

        httpContext.Response.Headers.CacheControl = "no-cache";

        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(
            httpContext.RequestAborted,
            cancellationToken);

        CancellationToken ct = streamCts.Token;

        ChatCompletionUsage? sseUsage = null;

        bool aborted = false;

        bool streamErrored = false;

        try
        {
            await WriteRoleChunkAsync(httpContext, completionId, created, resolvedModel, systemFingerprint, ct).ConfigureAwait(false);

            await foreach (IntelligenceEvent ev in intelligence.StreamPromptAsync(ping, ct).ConfigureAwait(false))
            {
                switch (ev.Type)
                {
                    case IntelligenceEventType.Token when !string.IsNullOrEmpty(ev.Data):
                        await WriteContentChunkAsync(httpContext, completionId, created, resolvedModel, systemFingerprint, ev.Data, ct).ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.ToolCall when ev.ToolCall is { } toolCall:
                        await WriteToolCallChunkAsync(httpContext, completionId, created, resolvedModel, systemFingerprint, toolCall, ct).ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.Result:
                        sseUsage = ev.Usage;
                        break;

                    case IntelligenceEventType.Error:
                        await WriteStreamErrorAsync(httpContext, ev.Message, ct).ConfigureAwait(false);
                        streamErrored = true;
                        return Results.Empty;

                    case IntelligenceEventType.Status:
                    case IntelligenceEventType.ToolResult:
                    case IntelligenceEventType.ConversationBound:
                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            aborted = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception while streaming OpenAI chat completion {CompletionId}.", completionId);

            try
            {
                await WriteStreamErrorAsync(
                    httpContext,
                    "Inference failed. See server logs for details.",
                    CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception writeEx)
            {
                logger.LogWarning(writeEx, "Failed to write terminal error to OpenAI SSE stream {CompletionId}.", completionId);
            }

            return Results.Empty;
        }

        if (streamErrored)
        {
            return Results.Empty;
        }

        try
        {
            if (includeUsage)
            {
                await WriteFinalContentChunkAsync(httpContext, completionId, created, resolvedModel, systemFingerprint, finishReason: "stop", ct: aborted ? CancellationToken.None : ct).ConfigureAwait(false);

                await WriteUsageOnlyChunkAsync(httpContext, completionId, created, resolvedModel, systemFingerprint, sseUsage, aborted ? CancellationToken.None : ct).ConfigureAwait(false);
            }
            else
            {
                await WriteFinalContentChunkAsync(httpContext, completionId, created, resolvedModel, systemFingerprint, finishReason: "stop", ct: aborted ? CancellationToken.None : ct).ConfigureAwait(false);
            }

            await httpContext.Response.Body.WriteAsync(SseDone, aborted ? CancellationToken.None : ct).ConfigureAwait(false);

            await httpContext.Response.Body.FlushAsync(aborted ? CancellationToken.None : ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write terminal frames to OpenAI SSE stream {CompletionId}.", completionId);
        }

        return Results.Empty;
    }

    private static async Task WriteRoleChunkAsync(
        HttpContext httpContext,
        string completionId,
        long created,
        string resolvedModel,
        string systemFingerprint,
        CancellationToken ct)
    {
        OpenAiChatChunk chunk = new(
            Id: completionId,
            ObjectKind: "chat.completion.chunk",
            Created: created,
            Model: resolvedModel,
            Choices:
            [
                new OpenAiChatStreamChoice(
                    Index: 0,
                    Delta: new OpenAiDelta(Role: "assistant"),
                    FinishReason: null,
                    Logprobs: null),
            ],
            Usage: null,
            SystemFingerprint: systemFingerprint);

        await WriteSseJsonAsync(httpContext, chunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct).ConfigureAwait(false);
    }

    private static async Task WriteContentChunkAsync(
        HttpContext httpContext,
        string completionId,
        long created,
        string resolvedModel,
        string systemFingerprint,
        string content,
        CancellationToken ct)
    {
        OpenAiChatChunk chunk = new(
            Id: completionId,
            ObjectKind: "chat.completion.chunk",
            Created: created,
            Model: resolvedModel,
            Choices:
            [
                new OpenAiChatStreamChoice(
                    Index: 0,
                    Delta: new OpenAiDelta(Content: content),
                    FinishReason: null,
                    Logprobs: null),
            ],
            Usage: null,
            SystemFingerprint: systemFingerprint);

        await WriteSseJsonAsync(httpContext, chunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct).ConfigureAwait(false);
    }

    private static async Task WriteToolCallChunkAsync(
        HttpContext httpContext,
        string completionId,
        long created,
        string resolvedModel,
        string systemFingerprint,
        IntelligenceToolCallEvent toolCall,
        CancellationToken ct)
    {
        OpenAiStreamToolCall streamToolCall = new(
            Index: toolCall.Index,
            Id: toolCall.CallId,
            Type: "function",
            Function: new OpenAiFunctionCall(
                Name: toolCall.Name,
                Arguments: toolCall.ArgumentsJson));

        OpenAiChatChunk chunk = new(
            Id: completionId,
            ObjectKind: "chat.completion.chunk",
            Created: created,
            Model: resolvedModel,
            Choices:
            [
                new OpenAiChatStreamChoice(
                    Index: 0,
                    Delta: new OpenAiDelta(ToolCalls: [streamToolCall]),
                    FinishReason: null,
                    Logprobs: null),
            ],
            Usage: null,
            SystemFingerprint: systemFingerprint);

        await WriteSseJsonAsync(httpContext, chunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct).ConfigureAwait(false);
    }

    private static async Task WriteFinalContentChunkAsync(
        HttpContext httpContext,
        string completionId,
        long created,
        string resolvedModel,
        string systemFingerprint,
        string finishReason,
        CancellationToken ct)
    {
        OpenAiChatChunk chunk = new(
            Id: completionId,
            ObjectKind: "chat.completion.chunk",
            Created: created,
            Model: resolvedModel,
            Choices:
            [
                new OpenAiChatStreamChoice(
                    Index: 0,
                    Delta: new OpenAiDelta(),
                    FinishReason: finishReason,
                    Logprobs: null),
            ],
            Usage: null,
            SystemFingerprint: systemFingerprint);

        await WriteSseJsonAsync(httpContext, chunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct).ConfigureAwait(false);
    }

    private static async Task WriteUsageOnlyChunkAsync(
        HttpContext httpContext,
        string completionId,
        long created,
        string resolvedModel,
        string systemFingerprint,
        ChatCompletionUsage? usage,
        CancellationToken ct)
    {
        OpenAiChatChunk chunk = new(
            Id: completionId,
            ObjectKind: "chat.completion.chunk",
            Created: created,
            Model: resolvedModel,
            Choices: [],
            Usage: usage,
            SystemFingerprint: systemFingerprint);

        await WriteSseJsonAsync(httpContext, chunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct).ConfigureAwait(false);
    }

    private static async Task WriteStreamErrorAsync(HttpContext httpContext, string message, CancellationToken ct)
    {
        OpenAiErrorResponse error = new(
            new OpenAiErrorDetail(
                Message: message ?? "Inference failed.",
                Type: "api_error",
                Param: null,
                Code: "inference_failed"));

        await WriteSseJsonAsync(httpContext, error, ArcanumJsonContext.Default.OpenAiErrorResponse, ct).ConfigureAwait(false);

        await httpContext.Response.Body.WriteAsync(SseDone, ct).ConfigureAwait(false);

        await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteSseJsonAsync<T>(
        HttpContext httpContext,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new(512);

        await httpContext.Response.Body.WriteAsync(SseDataPrefix, cancellationToken).ConfigureAwait(false);

        await using (Utf8JsonWriter jsonWriter = new(buffer, new JsonWriterOptions { Indented = false }))
        {
            JsonSerializer.Serialize(jsonWriter, value, typeInfo);
        }

        await httpContext.Response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

        await httpContext.Response.Body.WriteAsync(SseLineBreak, cancellationToken).ConfigureAwait(false);

        await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static OpenAiToolCall[]? MapToolCalls(List<PromptToolCall>? promptToolCalls)
    {
        if (promptToolCalls is not { Count: > 0 })
        {
            return null;
        }

        OpenAiToolCall[] dest = new OpenAiToolCall[promptToolCalls.Count];

        for (int i = 0; i < promptToolCalls.Count; i++)
        {
            PromptToolCall p = promptToolCalls[i];

            dest[i] = new OpenAiToolCall(
                Id: p.CallId,
                Type: "function",
                Function: new OpenAiFunctionCall(p.Name, p.ArgumentsJson));
        }

        return dest;
    }

    private static string ResolveFinishReason(string? hubFinishReason, OpenAiToolCall[]? toolCalls)
    {
        if (!string.IsNullOrWhiteSpace(hubFinishReason))
        {
            return hubFinishReason!;
        }

        return toolCalls is { Length: > 0 } ? "tool_calls" : "stop";
    }

    private static string ResolveSystemFingerprint(ArcanumSettings settings)
    {
        string? configured = settings.Host.SystemFingerprint;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return DefaultSystemFingerprint;
    }

    private static string BuildDefaultSystemFingerprint()
    {
        try
        {
            Assembly asm = typeof(OpenAiV1Endpoints).Assembly;

            string version = asm
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "unknown";

            int plus = version.IndexOf('+');

            if (plus >= 0)
            {
                version = version[..plus];
            }

            return "arcanum-" + version.Trim();
        }
        catch (Exception)
        {
            return "arcanum";
        }
    }

    private static IResult JsonError(string message, string type, string? code, string? param, int statusCode)
    {
        OpenAiErrorResponse response = new(new OpenAiErrorDetail(message, type, param, code));

        return Results.Json(response, ArcanumJsonContext.Default.OpenAiErrorResponse, statusCode: statusCode);
    }

}
