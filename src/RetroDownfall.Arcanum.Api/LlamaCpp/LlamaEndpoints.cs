using System.Buffers;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.LlamaCpp;

[ExcludeFromCodeCoverage] // Reason: llama model pull/status HTTP endpoints backed by excluded TheReliquary/LlamaServerManager.
internal static class LlamaEndpoints
{

    private const string PublicPullFailureMessage =
        "Model download failed. See server logs for details.";

    /// <summary>
    /// Matches <c>ChatClientFactory</c>'s placeholder credential for keyless local llama-server
    /// endpoints — reused here rather than hardcoded independently so both call sites stay in sync.
    /// </summary>
    private const string KeylessOpenAiPlaceholder = "no-key";

    private const string OpenAiCompatibleHttpClientName = "OpenAiCompatibleProvider";

    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    internal static void MapLlamaEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapPost("/llama/models/pull", HandlePullModelAsync).WithName("PostLlamaModelPull");

        apiGroup.MapGet("/llama/models", HandleListModels).WithName("GetLlamaCachedModels");

        apiGroup.MapGet("/llama/servers", HandleListServers).WithName("GetLlamaServers");

        apiGroup.MapPost("/llama/servers/{cacheKey}/start", HandleStartServerAsync).WithName("PostLlamaServerStart");

        apiGroup.MapPost("/llama/servers/{cacheKey}/stop", HandleStopServerAsync).WithName("PostLlamaServerStop");

        apiGroup.MapPost("/llama/servers/stop", HandleStopAllServersAsync).WithName("PostLlamaServersStopAll");

