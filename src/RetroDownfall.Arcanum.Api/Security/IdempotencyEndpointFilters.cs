using System.Collections.Concurrent;
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
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// <c>Idempotency-Key</c> request-replay support (<c>docs/Arcanum.DESIGN.md</c> §11.17) for side-effecting inference
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
///
/// <para>
/// Concurrent misses for the same claim-key hash share one in-flight handler execution.
/// Waiters await the leader's <see cref="HttpResponse.OnCompleted"/> persist, then replay
/// from a terminal <see cref="IdempotencyClaimState.Completed"/> claim only. Partial,
/// cancelled, or over-cap streams are marked Abandoned and are never replayable.
/// Fingerprint mismatch yields <see cref="ErrorCodes.Security.IdempotencyConflict"/> (409).
/// </para>
/// </summary>
public static class IdempotencyEndpointFilters
{

    private static readonly ConcurrentDictionary<string, Task> InFlight = new(StringComparer.Ordinal);

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

        TurnIdempotencyAmbient.Publish(true);

        try
        {
            return await InvokeCoreWithAmbientAsync(context, next, key, bodyBytes).ConfigureAwait(false);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }

    }

    private static async ValueTask<object?> InvokeCoreWithAmbientAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string key,
        byte[] bodyBytes)
    {

        HttpContext httpContext = context.HttpContext;

        IOptionsMonitor<ArcanumSettings> optionsMonitor =
            httpContext.RequestServices.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

        SecuritySettings security = optionsMonitor.CurrentValue.Security ?? new SecuritySettings();

        string principal = IdempotencyIdentity.ResolvePrincipal(httpContext);
        string route = IdempotencyIdentity.NormalizeRoute(httpContext);
        string method = httpContext.Request.Method;
        string claimKeyHash = IdempotencyIdentity.ComputeClaimKeyHash(principal, method, route, key);
        string fingerprintHash = IdempotencyIdentity.ComputeFingerprintHash(
            bodyBytes,
            route,
            httpContext.Request.ContentType);

        IIdempotencyClaimStore claimStore =
            httpContext.RequestServices.GetRequiredService<IIdempotencyClaimStore>();

        ILogger logger = httpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("RetroDownfall.Arcanum.Api.Security.IdempotencyEndpointFilters");

        IdempotencyClaim? existing;

        try
        {
            existing = await claimStore.TryGetAsync(claimKeyHash, httpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Idempotency claim lookup failed for {ClaimKeyHash}; proceeding without replay.", claimKeyHash);
            existing = null;
        }

        if (existing is not null)
        {
            if (!string.Equals(existing.FingerprintHash, fingerprintHash, StringComparison.Ordinal))
            {
                return BuildConflictResult(httpContext);
            }

            if (existing.State == IdempotencyClaimState.Completed
                && existing.TerminalStreamComplete
                && existing.StatusCode is int status
                && existing.ResponseBody is not null)
            {
                return new IdempotencyReplayResult(status, existing.ContentType, existing.ResponseBody);
            }

            if (existing.State is IdempotencyClaimState.Running or IdempotencyClaimState.Claimed
                && existing.LeaseExpiresAt > DateTimeOffset.UtcNow)
            {
                // Wait for in-process leader if present; otherwise poll once after a short delay path via InFlight.
            }
        }

        string ownerId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset leaseExpires = now.AddMinutes(5);

        IdempotencyClaimAcquireResult acquire;

        try
        {
            if (existing is { State: IdempotencyClaimState.Running or IdempotencyClaimState.Claimed }
                && existing.LeaseExpiresAt <= now)
            {
                _ = await claimStore.TryReclaimAsync(existing.Id, ownerId, leaseExpires, httpContext.RequestAborted)
                    .ConfigureAwait(false);
            }

            acquire = await claimStore.TryAcquireAsync(
                    new IdempotencyClaimAcquireRequest(claimKeyHash, fingerprintHash, ownerId, leaseExpires, now),
                    httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Idempotency claim acquire failed for {ClaimKeyHash}; proceeding without claim.", claimKeyHash);

            return await next(context).ConfigureAwait(false);
        }

        if (acquire.Conflict)
        {
            return BuildConflictResult(httpContext);
        }

        if (!acquire.Acquired
            && acquire.Claim.State == IdempotencyClaimState.Completed
            && acquire.Claim.TerminalStreamComplete
            && acquire.Claim.StatusCode is int completedStatus
            && acquire.Claim.ResponseBody is not null)
        {
            return new IdempotencyReplayResult(completedStatus, acquire.Claim.ContentType, acquire.Claim.ResponseBody);
        }

        if (!acquire.Acquired)
        {
            // Another owner holds a live claim — join in-process flight if any, then re-read.
            if (InFlight.TryGetValue(claimKeyHash, out Task? inFlightTask))
            {
                try
                {
                    await inFlightTask.ConfigureAwait(false);
                }
                catch
                {
                    // fall through
                }

                IdempotencyClaim? after = await claimStore.TryGetAsync(claimKeyHash, httpContext.RequestAborted)
                    .ConfigureAwait(false);

                if (after is { State: IdempotencyClaimState.Completed, TerminalStreamComplete: true, StatusCode: int s, ResponseBody: not null })
                {
                    return new IdempotencyReplayResult(s, after.ContentType, after.ResponseBody);
                }
            }
        }

        TaskCompletionSource flightTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (acquire.Acquired && InFlight.TryAdd(claimKeyHash, flightTcs.Task))
        {
            try
            {
                return await ExecuteMissAsync(
                        context,
                        next,
                        claimKeyHash,
                        acquire.Claim.Id,
                        ownerId,
                        security,
                        claimStore,
                        logger,
                        flightTcs)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = InFlight.TryRemove(new KeyValuePair<string, Task>(claimKeyHash, flightTcs.Task));
                _ = flightTcs.TrySetException(ex);

                try
                {
                    await claimStore.MarkFailedAsync(acquire.Claim.Id, ownerId, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                throw;
            }
        }

        return await ExecuteMissAsync(
                context,
                next,
                claimKeyHash,
                acquire.Claim.Id,
                acquire.Acquired ? ownerId : acquire.Claim.OwnerId,
                security,
                claimStore,
                logger,
                flightSignal: null)
            .ConfigureAwait(false);

    }

    private static async Task<object?> ExecuteMissAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string claimKeyHash,
        Guid claimId,
        string ownerId,
        SecuritySettings security,
        IIdempotencyClaimStore claimStore,
        ILogger logger,
        TaskCompletionSource? flightSignal)
    {

        int maxCacheBytes = ArcanumSettingClamps.SecurityIdempotencyMaxResponseBytes(
            ArcanumRuntimeDefaults.SecurityIdempotencyMaxResponseBytes);

        HttpContext httpContext = context.HttpContext;

        Stream originalBody = httpContext.Response.Body;

        IdempotencyBufferingStream teeStream = new(originalBody, maxCacheBytes);

        httpContext.Response.Body = teeStream;

        httpContext.Response.OnCompleted(async () =>
        {
            try
            {
                await PersistClaimAsync(httpContext, claimStore, claimId, ownerId, teeStream, logger)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (flightSignal is not null)
                {
                    _ = InFlight.TryRemove(new KeyValuePair<string, Task>(claimKeyHash, flightSignal.Task));
                    _ = flightSignal.TrySetResult();
                }
            }
        });

        return await next(context).ConfigureAwait(false);

    }

    private static async Task PersistClaimAsync(
        HttpContext httpContext,
        IIdempotencyClaimStore claimStore,
        Guid claimId,
        string ownerId,
        IdempotencyBufferingStream teeStream,
        ILogger logger)
    {
        try
        {
            bool withinCap = teeStream.WithinCap;
            byte[] buffered = withinCap ? teeStream.GetBufferedBytes() : [];
            bool aborted = httpContext.RequestAborted.IsCancellationRequested;
            // Terminal when:
            // - writer explicitly marked continue-then-replay completion, or
            // - the request was not aborted and we buffered a non-empty in-cap body (buffered IResult paths).
            bool terminalStreamValid = withinCap
                && buffered.Length > 0
                && (TurnContextGuards.IsIdempotencyTerminal(httpContext) || !aborted);

            if (!terminalStreamValid)
            {
                await claimStore.MarkAbandonedAsync(claimId, ownerId, CancellationToken.None).ConfigureAwait(false);

                return;
            }

            string body = Encoding.UTF8.GetString(buffered);

            await claimStore.CompleteAsync(
                    claimId,
                    ownerId,
                    httpContext.Response.StatusCode,
                    httpContext.Response.ContentType,
                    body,
                    terminalStreamValid: true,
                    runId: null,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist idempotency claim {ClaimId}.", claimId);

            try
            {
                await claimStore.MarkFailedAsync(claimId, ownerId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static IResult BuildConflictResult(HttpContext httpContext)
    {
        const string message = "Idempotency-Key reused with a different request fingerprint.";

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        bool isOpenAi = httpContext.Request.Path.StartsWithSegments("/v1");

        if (isOpenAi)
        {
            return Results.Json(
                new OpenAiErrorResponse(
                    new OpenAiErrorDetail(
                        message,
                        "invalid_request_error",
                        Param: null,
                        Code: "idempotency_conflict")),
                ArcanumJsonContext.Default.OpenAiErrorResponse,
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Json(
            ApiResponse<string>.FromResult(
                Result<string>.Failure(new Error(ErrorCodes.Security.IdempotencyConflict, message)),
                traceId),
            ArcanumJsonContext.Default.ApiResponseString,
            statusCode: StatusCodes.Status409Conflict);
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

    /// <summary>Legacy hash retained for test helpers; prefer <see cref="IdempotencyIdentity"/>.</summary>
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

    private bool _innerDead;

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

    public override void Flush()
    {
        if (_innerDead)
        {
            return;
        }

        try
        {
            inner.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _innerDead = true;
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_innerDead)
        {
            return;
        }

        try
        {
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _innerDead = true;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {

        TryBuffer(buffer.AsSpan(offset, count));

        if (_innerDead)
        {
            return;
        }

        try
        {
            inner.Write(buffer, offset, count);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _innerDead = true;
        }

    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {

        TryBuffer(buffer.AsSpan(offset, count));

        if (_innerDead)
        {
            return;
        }

        try
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _innerDead = true;
        }

    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {

        TryBuffer(buffer.Span);

        if (_innerDead)
        {
            return;
        }

        try
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _innerDead = true;
        }

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
