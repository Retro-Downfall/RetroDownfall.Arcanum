using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// <c>Idempotency-Key</c> request-replay support (DESIGN.md §11.17) for side-effecting inference
/// endpoints (<c>/api/intelligence/ping</c>, <c>/api/intelligence/ping-stream</c>,
/// <c>/v1/chat/completions</c>, <c>/v1/embeddings</c>). Opt-in: requests without the header pass
/// through untouched at effectively zero cost.
///
/// <para>
/// Two factory shapes cover every call site in the codebase: <see cref="ForBoundArgument{TRequest}"/>
/// for handlers whose body is bound as a route-handler parameter (its already-bound value is
/// re-serialized via the same source-generated <see cref="JsonTypeInfo{T}"/> to derive canonical
/// hash bytes — no raw body re-read needed), and <see cref="ForRawBody"/> for handlers that read
/// <c>HttpContext.Request.Body</c> themselves (buffers the raw bytes for hashing, then rewinds so
/// the handler can still read it).
/// </para>
///
/// <para>
/// Both buffered and streaming (NDJSON/SSE) responses are cached the same way: on a cache miss,
/// <see cref="HttpResponse.Body"/> is substituted with an <see cref="IdempotencyBufferingStream"/>
/// that tees every write into a capped in-memory buffer while still forwarding to the real
/// response stream, and an <see cref="HttpResponse.OnCompleted"/> callback persists the buffer
/// once the response has finished (only if it never exceeded the cap). A cache hit never invokes
/// the handler — it short-circuits with a small <see cref="IdempotencyReplayResult"/> that replays
/// the cached bytes verbatim.
/// </para>
/// </summary>
public static class IdempotencyEndpointFilters
{

    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> ForBoundArgument<TRequest>(
        int argumentIndex,
        JsonTypeInfo<TRequest> requestTypeInfo)
        where TRequest : class
    {

        return (context, next) =>
        {

            if (!TryResolveIdempotencyKey(context.HttpContext, out string? key, out IResult? keyError))
            {

                return keyError is not null
                    ? ValueTask.FromResult<object?>(keyError)
                    : next(context);

            }

            TRequest? request = context.GetArgument<TRequest?>(argumentIndex);

            byte[] bodyBytes = request is null
                ? []
                : JsonSerializer.SerializeToUtf8Bytes(request, requestTypeInfo);

            return InvokeCoreAsync(context, next, key!, bodyBytes);

        };

    }

    public static async ValueTask<object?> ForRawBody(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {

        if (!TryResolveIdempotencyKey(context.HttpContext, out string? key, out IResult? keyError))
        {

            return keyError is not null
                ? keyError
                : await next(context).ConfigureAwait(false);

        }

        HttpRequest request = context.HttpContext.Request;

        request.EnableBuffering();

        byte[] bodyBytes;

        using (MemoryStream buffer = new())
        {

            await request.Body.CopyToAsync(buffer, context.HttpContext.RequestAborted).ConfigureAwait(false);

            bodyBytes = buffer.ToArray();

        }

        request.Body.Position = 0;

        return await InvokeCoreAsync(context, next, key!, bodyBytes).ConfigureAwait(false);

    }

    private static async ValueTask<object?> InvokeCoreAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string key,
        byte[] bodyBytes)
    {

        HttpContext httpContext = context.HttpContext;

        IOptionsMonitor<ArcanumSettings> optionsMonitor =
            httpContext.RequestServices.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

        SecuritySettings security = optionsMonitor.CurrentValue.Security ?? new SecuritySettings();

        string keyHash = ComputeKeyHash(key, bodyBytes);

        IIdempotencyStore store = httpContext.RequestServices.GetRequiredService<IIdempotencyStore>();

        ILogger logger = httpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("RetroDownfall.Arcanum.Api.Security.IdempotencyEndpointFilters");

        int ttlHours = ArcanumSettingClamps.SecurityIdempotencyTtlHours(security.IdempotencyTtlHours);

        DateTimeOffset notOlderThan = DateTimeOffset.UtcNow.AddHours(-ttlHours);

        IdempotencyRecord? cached;

        try
        {

            cached = await store.TryGetAsync(keyHash, notOlderThan, httpContext.RequestAborted).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            // Fail open: an unavailable cache backing store must never block inference.
            logger.LogWarning(ex, "Idempotency cache lookup failed for hash {KeyHash}; proceeding without replay.", keyHash);

            cached = null;

        }

        if (cached is not null)
        {

            return new IdempotencyReplayResult(cached.StatusCode, cached.ContentType, cached.ResponseBody);

        }

        int maxCacheBytes = ArcanumSettingClamps.SecurityIdempotencyMaxResponseBytes(security.IdempotencyMaxResponseBytes);

        Stream originalBody = httpContext.Response.Body;

        IdempotencyBufferingStream teeStream = new(originalBody, maxCacheBytes);

        httpContext.Response.Body = teeStream;

        httpContext.Response.OnCompleted(() => PersistIfCapturedAsync(httpContext, store, keyHash, teeStream, logger));

        return await next(context).ConfigureAwait(false);

    }

