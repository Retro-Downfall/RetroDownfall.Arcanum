using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Configuration;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Configuration;

namespace RetroDownfall.Arcanum.Api;

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible HTTP streaming endpoints; covered via OpenAiV1EndpointTests integration smoke.
internal static partial class OpenAiV1Endpoints
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
            .AllowCovenantContext()
            .WithLargeRequestBody()
            .AddEndpointFilter(IdempotencyEndpointFilters.ForRawBody);
    }

    internal static void MapOpenAiV1Models(this RouteGroupBuilder v1)
    {
        _ = v1.MapGet("/models", HandleListModels).WithName("GetOpenAiModels");
    }

    // Enrichment fields are additive: Arcanum runs its own server-side tool layer regardless of the
    // resolved model/provider, so every model reports the same tool/streaming capability.
    private const bool AllModelsSupportTools = true;

    private const bool AllModelsSupportStreaming = true;

    private static IResult HandleListModels(
        ConfigurationWriter writer,
        IOptionsSnapshot<ArcanumSettings> settings)
    {
        // Non-billable (ADR 0002 / NonBillableSurfaces.GetModels): config-only, no provider call.
        List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(
            ConfigurationEndpoints.ResolveCurrentSettings(writer, settings));
        Dictionary<string, PromptCachingProfile?> sharedPromptCaching = models
            .GroupBy(static model => model.Model, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group =>
                {
                    PromptCachingProfile? first = group.First().PromptCaching;

                    return group.All(model => Equals(model.PromptCaching, first))
                        ? first
                        : null;
                },
                StringComparer.OrdinalIgnoreCase);

        List<OpenAiModel> data = [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (ModelInfoDto model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Model) || !seen.Add(model.Model))
            {
                continue;
            }

            string ownedBy = string.IsNullOrWhiteSpace(model.ProviderName) ? "system" : model.ProviderName;

            data.Add(new OpenAiModel(
                model.Model,
                "model",
                ProcessStartUnix,
                ownedBy,
                model.ContextWindowLimit,
                model.SupportsVision,
                model.ProviderName,
                ModelInfoBuilder.ToSnakeCaseProviderType(model.ProviderType),
                AllModelsSupportTools,
                AllModelsSupportStreaming,
                model.WireDialect,
                model.MaxBudgetTokens,
                sharedPromptCaching.GetValueOrDefault(model.Model)));
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

        // ReadFromJsonAsync throws InvalidOperationException — not JsonException — for a missing or
        // non-JSON Content-Type, and Kestrel throws BadHttpRequestException once WithLargeRequestBody's
        // 16 MiB ceiling is exceeded. Uncaught, both escape to ArcanumExceptionHandler and turn a routine
        // client mistake into a 500 api_error/inference_failed with an Error-level stack trace.
        if (!httpContext.Request.HasJsonContentType())
        {
            return CreateUnsupportedMediaTypeErrorResult();
        }

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
        catch (InvalidOperationException)
        {
            return CreateUnsupportedMediaTypeErrorResult();
        }
        catch (BadHttpRequestException exception)
        {
            return CreateRequestBodyReadErrorResult(exception.StatusCode);
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

        ChatCompletionValidationFailure? validationFailure = TryValidateChatCompletionRequest(
            body,
            settings.Value,
            out PingRequest? ping,
            forceDisableAllTools: false);

        if (validationFailure is not null || ping is null)
        {
            ChatCompletionValidationFailure failure = validationFailure
                ?? new ChatCompletionValidationFailure(
                    "Request validation failed.",
                    "invalid_request_error",
                    "invalid_value",
                    null,
                    StatusCodes.Status400BadRequest);

            return JsonError(
                failure.Message,
                failure.Type,
                code: failure.Code,
                param: failure.Param,
                statusCode: failure.StatusCode);
        }

        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        string completionId = "chatcmpl-" + Guid.NewGuid().ToString("N");

        string echoModel = body.Model!.Trim();

        string systemFingerprint = ResolveSystemFingerprint();

        InferenceAuditContext auditContext = new()
        {
            RequestType = "v1-completion",
            ClientIp = httpContext.Connection.RemoteIpAddress?.ToString(),
        };

        if (!body.Stream)
        {
            return await HandleBufferedAsync(
                httpContext,
                intelligence,
                ping,
                completionId,
                created,
                echoModel,
                systemFingerprint,
                cancellationToken,
                auditContext)
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
            cancellationToken,
            auditContext)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> HandleBufferedAsync(
        HttpContext httpContext,
        IArcanumIntelligenceProvider intelligence,
        PingRequest ping,
        string completionId,
        long created,
        string echoModel,
        string systemFingerprint,
        CancellationToken cancellationToken,
        InferenceAuditContext? auditContext = null)
    {
        Result<PromptTurnResult> result = await intelligence.ExecutePromptAsync(
            ping,
            ArcanumInvocationContexts.ForStatelessTurn(httpContext, ping),
            cancellationToken,
            auditContext).ConfigureAwait(false);

        if (result.IsFailure)
        {
            if (OpenAiStreamErrorMapper.IsReasoningValidationCode(result.Error.Code))
            {
                OpenAiErrorDetail reasoningError = OpenAiStreamErrorMapper.Map(result.Error);

                return JsonError(
                    reasoningError.Message,
                    reasoningError.Type,
                    reasoningError.Code,
                    reasoningError.Param,
                    ResolveOpenAiInferenceFailureStatusCode(result.Error.Code));
            }

            (string errorType, string errorCode) = MapPublicOpenAiError(result.Error.Code);

            return JsonError(
                ResolvePublicInferenceFailureMessage(result.Error.Code),
                errorType,
                code: errorCode,
                param: null,
                statusCode: ResolveOpenAiInferenceFailureStatusCode(result.Error.Code));
        }

        PromptTurnResult turn = result.Value;

        string finishReason = ResolveFinishReason(turn.FinishReason);

        (string? reasoningContent, string? reasoningSummary) =
            MapBufferedReasoning(turn.Reasoning);

        OpenAiChatAssistantMessage message = new(
            Role: "assistant",
            Content: turn.Text,
            ToolCalls: MapBufferedToolCalls(turn.ToolCalls, turn.PreserveProviderToolCallIds),
            Refusal: null,
            ReasoningContent: reasoningContent,
            ReasoningSummary: reasoningSummary);

        string responseSystemFingerprint = turn.Warnings.Count > 0
            ? $"{systemFingerprint}:arcanum:structured-output-warning"
            : systemFingerprint;

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
            SystemFingerprint: responseSystemFingerprint,
            ServiceTier: null);

        if (turn.Warnings.Count > 0
            && SanitizeWarningHeaderValue(turn.Warnings) is { Length: > 0 } warningHeader)
        {
            httpContext.Response.Headers.Append(
                "X-Arcanum-Structured-Output-Warning",
                warningHeader);
        }

        return Results.Json(response, ArcanumJsonContext.Default.OpenAiChatResponse);
    }

    /// <summary>
    /// Reduces structured-output validation warnings to a value that is always legal in an HTTP
    /// response header. Warning text quotes model- and schema-supplied JSON property names verbatim
    /// (see <c>JsonSchemaHelper</c>'s "additional property '…' is not allowed"), so an accented or
    /// emoji key — ordinary model behaviour — would otherwise reach Kestrel's ASCII header
    /// validation and throw, turning an already-billed 200 completion into a 500. Every character
    /// outside printable US-ASCII (including CR/LF, the header-injection sink) collapses to '?', and
    /// the value is capped so a pathological schema cannot inflate the response headers. The
    /// <c>system_fingerprint</c> suffix remains the authoritative warning signal.
    /// </summary>
    private static string SanitizeWarningHeaderValue(IReadOnlyList<string> warnings)
    {
        const int maxLength = 512;

        const string truncationMarker = " [truncated]";

        string joined = string.Join(" | ", warnings);

        int budget = Math.Min(joined.Length, maxLength);

        StringBuilder sanitized = new(budget + truncationMarker.Length);

        for (int i = 0; i < budget; i++)
        {
            char candidate = joined[i];

            _ = sanitized.Append(candidate is >= ' ' and <= '~' ? candidate : '?');
        }

        if (joined.Length > budget)
        {
            _ = sanitized.Append(truncationMarker);
        }

        return sanitized.ToString();
    }

    private static (string? Content, string? Summary) MapBufferedReasoning(
        IReadOnlyList<ReasoningContentSegment> reasoning)
    {
        string summary = string.Concat(
            reasoning
                .Where(static segment => segment.Output == ReasoningOutputMode.Summary)
                .Select(static segment => segment.Text));
        string content = string.Concat(
            reasoning
                .Where(static segment => segment.Output != ReasoningOutputMode.Summary)
                .Select(static segment => segment.Text));

        return (
            string.IsNullOrEmpty(content) ? null : content,
            string.IsNullOrEmpty(summary) ? null : summary);
    }

    /// <summary>
    /// Maps the assistant-issued tool calls Arcanum already executed server-side during this turn
    /// (§8.8.1) into OpenAI-shaped <c>message.tool_calls</c> for observability/replay. Returns
    /// <see langword="null"/> (rather than an empty array) when there were none, matching OpenAI's
    /// own wire shape for a turn that never called a tool.
    /// </summary>
    private static OpenAiToolCall[]? MapBufferedToolCalls(List<PromptToolCall>? toolCalls, bool preserveProviderIds)
    {

        if (toolCalls is not { Count: > 0 })
        {

            return null;

        }

        OpenAiToolCall[] mapped = new OpenAiToolCall[toolCalls.Count];

        for (int i = 0; i < toolCalls.Count; i++)
        {

            PromptToolCall call = toolCalls[i];

            mapped[i] = new OpenAiToolCall(
                Id: preserveProviderIds ? call.CallId : GenerateOpenAiToolCallId(),
                Type: "function",
                Function: new OpenAiFunctionCall(call.Name, call.ArgumentsJson));

        }

        return mapped;

    }

    /// <summary>
    /// Generates an OpenAI-shaped tool call id (<c>call_</c> + 24 hex characters, mirroring the
    /// ~29-character length of real OpenAI ids). Arcanum's internal <c>PromptToolCall.CallId</c> /
    /// <c>IntelligenceToolCallEvent.CallId</c> are not guaranteed to be in this shape across every
    /// provider (some fall back to the tool name — see <c>ToolExecutionPipeline.ResolveCallId</c>),
    /// so a fresh id is minted for the wire response rather than exposing the internal one. This is
    /// safe for client replay (§8.8, "client-supplied tool replay in message history") because the
    /// client only needs the id to correlate its own echoed <c>tool_calls[].id</c> with a subsequent
    /// <c>role: "tool"</c> message's <c>tool_call_id</c> in the *next* stateless request — Arcanum
    /// does not need to recognize the id as one it minted internally.
    /// </summary>
    private static string GenerateOpenAiToolCallId() =>
        "call_" + Guid.NewGuid().ToString("N")[..24];

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
        CancellationToken cancellationToken,
        InferenceAuditContext? auditContext = null)
    {
        httpContext.Response.ContentType = "text/event-stream; charset=utf-8";

        httpContext.Response.Headers.CacheControl = "no-cache";

        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        DisconnectPolicy disconnectPolicy = httpContext.RequestServices
            .GetRequiredService<IOptionsSnapshot<ArcanumSettings>>()
            .Value.ResolveIntelligence().DisconnectPolicy;

        bool continueThenReplay = TurnContextGuards.ResolveContinueThenReplay(httpContext, disconnectPolicy);
        CancellationToken ownershipLost = TurnIdempotencyAmbient.OwnershipLostToken;

        using CancellationTokenSource streamCts = continueThenReplay
            ? CancellationTokenSource.CreateLinkedTokenSource(ownershipLost)
            : CancellationTokenSource.CreateLinkedTokenSource(
                httpContext.RequestAborted,
                cancellationToken,
                ownershipLost);

        CancellationToken ct = streamCts.Token;

        ArrayBufferWriter<byte> sseBuffer = new(512);

        ChatCompletionUsage? sseUsage = null;

        string? sseFinishReason = null;

        bool aborted = false;

        bool streamErrored = false;

        bool disconnected = false;

        bool clientGone = false;

        // Monotonically increasing across the whole response (not reset per hub tool round, unlike
        // IntelligenceToolCallEvent.Index — see WriteToolCallChunksAsync remarks) so a multi-round
        // Arcanum tool loop never emits two distinct calls under the same `index`, which would be
        // ambiguous to an OpenAI SDK's index-keyed delta accumulator.
        int nextToolCallDeltaIndex = 0;

        try
        {
            try
            {
                await WriteRoleChunkAsync(httpContext, sseBuffer, completionId, created, echoModel, systemFingerprint, ct).ConfigureAwait(false);
            }
            catch (Exception roleEx) when (ClientDisconnect.IsClientDisconnect(roleEx, httpContext))
            {
                // Write-side broken pipe before the pump starts. Classified here rather than in the
                // whole-pipeline catch below, which now only treats a genuinely gone client as a
                // disconnect and would otherwise report this as a provider fault.
                clientGone = true;
                disconnected = true;

                if (!continueThenReplay)
                {
                    streamCts.Cancel();
                    aborted = true;

                    return Results.Empty;
                }
            }

            // Pump via the intelligence provider facade (already TurnExecutionCoordinator →
            // TurnEngine for production). Keep-alives and SSE serialization stay in this writer
            // (ADR 0004 transport/replay boundary). Tests inject a fake IArcanumIntelligenceProvider;
            // resolving ITurnExecutionFacade here would bypass that fake.
            IAsyncEnumerator<IntelligenceEvent> enumerator =
                intelligence.StreamPromptAsync(
                    ping,
                    ArcanumInvocationContexts.ForStatelessTurn(httpContext, ping),
                    ct,
                    auditContext).GetAsyncEnumerator(ct);

            Task<bool> move = enumerator.MoveNextAsync().AsTask();

            // The pump owns the enumerator explicitly: a client disconnect can abandon it with a
            // MoveNextAsync still in flight, and disposing a compiler-generated async iterator in
            // that state throws NotSupportedException — which would surface an ordinary disconnect
            // as an unhandled streaming error instead of a clean abort (see QuiesceAndDisposeAsync).
            try
            {
                while (true)
                {
                    using CancellationTokenSource delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    Task keepAliveDelay = Task.Delay(StreamKeepAliveInterval, delayCts.Token);

                    Task completed = await Task.WhenAny(move, keepAliveDelay).ConfigureAwait(false);

                    if (completed == keepAliveDelay)
                    {
                        if (!clientGone)
                        {
                            try
                            {
                                await SseStreamWriter.WriteKeepAliveAsync(httpContext, ct).ConfigureAwait(false);
                            }
                            catch (Exception keepAliveEx) when (ClientDisconnect.IsClientDisconnect(keepAliveEx, httpContext))
                            {
                                clientGone = true;
                                disconnected = true;

                                if (!continueThenReplay)
                                {
                                    streamCts.Cancel();
                                    aborted = true;
                                    break;
                                }
                            }
                        }

                        continue;
                    }

                    delayCts.Cancel();

                    if (!await move.ConfigureAwait(false))
                    {
                        break;
                    }

                    IntelligenceEvent ev = enumerator.Current;

                    if (clientGone && continueThenReplay)
                    {
                        if (ev.Type == IntelligenceEventType.Result)
                        {
                            sseUsage = ev.Usage;
                            sseFinishReason = ev.FinishReason;
                        }

                        move = enumerator.MoveNextAsync().AsTask();
                        continue;
                    }

                    try
                    {
                        switch (ev.Type)
                        {
                            case IntelligenceEventType.Token when !string.IsNullOrEmpty(ev.Data):
                                await WriteContentChunkAsync(httpContext, sseBuffer, completionId, created, echoModel, systemFingerprint, ev.Data, ct).ConfigureAwait(false);
                                break;

                            case IntelligenceEventType.Reasoning when ev.Reasoning is { Text.Length: > 0 } reasoning:
                                await WriteReasoningChunkAsync(
                                    httpContext,
                                    sseBuffer,
                                    completionId,
                                    created,
                                    echoModel,
                                    systemFingerprint,
                                    reasoning,
                                    ct).ConfigureAwait(false);
                                break;

                            case IntelligenceEventType.Reasoning:
                                break;

                            case IntelligenceEventType.ToolCall when ev.ToolCall is { } toolCallPayload:
                                await WriteToolCallChunksAsync(
                                    httpContext,
                                    sseBuffer,
                                    completionId,
                                    created,
                                    echoModel,
                                    systemFingerprint,
                                    toolCallPayload,
                                    nextToolCallDeltaIndex,
                                    ct).ConfigureAwait(false);

                                nextToolCallDeltaIndex++;

                                break;

                            case IntelligenceEventType.ToolCall:
                                break;

                            case IntelligenceEventType.Result:
                                sseUsage = ev.Usage;

                                sseFinishReason = ev.FinishReason;

                                if (ev.Warnings.Count > 0)
                                {
                                    systemFingerprint = string.IsNullOrEmpty(systemFingerprint)
                                        ? "arcanum:structured-output-warning"
                                        : systemFingerprint + ":arcanum:structured-output-warning";
                                }

                                break;

                            case IntelligenceEventType.Error:
                                await WriteStreamErrorAsync(
                                    httpContext,
                                    sseBuffer,
                                    completionId,
                                    created,
                                    echoModel,
                                    systemFingerprint,
                                    new Error(
                                        ev.Data ?? ErrorCodes.Hub.Error,
                                        ev.Message),
                                    ct).ConfigureAwait(false);
                                streamErrored = true;
                                TurnContextGuards.MarkIdempotencyTerminal(httpContext);
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
                    }
                    catch (Exception writeEx) when (ClientDisconnect.IsClientDisconnect(writeEx, httpContext))
                    {
                        clientGone = true;
                        disconnected = true;

                        if (!continueThenReplay)
                        {
                            streamCts.Cancel();
                            aborted = true;
                            break;
                        }
                    }

                    move = enumerator.MoveNextAsync().AsTask();
                }
            }
            finally
            {
                await SseStreamWriter
                    .QuiesceAndDisposeAsync(enumerator, move, streamCts)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            ct.IsCancellationRequested
            || cancellationToken.IsCancellationRequested
            || httpContext.RequestAborted.IsCancellationRequested
            || ownershipLost.IsCancellationRequested)
        {
            aborted = true;

            if (ownershipLost.IsCancellationRequested)
            {
                return Results.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Direction matters here. Every write on this route is wrapped by its own disconnect catch
        // above, so an IOException/HttpIOException surfacing at this level came out of the
        // provider's MoveNextAsync. While the client socket is still healthy that is a PROVIDER
        // fault owed the terminal error frame the general catch below writes, not a silent
        // truncation — so only classify it as a disconnect when the client really is gone.
        catch (Exception ex) when (
            ClientDisconnect.IsClientDisconnect(ex, httpContext)
            && (clientGone || httpContext.RequestAborted.IsCancellationRequested))
        {
            if (!continueThenReplay)
            {
                streamCts.Cancel();
            }

            aborted = true;

            disconnected = true;
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Unhandled exception while streaming OpenAI chat completion {CompletionId}; exception type {ExceptionType}.",
                completionId,
                ex.GetType().FullName);

            try
            {
                await WriteStreamErrorAsync(
                    httpContext,
                    sseBuffer,
                    completionId,
                    created,
                    echoModel,
                    systemFingerprint,
                    error: null,
                    httpContext.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (Exception writeEx)
            {
                logger.LogWarning(
                    "Failed to write terminal error to OpenAI SSE stream {CompletionId}; exception type {ExceptionType}.",
                    completionId,
                    writeEx.GetType().FullName);
            }

            return Results.Empty;
        }

        if (streamErrored || (disconnected && !continueThenReplay))
        {
            return Results.Empty;
        }

        try
        {
            if (!clientGone)
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

            TurnContextGuards.MarkIdempotencyTerminal(httpContext);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Failed to write terminal frames to OpenAI SSE stream {CompletionId}; exception type {ExceptionType}.",
                completionId,
                ex.GetType().FullName);
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

    private static async Task WriteReasoningChunkAsync(
        HttpContext httpContext,
        ArrayBufferWriter<byte> sseBuffer,
        string completionId,
        long created,
        string echoModel,
        string systemFingerprint,
        ReasoningContentSegment reasoning,
        CancellationToken ct)
    {
        OpenAiDelta delta = reasoning.Output == ReasoningOutputMode.Summary
            ? new OpenAiDelta(ReasoningSummary: reasoning.Text)
            : new OpenAiDelta(ReasoningContent: reasoning.Text);

        OpenAiChatChunk chunk = new(
            Id: completionId,
            ObjectKind: "chat.completion.chunk",
            Created: created,
            Model: echoModel,
            Choices:
            [
                new OpenAiChatStreamChoice(
                    Index: 0,
                    Delta: delta,
                    FinishReason: null,
                    Logprobs: null),
            ],
            Usage: null,
            SystemFingerprint: systemFingerprint);

        await WriteSseJsonAsync(
            httpContext,
            sseBuffer,
            chunk,
            ArcanumJsonContext.Default.OpenAiChatChunk,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The full argument JSON string is available all-at-once from
    /// <see cref="IntelligenceToolCallEvent.ArgumentsJson"/> (Arcanum executes each tool call to
    /// completion — sequentially, one at a time — before advancing to the next, unlike a model
    /// literally streaming argument tokens as it generates them). To still match OpenAI's observed
    /// wire behavior — an id/name delta followed by several small <c>function.arguments</c>
    /// fragments the client concatenates — the string is artificially re-chunked into
    /// <see cref="ToolCallArgumentChunkChars"/>-sized pieces here.
    ///
    /// Because tool calls within one Arcanum turn can span multiple server-side rounds (an
    /// OpenAI-native single-shot streaming response never does this), <paramref name="deltaIndex"/>
    /// is a caller-maintained counter that increases once per distinct call for the whole HTTP
    /// response — not <see cref="IntelligenceToolCallEvent.Index"/>, which the hub resets every
    /// round and would otherwise let two unrelated calls collide on the same <c>index</c>, which is
    /// ambiguous for an index-keyed OpenAI SDK delta accumulator.
    ///
    /// Parallel tool calls in one round are inherently emitted as one complete chunk-burst per call
    /// (not literally interleaved token-by-token across calls) because
    /// <see cref="WizardIntelligenceProvider"/> executes — and awaits the result of — each call
    /// before yielding the next <see cref="IntelligenceEventType.ToolCall"/> event; each call still
    /// gets its own correct, stable <paramref name="deltaIndex"/>, which is all an accumulating
    /// client keys on.
    /// </summary>
    private const int ToolCallArgumentChunkChars = 40;

    private static async Task WriteToolCallChunksAsync(
        HttpContext httpContext,
        ArrayBufferWriter<byte> sseBuffer,
        string completionId,
        long created,
        string echoModel,
        string systemFingerprint,
        IntelligenceToolCallEvent toolCall,
        int deltaIndex,
        CancellationToken ct)
    {

        string id = toolCall.PreserveProviderCallId
            ? toolCall.CallId
            : GenerateOpenAiToolCallId();

        string arguments = toolCall.ArgumentsJson ?? string.Empty;

        int firstChunkLength = WholeCodePointChunkLength(arguments, 0, Math.Min(ToolCallArgumentChunkChars, arguments.Length));

        OpenAiStreamToolCall firstDelta = new(
            Index: deltaIndex,
            Id: id,
            Type: "function",
            Function: new OpenAiFunctionCall(toolCall.Name, arguments[..firstChunkLength]));

        await WriteToolCallDeltaChunkAsync(httpContext, sseBuffer, completionId, created, echoModel, systemFingerprint, firstDelta, ct).ConfigureAwait(false);

        for (int offset = firstChunkLength; offset < arguments.Length;)
        {

            int length = WholeCodePointChunkLength(arguments, offset, Math.Min(ToolCallArgumentChunkChars, arguments.Length - offset));

            OpenAiStreamToolCall nextDelta = new(
                Index: deltaIndex,
                Function: new OpenAiFunctionCall(Name: null, Arguments: arguments.Substring(offset, length)));

            await WriteToolCallDeltaChunkAsync(httpContext, sseBuffer, completionId, created, echoModel, systemFingerprint, nextDelta, ct).ConfigureAwait(false);

            offset += length;

        }

    }

    /// <summary>
    /// Shortens a candidate chunk by one UTF-16 code unit when it would otherwise end on a high
    /// surrogate, deferring the whole pair to the next delta.
    /// </summary>
    /// <remarks>
    /// <see cref="ToolCallArgumentChunkChars"/> is a code-unit count, not a code-point count, so a
    /// raw slice can cut an astral character (emoji, rare CJK, older mathematical alphanumerics) in
    /// half. <see cref="System.Text.Json.Utf8JsonWriter"/> substitutes U+FFFD for the resulting lone
    /// surrogate rather than throwing, so the corruption is silent: an accumulating OpenAI SDK
    /// concatenates the deltas and gets arguments that no longer round-trip. Shrinking (rather than
    /// growing) keeps every delta within the advertised ceiling.
    /// </remarks>
    private static int WholeCodePointChunkLength(string value, int offset, int length)
    {

        if (length <= 1 || offset + length >= value.Length)
        {

            return length;

        }

        return char.IsHighSurrogate(value[offset + length - 1]) ? length - 1 : length;

    }

    private static async Task WriteToolCallDeltaChunkAsync(
        HttpContext httpContext,
        ArrayBufferWriter<byte> sseBuffer,
        string completionId,
        long created,
        string echoModel,
        string systemFingerprint,
        OpenAiStreamToolCall toolCallDelta,
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
                    Delta: new OpenAiDelta(ToolCalls: [toolCallDelta]),
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
        Error? error,
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
                    FinishReason: "error",
                    Logprobs: null),
            ],
            SystemFingerprint: systemFingerprint,
            Error: OpenAiStreamErrorMapper.Map(error));

        await WriteSseJsonAsync(httpContext, sseBuffer, chunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct).ConfigureAwait(false);

        await httpContext.Response.Body.WriteAsync(SseDone, ct).ConfigureAwait(false);

        await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);

    }

    /// <summary>
    /// Writes one complete SSE frame — <c>data: </c>, the serialized payload, and the terminating
    /// blank line — with a single <c>WriteAsync</c>.
    /// </summary>
    /// <remarks>
    /// Assembled in the buffer first, the way <c>EventEndpoints.WriteSseJsonAsync</c> does it. Issuing
    /// the prefix as its own write commits it before the payload exists, so a failure during
    /// serialization or the payload write leaves an orphan <c>data: </c> on the socket and the next
    /// frame lands behind it as a malformed <c>data: data: {...}</c>. Buffering also cuts the frame
    /// from three writes plus a flush to one write plus a flush.
    /// </remarks>
    internal static async Task WriteSseJsonAsync<T>(
        HttpContext httpContext,
        ArrayBufferWriter<byte> buffer,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {

        buffer.Clear();

        buffer.Write(SseDataPrefix);

        await using (Utf8JsonWriter jsonWriter = new(buffer, new JsonWriterOptions { Indented = false }))
        {

            JsonSerializer.Serialize(jsonWriter, value, typeInfo);

        }

        buffer.Write(SseLineBreak);

        await httpContext.Response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

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

    private static string ResolveSystemFingerprint() => DefaultSystemFingerprint;

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

    private static (string Type, string Code) MapPublicOpenAiError(string internalCode) =>
        internalCode switch
        {
            ErrorCodes.Hub.Model => ("api_error", "model_not_found"),
            ErrorCodes.Validation.InvalidPrompt => ("api_error", "missing_required_parameter"),
            ErrorCodes.Validation.AttachedFiles => ("api_error", "invalid_value"),
            ErrorCodes.Hub.Error => ("api_error", "inference_failed"),
            ErrorCodes.Scrying.VisionNotSupported => ("api_error", "vision_not_supported"),
            ErrorCodes.Scrying.FeatureDisabled => ("api_error", "feature_disabled"),
            ErrorCodes.Scrying.TooManyImages
                or ErrorCodes.Scrying.UnsupportedMimeType
                or ErrorCodes.Scrying.InvalidImageData
                or ErrorCodes.Scrying.ImageTooLarge => ("api_error", MapScryingOpenAiErrorCode(internalCode)),
            ErrorCodes.StructuredOutput.SchemaInvalid => ("invalid_request_error", "invalid_schema"),
            ErrorCodes.StructuredOutput.ValidationFailed => ("invalid_request_error", "validation_failed"),
            _ => ("api_error", "inference_failed"),
        };

    private static string MapPublicOpenAiErrorCode(string internalCode) =>
        MapPublicOpenAiError(internalCode).Code;

    private static string ResolvePublicInferenceFailureMessage(string internalCode) =>
        internalCode switch
        {
            ErrorCodes.Hub.Model =>
                PublicInferenceErrorMessages.ModelNotConfigured,
            ErrorCodes.Validation.InvalidPrompt => "Prompt is required.",
            ErrorCodes.Validation.AttachedFiles => "Attached file validation failed.",
            ErrorCodes.StructuredOutput.SchemaInvalid => "The supplied JSON schema for structured output is invalid.",
            ErrorCodes.StructuredOutput.ValidationFailed => "The model response did not match the requested JSON schema.",
            _ => PublicInferenceErrorMessages.OpenAiGenericFailure,
        };

    private static string MapScryingOpenAiErrorCode(string internalCode) =>
        internalCode switch
        {
            ErrorCodes.Scrying.TooManyImages => "too_many_images",
            ErrorCodes.Scrying.UnsupportedMimeType => "unsupported_mime_type",
            ErrorCodes.Scrying.InvalidImageData => "invalid_image_data",
            ErrorCodes.Scrying.ImageTooLarge => "image_too_large",
            ErrorCodes.Scrying.FeatureDisabled => "feature_disabled",
            _ => "invalid_value",
        };

    private static bool IsSupportedContentPartType(string? type) =>
        string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidClientToolChoice(JsonElement toolChoice)
    {

        if (toolChoice.ValueKind == JsonValueKind.String)
        {

            ReadOnlySpan<char> text = toolChoice.GetString() ?? ReadOnlySpan<char>.Empty;

            return text.Equals("auto", StringComparison.Ordinal)
                || text.Equals("none", StringComparison.Ordinal)
                || text.Equals("required", StringComparison.Ordinal);

        }

        if (toolChoice.ValueKind == JsonValueKind.Object
            && toolChoice.TryGetProperty("type", out JsonElement typeElement)
            && string.Equals(typeElement.GetString(), "function", StringComparison.Ordinal)
            && toolChoice.TryGetProperty("function", out JsonElement functionElement)
            && functionElement.TryGetProperty("name", out JsonElement nameElement)
            && nameElement.ValueKind == JsonValueKind.String)
        {

            return !string.IsNullOrWhiteSpace(nameElement.GetString());

        }

        return false;

    }

    private static Result ValidateClientTools(OpenAiToolDefinition[] tools, int configuredMaxClientTools, int schemaMaxDepth)
    {

        int maxClientTools = ArcanumSettingClamps.ClientToolForwardingMaxClientTools(configuredMaxClientTools);

        if (tools.Length > maxClientTools)
        {

            return Result.Failure(new Error(
                ErrorCodes.ClientTools.TooMany,
                $"Client-supplied tools exceed the maximum of {maxClientTools}."));

        }

        HashSet<string> seenNames = new(StringComparer.Ordinal);

        for (int i = 0; i < tools.Length; i++)
        {

            OpenAiToolDefinition tool = tools[i];

            if (!string.Equals(tool.Type, "function", StringComparison.Ordinal))
            {

                return Result.Failure(new Error(
                    ErrorCodes.ClientTools.InvalidSchema,
                    $"tools[{i}].type must be 'function'."));

            }

            if (string.IsNullOrWhiteSpace(tool.Function?.Name))
            {

                return Result.Failure(new Error(
                    ErrorCodes.ClientTools.InvalidSchema,
                    $"tools[{i}].function.name is required."));

            }

            if (!seenNames.Add(tool.Function.Name))
            {

                return Result.Failure(new Error(
                    ErrorCodes.ClientTools.InvalidSchema,
                    $"Duplicate tool function name '{tool.Function.Name}'."));

            }

            JsonElement? parameters = tool.Function?.Parameters;

            if (parameters is { } parametersElement
                && parametersElement.ValueKind is not JsonValueKind.Null
                && parametersElement.ValueKind is not JsonValueKind.Undefined)
            {

                if (parametersElement.ValueKind != JsonValueKind.Object)
                {

                    return Result.Failure(new Error(
                        ErrorCodes.ClientTools.InvalidSchema,
                        $"tools[{i}].function.parameters must be a valid JSON Schema object."));

                }

                using JsonDocument parametersDocument = JsonDocument.Parse(parametersElement.GetRawText());

                Result<JsonSchemaDefinition> parseResult = JsonSchemaHelper.Parse(parametersDocument, schemaMaxDepth);

                if (parseResult.IsFailure)
                {

                    return Result.Failure(new Error(
                        ErrorCodes.ClientTools.InvalidSchema,
                        $"tools[{i}].function.parameters is not a valid JSON Schema: {parseResult.Error.Message}"));

                }

            }

        }

        return Result.Success();

    }

    private static string MapClientToolsErrorCode(string internalCode) =>
        internalCode switch
        {
            ErrorCodes.ClientTools.TooMany => "too_many_tools",
            ErrorCodes.ClientTools.InvalidSchema => "invalid_schema",
            _ => "invalid_value",
        };

    private static int ResolveOpenAiInferenceFailureStatusCode(string internalCode) =>
        ArcanumErrorMapper.ResolveStatusCode(internalCode);

    internal static IResult CreateUnhandledInferenceErrorResult() =>
        JsonError(
            PublicInferenceErrorMessages.OpenAiGenericFailure,
            "api_error",
            code: "inference_failed",
            param: null,
            statusCode: StatusCodes.Status500InternalServerError);

    private static IResult CreateUnsupportedMediaTypeErrorResult() =>
        JsonError(
            ApiRequestJson.UnsupportedMediaTypeMessage,
            "invalid_request_error",
            code: "unsupported_media_type",
            param: null,
            statusCode: StatusCodes.Status415UnsupportedMediaType);

    internal static IResult CreateRequestBodyReadErrorResult(int statusCode)
    {
        bool payloadTooLarge = statusCode == StatusCodes.Status413PayloadTooLarge;

        return JsonError(
            payloadTooLarge
                ? "Request body exceeds the maximum size accepted by this server."
                : "Request body could not be read.",
            "invalid_request_error",
            code: payloadTooLarge ? "payload_too_large" : "invalid_request",
            param: null,
            statusCode: statusCode);
    }

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