        apiGroup.MapPost("/llama/servers/{cacheKey}/warmup", HandleWarmupServerAsync).WithName("PostLlamaServerWarmup");

    }

    private static async Task<IResult> HandlePullModelAsync(
        HttpContext httpContext,
        IReliquary modelCache,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {

        PullModelRequestDto? body;

        IResult? jsonError;

        (body, jsonError) = await ApiRequestJson.ReadAsync(
            httpContext,
            ArcanumJsonContext.Default.PullModelRequestDto,
            static ctx => ValidationError(ctx, ApiRequestJson.MalformedJsonMessage),
            cancellationToken).ConfigureAwait(false);

        if (jsonError is not null)
        {
            return jsonError;
        }

        if (body is null || string.IsNullOrWhiteSpace(body.SourceUrl))
        {
            return ValidationError(httpContext, "SourceUrl is required.");
        }

        if (!LlamaSourceUrl.TryValidate(body.SourceUrl, out string normalizedUrl))
        {
            return ValidationError(httpContext, "Source URL must be an absolute http or https URI.");
        }

        Result outbound = await OutboundUrlGuard.ValidateUntrustedUrlAsync(normalizedUrl, cancellationToken).ConfigureAwait(false);

        if (outbound.IsFailure)
        {
            return ValidationError(httpContext, outbound.Error.Message);
        }

        string cacheKey = string.IsNullOrWhiteSpace(body.CacheKey)
            ? LlamaCacheKey.Normalize(normalizedUrl)
            : LlamaCacheKey.NormalizeModelKey(body.CacheKey);

        httpContext.Response.ContentType = "application/x-ndjson; charset=utf-8";

        httpContext.Response.Headers.CacheControl = "no-cache";

        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        // Explicitly link httpContext.RequestAborted alongside the DI-bound cancellationToken (which
        // ASP.NET Core already binds to RequestAborted for Minimal API parameters) — this is a
        // long-running download, and an explicit link guards against a future signature change
        // silently decoupling the two, leaking the download task past client disconnect.
        using CancellationTokenSource pullCts = CancellationTokenSource.CreateLinkedTokenSource(
            httpContext.RequestAborted,
            cancellationToken);

        CancellationToken ct = pullCts.Token;

        ArrayBufferWriter<byte> buffer = new(256);

        Channel<LlamaPullProgress> channel = Channel.CreateBounded<LlamaPullProgress>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        Task writerTask = WriteProgressStreamAsync(httpContext, channel.Reader, buffer, ct);

        Progress<LlamaPullProgress> progress = new(frame => _ = channel.Writer.TryWrite(frame));

        try
        {
            Result<string> result = await modelCache.EnsureModelAsync(
                cacheKey,
                normalizedUrl,
                body.Sha256,
                progress,
                ct).ConfigureAwait(false);

            if (result.IsFailure)
            {
                ILogger logger = loggerFactory.CreateLogger(typeof(LlamaEndpoints));

                logger.LogWarning(
                    "Llama model pull failed for {CacheKey}: {ErrorCode} {ErrorMessage}",
                    cacheKey,
                    result.Error.Code,
                    result.Error.Message);

                await channel.Writer.WriteAsync(new LlamaPullProgress
                {
                    CacheKey = cacheKey,
                    Completed = true,
                    Error = PublicPullFailureMessage,
                }, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // client cancelled
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        try
        {
            await writerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // client cancelled
        }

        return Results.Empty;

    }

    private static async Task WriteProgressStreamAsync(
        HttpContext httpContext,
        ChannelReader<LlamaPullProgress> reader,
        ArrayBufferWriter<byte> buffer,
        CancellationToken cancellationToken)
    {

        await foreach (LlamaPullProgress frame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteProgressFrameAsync(httpContext, frame, buffer, cancellationToken).ConfigureAwait(false);
        }

    }

    private static async Task<IResult> HandleListModels(
        IReliquary modelCache,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        IReadOnlyList<CachedModelInfo> models = await modelCache.ListAsync(cancellationToken).ConfigureAwait(false);

        Result<CachedModelInfo[]> result = models.ToArray();

        ApiResponse<CachedModelInfo[]> response = ApiResponse<CachedModelInfo[]>.FromResult(result, traceId);

        return Results.Ok(response);

    }

    private static IResult HandleListServers(ILlamaServerManager manager, HttpContext httpContext)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        IReadOnlyList<LlamaServerInfo> servers = manager.ListServers();

        Result<LlamaServerInfo[]> result = servers.ToArray();

        ApiResponse<LlamaServerInfo[]> response = ApiResponse<LlamaServerInfo[]>.FromResult(result, traceId);

        return Results.Ok(response);

    }

    private static async Task<IResult> HandleStartServerAsync(
        string cacheKey,
        HttpContext httpContext,
        IReliquary modelCache,
        ILlamaServerManager manager,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        StartLlamaServerRequestDto? body;

        IResult? jsonError;

        (body, jsonError) = await ApiRequestJson.ReadAsync(
            httpContext,
            ArcanumJsonContext.Default.StartLlamaServerRequestDto,
            static ctx => ValidationError(ctx, ApiRequestJson.MalformedJsonMessage),
            cancellationToken).ConfigureAwait(false);

        if (jsonError is not null)
        {
            return jsonError;
        }

        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return ValidationError(httpContext, "Cache key is required.");
        }

        string normalizedKey = LlamaCacheKey.NormalizeModelKey(cacheKey);

        if (!modelCache.IsCached(normalizedKey))
        {
            Result<LlamaServerInfo> missing = Result<LlamaServerInfo>.Failure(
                new Error(ErrorCodes.Llama.ModelNotCached, $"Model '{normalizedKey}' is not cached. Pull it first."));

            return Results.Json(
                ApiResponse<LlamaServerInfo>.FromResult(missing, traceId),
                ArcanumJsonContext.Default.ApiResponseLlamaServerInfo,
                statusCode: StatusCodes.Status400BadRequest);
        }

        LlamaServerInfo? running = manager.TryGetRunningServer(normalizedKey);

        if (running is not null)
        {
            ILogger logger = loggerFactory.CreateLogger(typeof(LlamaEndpoints));

            if (body?.GpuLayers is not null || body?.Port is not null)
            {
                logger.LogWarning(
                    "Llama server for {CacheKey} is already running; ignoring gpuLayers/port overrides.",
                    normalizedKey);
            }

            Result<LlamaServerInfo> existing = running;

            return Results.Ok(ApiResponse<LlamaServerInfo>.FromResult(existing, traceId));
        }

        Result<LlamaServerInfo> ensure = await manager.EnsureServerAsync(
            normalizedKey,
            sourceUrl: null,
            body?.GpuLayers,
            body?.Port,
            cancellationToken).ConfigureAwait(false);

        if (ensure.IsFailure)
        {
            return Results.Json(
                ApiResponse<LlamaServerInfo>.FromResult(ensure, traceId),
                ArcanumJsonContext.Default.ApiResponseLlamaServerInfo,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Ok(ApiResponse<LlamaServerInfo>.FromResult(ensure, traceId));

    }

    private static async Task<IResult> HandleStopServerAsync(
        string cacheKey,
        HttpContext httpContext,
        ILlamaServerManager manager,
        CancellationToken cancellationToken)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return ValidationError(httpContext, "Cache key is required.");
        }

        string normalizedKey = LlamaCacheKey.NormalizeModelKey(cacheKey);

        Result stop = await manager.StopAsync(normalizedKey, cancellationToken).ConfigureAwait(false);

        ApiResponse<bool> response = stop.IsSuccess
            ? ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId)
            : ApiResponse<bool>.FromResult(Result<bool>.Failure(stop.Error), traceId);

        return stop.IsSuccess
            ? Results.Ok(response)
            : Results.Json(response, ArcanumJsonContext.Default.ApiResponseBoolean, statusCode: StatusCodes.Status404NotFound);

    }

    private static async Task<IResult> HandleStopAllServersAsync(
        HttpContext httpContext,
        ILlamaServerManager manager,
        CancellationToken cancellationToken)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        await manager.StopAllAsync(cancellationToken).ConfigureAwait(false);

        Result<bool> ok = true;

        return Results.Ok(ApiResponse<bool>.FromResult(ok, traceId));

    }

    /// <summary>
    /// Sends a minimal dummy chat request to a running <c>llama-server</c> to prime its KV-cache and
    /// verify the server actually responds to inference — distinct from <c>GET /api/health</c>, which
    /// only checks the process/port is alive. Does not start a server: warm-up requires one already
    /// running (<c>POST .../start</c> first).
    /// </summary>
    private static async Task<IResult> HandleWarmupServerAsync(
        string cacheKey,
        HttpContext httpContext,
        ILlamaServerManager manager,
        IOptionsSnapshot<ArcanumSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (string.IsNullOrWhiteSpace(cacheKey))
        {

            return ValidationError(httpContext, "Cache key is required.");

        }

        string normalizedKey = LlamaCacheKey.NormalizeModelKey(cacheKey);

        WarmupRequestDto? body;

        IResult? jsonError;

        (body, jsonError) = await ApiRequestJson.ReadAsync(
            httpContext,
            ArcanumJsonContext.Default.WarmupRequestDto,
            static ctx => ValidationError(ctx, ApiRequestJson.MalformedJsonMessage),
            cancellationToken).ConfigureAwait(false);

        if (jsonError is not null)
        {

            return jsonError;

        }

        body ??= new WarmupRequestDto();

        LlamaServerInfo? running = manager.TryGetRunningServer(normalizedKey);

        if (running is null)
        {

            Result<WarmupResultDto> notRunning = Result<WarmupResultDto>.Failure(
                new Error(
                    ErrorCodes.Llama.ServerNotRunning,
                    $"No running llama-server for cache key '{normalizedKey}'. Start it first via POST /api/llama/servers/{normalizedKey}/start."));

            return Results.Json(
                ApiResponse<WarmupResultDto>.FromResult(notRunning, traceId),
                ArcanumJsonContext.Default.ApiResponseWarmupResultDto,
                statusCode: StatusCodes.Status400BadRequest);

        }

        string modelName = ResolveWarmupModelName(settings.Value, normalizedKey);

        HttpClient http = httpClientFactory.CreateClient(OpenAiCompatibleHttpClientName);

        ApiKeyCredential credential = new(KeylessOpenAiPlaceholder);

        OpenAIClientOptions options = new()
        {
            Endpoint = new Uri(running.Endpoint),
            Transport = new HttpClientPipelineTransport(http),
        };

        ChatClient rawChatClient = new(modelName, credential, options);

        IChatClient chatClient = rawChatClient.AsIChatClient();

        int maxTokens = Math.Max(1, body.MaxTokens);

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {

            ChatOptions warmupOptions = new() { MaxOutputTokens = maxTokens };

            _ = await chatClient.GetResponseAsync(
                [new MeAiChatMessage(ChatRole.User, body.Prompt)],
                warmupOptions,
                cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            WarmupResultDto success = new(true, (int)stopwatch.ElapsedMilliseconds, running.Endpoint);

            return Results.Ok(ApiResponse<WarmupResultDto>.FromResult(Result<WarmupResultDto>.Success(success), traceId));

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            stopwatch.Stop();

            ILogger logger = loggerFactory.CreateLogger(typeof(LlamaEndpoints));

            logger.LogWarning(ex, "Llama warm-up request failed for {CacheKey} at {Endpoint}.", normalizedKey, running.Endpoint);

            // The HTTP call to the diagnostic endpoint itself succeeded (isSuccess: true); the
            // underlying warm-up inference attempt's outcome is reported via WarmupResultDto.Success
            // so operators get latency-so-far and the server endpoint either way.
            WarmupResultDto failed = new(false, (int)stopwatch.ElapsedMilliseconds, running.Endpoint);

            return Results.Ok(ApiResponse<WarmupResultDto>.FromResult(Result<WarmupResultDto>.Success(failed), traceId));

        }

    }

    /// <summary>
    /// Resolves a model name to send on the warm-up chat request. Prefers a configured model whose
    /// normalized cache key matches <paramref name="normalizedCacheKey"/> (the most accurate match
    /// for the server actually running), then <c>Arcanum:DefaultModel</c>, then the cache key itself
    /// — llama.cpp's OpenAI-compatible server does not validate <c>model</c> against the single GGUF
    /// it has loaded, so any non-empty string is functionally safe as a last resort.
    /// </summary>
    internal static string ResolveWarmupModelName(ArcanumSettings settings, string normalizedCacheKey)
    {

        foreach (ProviderSettings provider in settings.Providers ?? [])
        {

            if (provider.Type != AiProviderKind.LlamaCppServer)
            {

                continue;

            }

            foreach (ModelEntry model in provider.Models)
            {

                if (!string.IsNullOrWhiteSpace(model.Name)
                    && string.Equals(LlamaCacheKey.NormalizeModelKey(model.Name), normalizedCacheKey, StringComparison.Ordinal))
                {

                    return model.Name;

                }

            }

            if (provider.LlamaCpp?.ModelMap is { Count: > 0 } modelMap)
            {

                foreach (string modelKey in modelMap.Keys)
                {

                    if (string.Equals(LlamaCacheKey.NormalizeModelKey(modelKey), normalizedCacheKey, StringComparison.Ordinal))
                    {

                        return modelKey;

                    }

                }

            }

        }

        return string.IsNullOrWhiteSpace(settings.DefaultModel) ? normalizedCacheKey : settings.DefaultModel;

    }

    private static IResult ValidationError(HttpContext httpContext, string message)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        Result<string> invalid = Result<string>.Failure(new Error(ErrorCodes.Validation.InvalidBody, message));

        return Results.Json(
            ApiResponse<string>.FromResult(invalid, traceId),
            ArcanumJsonContext.Default.ApiResponseString,
            statusCode: StatusCodes.Status400BadRequest);

    }

    private static async Task WriteProgressFrameAsync(
        HttpContext httpContext,
        LlamaPullProgress frame,
        ArrayBufferWriter<byte> buffer,
        CancellationToken cancellationToken)
    {

        LlamaPullProgress sanitized = frame.Error is null
            ? frame
            : frame with { Error = PublicPullFailureMessage };

        buffer.ResetWrittenCount();

        await using (Utf8JsonWriter writer = new(buffer))
        {
            JsonSerializer.Serialize(writer, sanitized, ArcanumJsonContext.Default.LlamaPullProgress);
        }

        buffer.Write(NewlineBytes);

        await httpContext.Response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

        await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

    }

}
