using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api;

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible HTTP streaming endpoints; covered via OpenAiV1EndpointTests integration smoke.
internal static class OpenAiV1Endpoints
{

    private static readonly byte[] SseDataPrefix = "data: "u8.ToArray();

    private static readonly byte[] SseLineBreak = "\n\n"u8.ToArray();

    private static readonly byte[] SseDone = "data: [DONE]\n\n"u8.ToArray();

    // W3.5: interleave SSE keep-alive comments during idle gaps (slow provider, multi-round tool
    // loops) so reverse proxies / load balancers do not idle-timeout an otherwise-healthy stream.
    private static readonly TimeSpan StreamKeepAliveInterval = TimeSpan.FromSeconds(15);

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
        _ = v1.MapPost("/chat/completions", HandleChatCompletionsAsync)
            .WithName("PostOpenAiChatCompletions")
            .WithLargeRequestBody();
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

            foreach (ModelEntry model in provider.Models)
            {
                if (string.IsNullOrWhiteSpace(model.Name))
                {
                    continue;
                }

                string id = model.Name.Trim();

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
        catch (JsonException)
        {
            return JsonError(
                "Request body could not be parsed as a chat completion request.",
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

        Result messageCountBounds = PingRequestBoundsValidator.ValidateOpenApiMessageCount(body.Messages.Count, settings.Value);

        if (messageCountBounds.IsFailure)
        {
            return JsonError(
                messageCountBounds.Error.Message,
                "invalid_request_error",
                code: "invalid_value",
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

            // W3.5: bound the multimodal parts array and reject unknown part types up front (the
            // mapper otherwise silently drops unsupported parts, and a huge parts array allocates
            // heavily before content-length checks apply).
            if (m.Content?.Parts is { } parts)
            {
                int maxParts = ArcanumSettingClamps.MaxContentPartsPerMessage(
                    (settings.Value.Intelligence ?? new IntelligenceSettings()).MaxContentPartsPerMessage);

                if (parts.Length > maxParts)
                {
                    return JsonError(
                        $"messages[{i}].content has {parts.Length} parts; the maximum is {maxParts}.",
                        "invalid_request_error",
                        code: "invalid_value",
                        param: $"messages[{i}].content",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                for (int j = 0; j < parts.Length; j++)
                {
                    OpenAiContentPart part = parts[j];

                    if (part is not null && !IsSupportedContentPartType(part.Type))
                    {
                        return JsonError(
                            $"messages[{i}].content[{j}].type '{part.Type}' is not supported; expected 'text' or 'image_url'.",
                            "invalid_request_error",
                            code: "invalid_value",
                            param: $"messages[{i}].content[{j}].type",
                            statusCode: StatusCodes.Status400BadRequest);
                    }
                }
            }
        }

        if (body.N is int n && n != 1)
        {
            return JsonError(
                "`n` must be 1 when specified. Arcanum does not support multiple completion choices.",
                "invalid_request_error",
                code: "invalid_value",
                param: "n",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (body.Tools is { Length: > 0 })
        {
            return JsonError(
                "Client-supplied `tools` are not supported. Arcanum uses its own server-side MCP toolset.",
                "invalid_request_error",
                code: "unsupported_parameter",
                param: "tools",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (body.ToolChoice is { } toolChoice
            && toolChoice.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            && !IsDefaultToolChoice(toolChoice))
        {
            return JsonError(
                "Client-supplied `tool_choice` is not supported. Arcanum uses its own server-side MCP toolset.",
                "invalid_request_error",
                code: "unsupported_parameter",
                param: "tool_choice",
                statusCode: StatusCodes.Status400BadRequest);
        }

        PingRequest ping = OpenAiChatCompletionMapper.ToPingRequest(body);

        Result pingBounds = PingRequestBoundsValidator.Validate(ping, settings.Value);

        if (pingBounds.IsFailure)
        {
            return JsonError(
                pingBounds.Error.Message,
                "invalid_request_error",
                code: "invalid_value",
                param: null,
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!ProviderResolver.TryResolveProviderForModel(settings.Value, body.Model, out ProviderSettings? resolvedProvider, out string resolvedModel)
            || resolvedProvider is null
            || string.IsNullOrWhiteSpace(resolvedModel))
        {
            return JsonError(
                $"Model '{body.Model}' is not configured on any provider.",
                "invalid_request_error",
                code: "model_not_found",
                param: "model",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Scrying — reject images before any inference token is consumed. Count/MIME/size are
        // validated against the mapped PingRequest (shared with the native ping path); vision
        // capability is checked against the already-resolved provider/model.
        if (ScryingValidator.RequestContainsImages(ping))
        {
            ScryingSettings scrying = settings.Value.Scrying ?? new ScryingSettings();

            if (!scrying.Enabled)
            {
                return JsonError(
                    "Scrying is disabled. Enable Arcanum:Scrying:Enabled to send images.",
                    "invalid_request_error",
                    code: "feature_disabled",
                    param: null,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            Result scryingShape = ScryingValidator.ValidateRequestImages(ping, scrying);

            if (scryingShape.IsFailure)
            {
                return JsonError(
                    scryingShape.Error.Message,
                    "invalid_request_error",
                    code: MapScryingOpenAiErrorCode(scryingShape.Error.Code),
                    param: null,
                    statusCode: ArcanumErrorMapper.ResolveStatusCode(scryingShape.Error.Code));
            }

            if (!ProviderResolver.SupportsVision(resolvedProvider, resolvedModel))
            {
                return JsonError(
                    $"Model '{resolvedModel}' does not support vision. Use a vision-capable model.",
                    "invalid_request_error",
                    code: "vision_not_supported",
                    param: "model",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        string completionId = "chatcmpl-" + Guid.NewGuid().ToString("N");

        string echoModel = body.Model.Trim();

        string systemFingerprint = ResolveSystemFingerprint(settings.Value);

        if (!body.Stream)
        {
            return await HandleBufferedAsync(
                intelligence,
                ping,
                completionId,
                created,
                echoModel,
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
            echoModel,
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
        string echoModel,
        string systemFingerprint,
        CancellationToken cancellationToken)
    {
        Result<PromptTurnResult> result = await intelligence.ExecutePromptAsync(ping, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return JsonError(
                ResolvePublicInferenceFailureMessage(result.Error.Code),
                "api_error",
                code: MapPublicOpenAiErrorCode(result.Error.Code),
                param: null,
                statusCode: ResolveOpenAiInferenceFailureStatusCode(result.Error.Code));
        }

        PromptTurnResult turn = result.Value;

        string finishReason = ResolveFinishReason(turn.FinishReason);

        OpenAiChatAssistantMessage message = new(
            Role: "assistant",
            Content: turn.Text,
            ToolCalls: null,
            Refusal: null);

        OpenAiChatResponse response = new(
            Id: completionId,
            ObjectKind: "chat.completion",
            Created: created,
            Model: echoModel,
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
        string echoModel,
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

        ArrayBufferWriter<byte> sseBuffer = new(512);

        ChatCompletionUsage? sseUsage = null;

        string? sseFinishReason = null;

        bool aborted = false;

        bool streamErrored = false;

        bool disconnected = false;

        try
        {
            await WriteRoleChunkAsync(httpContext, sseBuffer, completionId, created, echoModel, systemFingerprint, ct).ConfigureAwait(false);

            // Pump the hub stream manually so idle gaps can be filled with SSE keep-alive comments.
            // A single in-flight MoveNextAsync is raced against a keep-alive delay; the delay is
            // cancelled the moment an event arrives so no timer lingers (the same MoveNextAsync task
            // is kept across keep-alive cycles — re-issuing MoveNextAsync while one is pending would
            // be an invalid concurrent enumeration).
            await using IAsyncEnumerator<IntelligenceEvent> enumerator =
                intelligence.StreamPromptAsync(ping, ct).GetAsyncEnumerator(ct);

            Task<bool> move = enumerator.MoveNextAsync().AsTask();

            while (true)
            {
                using CancellationTokenSource delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                Task keepAliveDelay = Task.Delay(StreamKeepAliveInterval, delayCts.Token);

                Task completed = await Task.WhenAny(move, keepAliveDelay).ConfigureAwait(false);

                if (completed == keepAliveDelay)
                {
                    await SseStreamWriter.WriteKeepAliveAsync(httpContext, ct).ConfigureAwait(false);

                    continue;
                }

                delayCts.Cancel();

                if (!await move.ConfigureAwait(false))
                {
                    break;
                }

                IntelligenceEvent ev = enumerator.Current;

                switch (ev.Type)
                {
                    case IntelligenceEventType.Token when !string.IsNullOrEmpty(ev.Data):
                        await WriteContentChunkAsync(httpContext, sseBuffer, completionId, created, echoModel, systemFingerprint, ev.Data, ct).ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.ToolCall:
                        break;

                    case IntelligenceEventType.Result:
                        sseUsage = ev.Usage;

                        sseFinishReason = ev.FinishReason;

                        break;

                    case IntelligenceEventType.Error:
                        await WriteStreamErrorAsync(
                            httpContext,
                            sseBuffer,
                            completionId,
                            created,
                            echoModel,
                            systemFingerprint,
                            ev.Data,
                            ct).ConfigureAwait(false);
                        streamErrored = true;
                        return Results.Empty;

                    case IntelligenceEventType.Status:
                    case IntelligenceEventType.ToolResult:
                    case IntelligenceEventType.SessionBound:
                    case IntelligenceEventType.ConversationBound:
                    case IntelligenceEventType.Warded:
                    case IntelligenceEventType.WardResolved:
                    default:
                        break;
                }

                move = enumerator.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException)
        {
            aborted = true;
        }
        catch (Exception ex) when (ClientDisconnect.IsClientDisconnect(ex, httpContext))
        {
            // Client dropped the TCP connection ungracefully (broken pipe / reset). Cancel the
            // linked inference CTS so the producer stops promptly, and never attempt to write an
            // error frame — or any further terminal frame — to a dead socket.
            streamCts.Cancel();

            aborted = true;

            disconnected = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception while streaming OpenAI chat completion {CompletionId}.", completionId);

            try
            {
                await WriteStreamErrorAsync(
                    httpContext,
                    sseBuffer,
                    completionId,
                    created,
                    echoModel,
                    systemFingerprint,
                    rawMessage: null,
                    httpContext.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (Exception writeEx)
            {
                logger.LogWarning(writeEx, "Failed to write terminal error to OpenAI SSE stream {CompletionId}.", completionId);
            }

            return Results.Empty;
        }

        if (streamErrored || disconnected)
        {
            return Results.Empty;
        }

        try
        {
            string terminalFinishReason = ResolveFinishReason(sseFinishReason);

            if (includeUsage)
            {
                await WriteFinalContentChunkAsync(httpContext, sseBuffer, completionId, created, echoModel, systemFingerprint, finishReason: terminalFinishReason, ct: aborted ? CancellationToken.None : ct).ConfigureAwait(false);

                await WriteUsageOnlyChunkAsync(httpContext, sseBuffer, completionId, created, echoModel, systemFingerprint, sseUsage, aborted ? CancellationToken.None : ct).ConfigureAwait(false);
            }
            else
            {
                await WriteFinalContentChunkAsync(httpContext, sseBuffer, completionId, created, echoModel, systemFingerprint, finishReason: terminalFinishReason, ct: aborted ? CancellationToken.None : ct).ConfigureAwait(false);
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
        ArrayBufferWriter<byte> sseBuffer,
        string completionId,
        long created,
        string echoModel,
        string systemFingerprint,
        CancellationToken ct)
    {
        OpenAiChatChunk chunk = new(
            Id: completionId,
            ObjectKind: "chat.completion.chunk",
            Created: created,
            Model: echoModel,
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

        await WriteSseJsonAsync(httpContext, sseBuffer, chunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct).ConfigureAwait(false);
    }

    private static async Task WriteContentChunkAsync(
        HttpContext httpContext,
        ArrayBufferWriter<byte> sseBuffer,
        string completionId,
        long created,
        string echoModel,
        string systemFingerprint,
        string content,
        CancellationToken ct)
    {
        OpenAiChatChunk chunk = new(
            Id: completionId,
            ObjectKind: "chat.completion.chunk",
            Created: created,
            Model: echoModel,
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

        await WriteSseJsonAsync(httpContext, sseBuffer, chunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct).ConfigureAwait(false);
    }

    private static async Task WriteFinalContentChunkAsync(
        HttpContext httpContext,
        ArrayBufferWriter<byte> sseBuffer,
        string completionId,
        long created,
        string echoModel,
        string systemFingerprint,
        string finishReason,
        CancellationToken ct)
    {
        OpenAiChatChunk chunk = new(
            Id: completionId,
            ObjectKind: "chat.completion.chunk",
            Created: created,
            Model: echoModel,
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

        await WriteSseJsonAsync(httpContext, sseBuffer, chunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct).ConfigureAwait(false);
    }

    private static async Task WriteUsageOnlyChunkAsync(
        HttpContext httpContext,
        ArrayBufferWriter<byte> sseBuffer,
        string completionId,
        long created,
        string echoModel,
        string systemFingerprint,
        ChatCompletionUsage? usage,
        CancellationToken ct)
    {
        OpenAiChatChunk chunk = new(
            Id: completionId,
            ObjectKind: "chat.completion.chunk",
            Created: created,
            Model: echoModel,
            Choices: [],
            Usage: usage,
            SystemFingerprint: systemFingerprint);

        await WriteSseJsonAsync(httpContext, sseBuffer, chunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct).ConfigureAwait(false);
    }

    private static async Task WriteStreamErrorAsync(
        HttpContext httpContext,
        ArrayBufferWriter<byte> sseBuffer,
        string completionId,
        long created,
        string echoModel,
        string systemFingerprint,
        string? rawMessage,
        CancellationToken ct)
    {

        string message = SanitizeStreamErrorMessage(rawMessage);

        string errorCode = ResolveStreamErrorCode(rawMessage);

        OpenAiChatStreamErrorChunk chunk = new(
            Id: completionId,
            ObjectKind: "chat.completion.chunk",
            Created: created,
            Model: echoModel,
            Choices:
            [
                new OpenAiChatStreamChoice(
                    Index: 0,
                    Delta: new OpenAiDelta(),
                    FinishReason: "error",
                    Logprobs: null),
            ],
            Error: new OpenAiErrorDetail(
                Message: message,
                Type: "api_error",
                Param: null,
                Code: errorCode),
            SystemFingerprint: systemFingerprint);

        await WriteSseJsonAsync(httpContext, sseBuffer, chunk, ArcanumJsonContext.Default.OpenAiChatStreamErrorChunk, ct).ConfigureAwait(false);

        await httpContext.Response.Body.WriteAsync(SseDone, ct).ConfigureAwait(false);

        await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);

    }

    private static readonly string[] AllowedStreamErrorMessages =
    [
        "Inference failed. See server logs for details.",
        "Tool invocation limit reached.",
        "Inference timed out.",
        "The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel.",
        "Prompt is required.",
        "Attached file validation failed.",
    ];

    private static string SanitizeStreamErrorMessage(string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return "Inference failed. See server logs for details.";
        }

        foreach (string allowed in AllowedStreamErrorMessages)
        {
            if (string.Equals(rawMessage, allowed, StringComparison.Ordinal))
            {
                return allowed;
            }
        }

        return "Inference failed. See server logs for details.";
    }

    private static string ResolveStreamErrorCode(string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return "inference_failed";
        }

        if (string.Equals(rawMessage, "Tool invocation limit reached.", StringComparison.Ordinal))
        {
            return "server_error";
        }

        if (string.Equals(rawMessage, "Inference timed out.", StringComparison.Ordinal))
        {
            return "server_error";
        }

        return "inference_failed";
    }

    private static async Task WriteSseJsonAsync<T>(
        HttpContext httpContext,
        ArrayBufferWriter<byte> buffer,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {

        buffer.Clear();

        await httpContext.Response.Body.WriteAsync(SseDataPrefix, cancellationToken).ConfigureAwait(false);

        await using (Utf8JsonWriter jsonWriter = new(buffer, new JsonWriterOptions { Indented = false }))
        {

            JsonSerializer.Serialize(jsonWriter, value, typeInfo);

        }

        await httpContext.Response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

        await httpContext.Response.Body.WriteAsync(SseLineBreak, cancellationToken).ConfigureAwait(false);

        await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

    }

    private static string ResolveFinishReason(string? hubFinishReason)
    {
        if (!string.IsNullOrWhiteSpace(hubFinishReason))
        {
            return hubFinishReason!;
        }

        return "stop";
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

        string version = RetroDownfall.Arcanum.Core.ArcanumBuildInfo.InformationalVersion;

        int plus = version.IndexOf('+');

        if (plus >= 0)
        {

            version = version[..plus];

        }

        return "arcanum-" + version.Trim();

    }

    private static IResult JsonError(string message, string type, string? code, string? param, int statusCode)
    {
        OpenAiErrorResponse response = new(new OpenAiErrorDetail(message, type, param, code));

        return Results.Json(response, ArcanumJsonContext.Default.OpenAiErrorResponse, statusCode: statusCode);
    }

    private static string MapPublicOpenAiErrorCode(string internalCode) =>
        internalCode switch
        {
            ErrorCodes.Hub.Model => "model_not_found",
            ErrorCodes.Validation.InvalidPrompt => "missing_required_parameter",
            ErrorCodes.Validation.AttachedFiles => "invalid_value",
            ErrorCodes.Hub.ToolLoop => "server_error",
            ErrorCodes.Hub.Timeout => "server_error",
            ErrorCodes.Hub.Error => "inference_failed",
            ErrorCodes.Scrying.VisionNotSupported => "vision_not_supported",
            ErrorCodes.Scrying.FeatureDisabled => "feature_disabled",
            ErrorCodes.Scrying.TooManyImages
                or ErrorCodes.Scrying.UnsupportedMimeType
                or ErrorCodes.Scrying.ImageTooLarge => MapScryingOpenAiErrorCode(internalCode),
            _ => "inference_failed",
        };

    private static string ResolvePublicInferenceFailureMessage(string internalCode) =>
        internalCode switch
        {
            ErrorCodes.Hub.Model =>
                "The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel.",
            ErrorCodes.Validation.InvalidPrompt => "Prompt is required.",
            ErrorCodes.Validation.AttachedFiles => "Attached file validation failed.",
            ErrorCodes.Hub.ToolLoop => "Tool invocation limit reached.",
            ErrorCodes.Hub.Timeout => "Inference timed out.",
            _ => "Inference failed. See server logs for details.",
        };

    private static string MapScryingOpenAiErrorCode(string internalCode) =>
        internalCode switch
        {
            ErrorCodes.Scrying.TooManyImages => "too_many_images",
            ErrorCodes.Scrying.UnsupportedMimeType => "unsupported_mime_type",
            ErrorCodes.Scrying.ImageTooLarge => "image_too_large",
            ErrorCodes.Scrying.FeatureDisabled => "feature_disabled",
            _ => "invalid_value",
        };

    private static bool IsSupportedContentPartType(string? type) =>
        string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase);

    private static bool IsDefaultToolChoice(JsonElement toolChoice)
    {

        if (toolChoice.ValueKind == JsonValueKind.String)
        {

            ReadOnlySpan<char> text = toolChoice.GetString() ?? ReadOnlySpan<char>.Empty;

            return text.Equals("auto", StringComparison.Ordinal)
                || text.Equals("none", StringComparison.Ordinal)
                || text.Equals("required", StringComparison.Ordinal);

        }

        return false;

    }

    private static int ResolveOpenAiInferenceFailureStatusCode(string internalCode) =>
        ArcanumErrorMapper.ResolveStatusCode(internalCode);

    internal static IResult CreateUnhandledInferenceErrorResult() =>
        JsonError(
            "Inference failed. See server logs for details.",
            "api_error",
            code: "inference_failed",
            param: null,
            statusCode: StatusCodes.Status500InternalServerError);

    internal static IResult CreateInvalidJsonErrorResult() =>
        JsonError(
            "Request body could not be parsed as a chat completion request.",
            "invalid_request_error",
            code: "invalid_json",
            param: null,
            statusCode: StatusCodes.Status400BadRequest);

    internal static string ResolveFinishReasonForTests(string? hubFinishReason) =>
        ResolveFinishReason(hubFinishReason);

    internal static string MapPublicOpenAiErrorCodeForTests(string internalCode) =>
        MapPublicOpenAiErrorCode(internalCode);

    internal static int ResolveOpenAiInferenceFailureStatusCodeForTests(string internalCode) =>
        ResolveOpenAiInferenceFailureStatusCode(internalCode);

}