    private static async Task PersistIfCapturedAsync(
        HttpContext httpContext,
        IIdempotencyStore store,
        string keyHash,
        IdempotencyBufferingStream teeStream,
        ILogger logger)
    {

        try
        {

            if (!teeStream.WithinCap)
            {

                return;

            }

            byte[] buffered = teeStream.GetBufferedBytes();

            if (buffered.Length == 0)
            {

                return;

            }

            string body = Encoding.UTF8.GetString(buffered);

            await store.SaveAsync(
                keyHash,
                httpContext.Response.StatusCode,
                httpContext.Response.ContentType,
                body,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Failed to persist idempotency cache entry for hash {KeyHash}.", keyHash);

        }

    }

    private static bool TryResolveIdempotencyKey(HttpContext httpContext, out string? key, out IResult? error)
    {

        error = null;

        key = null;

        if (!httpContext.Request.Headers.TryGetValue(ArcanumApiHeaders.IdempotencyKey, out StringValues values)
            || values.Count == 0)
        {

            return false;

        }

        string? candidate = values.Count == 1 ? values[0] : null;

        if (string.IsNullOrEmpty(candidate))
        {

            return false;

        }

        if (candidate.Length > ArcanumSettingClamps.SecurityIdempotencyKeyMaxChars)
        {

            error = BuildKeyTooLongError(httpContext);

            return false;

        }

        key = candidate;

        return true;

    }

    /// <summary>Internal (not private) solely so integration tests can pre-seed/inspect cache rows by their exact hash.</summary>
    internal static string ComputeKeyHash(string key, byte[] bodyBytes)
    {

        byte[] keyBytes = Encoding.UTF8.GetBytes(key);

        byte[] combined = bodyBytes.Length == 0 ? keyBytes : [.. keyBytes, .. bodyBytes];

        byte[] hash = SHA256.HashData(combined);

        return Convert.ToHexString(hash);

    }

    private static IResult BuildKeyTooLongError(HttpContext httpContext)
    {

        const string message = "Idempotency-Key header exceeds the maximum allowed length (256 characters).";

        if (httpContext.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
        {

            OpenAiErrorResponse response = new(new OpenAiErrorDetail(
                message,
                "invalid_request_error",
                Param: "idempotency_key",
                Code: "invalid_value"));

            return Results.Json(response, ArcanumJsonContext.Default.OpenAiErrorResponse, statusCode: StatusCodes.Status400BadRequest);

        }

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        ApiResponse<string> body = ApiResponse<string>.FromResult(
            Result<string>.Failure(new Error(ErrorCodes.Security.IdempotencyKeyTooLong, message)),
            traceId);

        return Results.Json(body, ArcanumJsonContext.Default.ApiResponseString, statusCode: StatusCodes.Status400BadRequest);

    }

}

/// <summary>
/// Terminal <see cref="IResult"/> returned on an idempotency cache hit — replays the cached status
/// code, content type, and body verbatim without invoking the endpoint handler.
/// </summary>
internal sealed class IdempotencyReplayResult(int statusCode, string? contentType, string body) : IResult
{

    public Task ExecuteAsync(HttpContext httpContext)
    {

        httpContext.Response.StatusCode = statusCode;

        if (!string.IsNullOrEmpty(contentType))
        {

            httpContext.Response.ContentType = contentType;

        }

        byte[] bytes = Encoding.UTF8.GetBytes(body);

        return httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted).AsTask();

    }

}

/// <summary>
/// Tees every write into a capped in-memory buffer while forwarding everything unmodified to
/// <paramref name="inner"/> (the real response stream) so the client always receives a complete,
/// un-truncated response regardless of whether buffering is later abandoned. Once the buffer would
/// exceed <paramref name="maxBytes"/> it stops accumulating (releasing the memory it already held)
/// and <see cref="WithinCap"/> flips permanently to <c>false</c> — the caller uses that to skip
/// caching an oversized response without ever affecting what was sent to the client.
/// </summary>
internal sealed class IdempotencyBufferingStream(Stream inner, int maxBytes) : Stream
{

    private readonly MemoryStream _buffer = new();

    private bool _capExceeded;

    public bool WithinCap => !_capExceeded;

    public byte[] GetBufferedBytes() => _buffer.ToArray();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {

        TryBuffer(buffer.AsSpan(offset, count));

        inner.Write(buffer, offset, count);

    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {

        TryBuffer(buffer.AsSpan(offset, count));

        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {

        TryBuffer(buffer.Span);

        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

    }

    private void TryBuffer(ReadOnlySpan<byte> span)
    {

        if (_capExceeded)
        {

            return;

        }

        try
        {

            if (_buffer.Length + span.Length > maxBytes)
            {

                _capExceeded = true;

                _buffer.SetLength(0);

                _buffer.Capacity = 0;

                return;

            }

            _buffer.Write(span);

        }
        catch (Exception ex) when (ex is OutOfMemoryException or ObjectDisposedException)
        {

            // Never let a buffering failure break the live response — just stop caching.
            _capExceeded = true;

        }

    }

    protected override void Dispose(bool disposing)
    {

        if (disposing)
        {

            _buffer.Dispose();

        }

        base.Dispose(disposing);

    }

}
