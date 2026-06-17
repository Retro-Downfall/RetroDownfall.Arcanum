using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.LlamaCpp;

internal static class LlamaEndpoints
{

    private const string PublicPullFailureMessage =
        "Model download failed. See server logs for details.";

    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    internal static void MapLlamaEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapPost("/llama/models/pull", HandlePullModelAsync).WithName("PostLlamaModelPull");

        apiGroup.MapGet("/llama/models", HandleListModels).WithName("GetLlamaCachedModels");

        apiGroup.MapGet("/llama/servers", HandleListServers).WithName("GetLlamaServers");

        apiGroup.MapPost("/llama/servers/{cacheKey}/start", HandleStartServerAsync).WithName("PostLlamaServerStart");

        apiGroup.MapPost("/llama/servers/{cacheKey}/stop", HandleStopServerAsync).WithName("PostLlamaServerStop");

        apiGroup.MapPost("/llama/servers/stop", HandleStopAllServersAsync).WithName("PostLlamaServersStopAll");

    }

    private static async Task<IResult> HandlePullModelAsync(
        HttpContext httpContext,
        IGgufModelCache modelCache,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {

        PullModelRequestDto? body = await httpContext.Request
            .ReadFromJsonAsync(ArcanumJsonContext.Default.PullModelRequestDto, cancellationToken)
            .ConfigureAwait(false);

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

        ArrayBufferWriter<byte> buffer = new(256);

        Channel<LlamaPullProgress> channel = Channel.CreateBounded<LlamaPullProgress>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        Task writerTask = WriteProgressStreamAsync(httpContext, channel.Reader, buffer, cancellationToken);

        Progress<LlamaPullProgress> progress = new(frame => _ = channel.Writer.TryWrite(frame));

        try
        {
            Result<string> result = await modelCache.EnsureModelAsync(
                cacheKey,
                normalizedUrl,
                body.Sha256,
                progress,
                cancellationToken).ConfigureAwait(false);

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
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        IGgufModelCache modelCache,
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
        IGgufModelCache modelCache,
        ILlamaServerManager manager,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        StartLlamaServerRequestDto? body = await httpContext.Request
            .ReadFromJsonAsync(ArcanumJsonContext.Default.StartLlamaServerRequestDto, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return ValidationError(httpContext, "Cache key is required.");
        }

        string normalizedKey = LlamaCacheKey.NormalizeModelKey(cacheKey);

        if (!modelCache.IsCached(normalizedKey))
        {
            Result<LlamaServerInfo> missing = Result<LlamaServerInfo>.Failure(
                new Error("Llama.ModelNotCached", $"Model '{normalizedKey}' is not cached. Pull it first."));

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

    private static IResult ValidationError(HttpContext httpContext, string message)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        Result<string> invalid = Result<string>.Failure(new Error("Validation.InvalidBody", message));

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
