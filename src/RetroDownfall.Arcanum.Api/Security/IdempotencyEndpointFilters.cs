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

    private static readonly ConcurrentDictionary<string, LocalFlight> InFlight = new(StringComparer.Ordinal);

    private static readonly string ProcessInstanceId = Guid.NewGuid().ToString("N");

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
            IdempotencyIdentity.NormalizeQuery(httpContext),
            httpContext.Request.ContentType);

        IIdempotencyClaimStore claimStore =
            httpContext.RequestServices.GetRequiredService<IIdempotencyClaimStore>();

        ILogger logger = httpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("RetroDownfall.Arcanum.Api.Security.IdempotencyEndpointFilters");

        IdempotencyLeaseTiming timing =
            httpContext.RequestServices.GetService<IdempotencyLeaseTiming>()
            ?? IdempotencyLeaseTiming.Default;

        while (true)
        {

            httpContext.RequestAborted.ThrowIfCancellationRequested();

            LocalFlight candidate = new();

            LocalFlight flight = InFlight.GetOrAdd(claimKeyHash, candidate);

            if (!ReferenceEquals(candidate, flight))
            {

                await flight.Completion.Task.WaitAsync(httpContext.RequestAborted).ConfigureAwait(false);

                continue;

            }

            bool releaseDeferred = false;

            try
            {

                return await InvokeLocalLeaderAsync(
                        context,
                        next,
                        claimKeyHash,
                        fingerprintHash,
                        security,
                        claimStore,
                        logger,
                        timing,
                        flight,
                        () => releaseDeferred = true)
                    .ConfigureAwait(false);

            }
            finally
            {

                if (!releaseDeferred)
                {

                    ReleaseLocalFlight(claimKeyHash, flight);

                }

            }

        }

    }

    private static async ValueTask<object?> InvokeLocalLeaderAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string claimKeyHash,
        string fingerprintHash,
        SecuritySettings security,
        IIdempotencyClaimStore claimStore,
        ILogger logger,
        IdempotencyLeaseTiming timing,
        LocalFlight flight,
        Action deferRelease)
    {

        HttpContext httpContext = context.HttpContext;

        IdempotencyClaim? existing;

        try
        {

            existing = await claimStore.TryGetAsync(claimKeyHash, httpContext.RequestAborted).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex)
        {

            LogStoreFault(logger, "lookup", claimKeyHash, ex);

            return await ExecuteFreshAsync(
                    context,
                    next,
                    claimKeyHash,
                    flight,
                    deferRelease)
                .ConfigureAwait(false);

        }

        if (existing is not null)
        {

            if (!string.Equals(existing.FingerprintHash, fingerprintHash, StringComparison.Ordinal))
            {

                return BuildConflictResult(httpContext);

            }

            if (TryBuildReplay(existing, out IdempotencyReplayResult? replay))
            {

                return replay;

            }

            if (IsLive(existing, timing.TimeProvider.GetUtcNow()))
            {

                if (!IsSameProcessOwner(existing.OwnerId))
                {

                    return BuildInProgressResult(httpContext);

                }

                if (!await TryRetireSameProcessOrphanAsync(
                        existing,
                        claimKeyHash,
                        claimStore,
                        logger,
                        httpContext.RequestAborted)
                    .ConfigureAwait(false))
                {

                    return await ExecuteFreshAsync(
                            context,
                            next,
                            claimKeyHash,
                            flight,
                            deferRelease)
                        .ConfigureAwait(false);

                }

            }

        }

        string ownerId = CreateOwnerId();

        bool sameProcessRecoveryAttempted = existing is not null
            && IsLive(existing, timing.TimeProvider.GetUtcNow())
            && IsSameProcessOwner(existing.OwnerId);

        for (int attempt = 0; attempt < 2; attempt++)
        {

            DateTimeOffset now = timing.TimeProvider.GetUtcNow();

            IdempotencyClaimAcquireResult acquire;

            try
            {

                acquire = await claimStore.TryAcquireAsync(
                        new IdempotencyClaimAcquireRequest(
                            claimKeyHash,
                            fingerprintHash,
                            ownerId,
                            now.Add(timing.LeaseDuration),
                            now),
                        httpContext.RequestAborted)
                    .ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {

                throw;

            }
            catch (Exception ex)
            {

                LogStoreFault(logger, "acquire", claimKeyHash, ex);

                return await ExecuteFreshAsync(
                        context,
                        next,
                        claimKeyHash,
                        flight,
                        deferRelease)
                    .ConfigureAwait(false);

            }

            if (acquire.Conflict
                || !string.Equals(acquire.Claim.FingerprintHash, fingerprintHash, StringComparison.Ordinal))
            {

                return BuildConflictResult(httpContext);

            }

            if (acquire.Acquired)
            {

                if (!string.Equals(acquire.Claim.OwnerId, ownerId, StringComparison.Ordinal)
                    || acquire.Claim.State != IdempotencyClaimState.Running)
                {

                    throw new InvalidOperationException(
                        "Idempotency store reported acquisition without matching claim ownership.");

                }

                return await ExecuteMissAsync(
                        context,
                        next,
                        claimKeyHash,
                        acquire.Claim.Id,
                        ownerId,
                        acquire.Claim.LeaseExpiresAt,
                        security,
                        claimStore,
                        logger,
                        timing,
                        flight,
                        deferRelease)
                    .ConfigureAwait(false);

            }

            if (TryBuildReplay(acquire.Claim, out IdempotencyReplayResult? replay))
            {

                return replay;

            }

            if (IsLive(acquire.Claim, timing.TimeProvider.GetUtcNow()))
            {

                if (!IsSameProcessOwner(acquire.Claim.OwnerId))
                {

                    return BuildInProgressResult(httpContext);

                }

                if (sameProcessRecoveryAttempted)
                {

                    throw new InvalidOperationException(
                        "Idempotency ownership could not be established after local recovery.");

                }

                if (!await TryRetireSameProcessOrphanAsync(
                        acquire.Claim,
                        claimKeyHash,
                        claimStore,
                        logger,
                        httpContext.RequestAborted)
                    .ConfigureAwait(false))
                {

                    return await ExecuteFreshAsync(
                            context,
                            next,
                            claimKeyHash,
                            flight,
                            deferRelease)
                        .ConfigureAwait(false);

                }

                sameProcessRecoveryAttempted = true;

                continue;

            }

            if (attempt == 0
                && acquire.Claim.State is IdempotencyClaimState.Failed or IdempotencyClaimState.Abandoned)
            {

                continue;

            }

            throw new InvalidOperationException("Idempotency ownership could not be established.");

        }

        throw new InvalidOperationException("Idempotency ownership transition limit exceeded.");

    }

    private static async ValueTask<object?> ExecuteFreshAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string claimKeyHash,
        LocalFlight flight,
        Action deferRelease)
    {

        HttpContext httpContext = context.HttpContext;

        httpContext.RequestAborted.ThrowIfCancellationRequested();

        httpContext.Response.OnCompleted(() =>
        {

            ReleaseLocalFlight(claimKeyHash, flight);

            return Task.CompletedTask;

        });

        deferRelease();

        try
        {

            httpContext.RequestAborted.ThrowIfCancellationRequested();

            return await next(context).ConfigureAwait(false);

        }
        catch
        {

            ReleaseLocalFlight(claimKeyHash, flight);

            throw;

        }

    }

    private static async Task<object?> ExecuteMissAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string claimKeyHash,
        Guid claimId,
        string ownerId,
        DateTimeOffset initialLeaseExpiresAt,
        SecuritySettings security,
        IIdempotencyClaimStore claimStore,
        ILogger logger,
        IdempotencyLeaseTiming timing,
        LocalFlight flight,
        Action deferRelease)
    {

        int maxCacheBytes = ArcanumSettingClamps.SecurityIdempotencyMaxResponseBytes(
            ArcanumRuntimeDefaults.SecurityIdempotencyMaxResponseBytes);

        HttpContext httpContext = context.HttpContext;

        Stream originalBody = httpContext.Response.Body;

        IdempotencyBufferingStream teeStream = new(originalBody, maxCacheBytes);

        httpContext.Response.Body = teeStream;

        OwnedClaimExecution owned = new(
            httpContext,
            claimStore,
            claimId,
            ownerId,
            initialLeaseExpiresAt,
            teeStream,
            logger,
            timing,
            () => ReleaseLocalFlight(claimKeyHash, flight));

        httpContext.Response.OnCompleted(owned.CompleteResponseAsync);

        deferRelease();

        owned.BindEndpointCancellation();
        TurnIdempotencyAmbient.Publish(true, owned.OwnershipLostToken);
        owned.StartHeartbeat();

        try
        {

            httpContext.RequestAborted.ThrowIfCancellationRequested();

            return await next(context).ConfigureAwait(false);

        }
        catch
        {

            await owned.FailAsync().ConfigureAwait(false);

            throw;

        }

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
            bool explicitlyTerminal = TurnContextGuards.IsIdempotencyTerminal(httpContext)
                || httpContext.Response.StatusCode == StatusCodes.Status204NoContent;
            // Terminal when:
            // - a writer explicitly marked completion (including a zero-byte response),
            // - the endpoint explicitly returned 204 No Content, or
            // - the request was not aborted and we buffered a non-empty in-cap body (buffered IResult paths).
            //
            // Gated on the response's status class regardless of which of the above applies: a 5xx or
            // a retryable 4xx (429/408) is never cached as a replayable Completed claim, even when the
            // writer buffered a full body or marked itself terminal, because DESIGN's "retry with the
            // same key" contract means a fresh execution, not the frozen failure. A deterministic 4xx
            // (validation refusal) still caches, since resending the same request would fail the same
            // way for the same reason.
            bool terminalStreamValid = withinCap
                && IsIdempotencyReplayableStatus(httpContext.Response.StatusCode)
                && (explicitlyTerminal || (buffered.Length > 0 && !aborted));

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
            logger.LogWarning(
                "Failed to persist idempotency claim {ClaimId}; exception type {ExceptionType}.",
                claimId,
                ex.GetType().Name);

            try
            {
                await claimStore.MarkFailedAsync(claimId, ownerId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception markFailedException)
            {
                logger.LogWarning(
                    "Failed to mark idempotency claim {ClaimId} non-replayable; exception type {ExceptionType}.",
                    claimId,
                    markFailedException.GetType().Name);
            }
        }
    }

    private static bool TryBuildReplay(
        IdempotencyClaim claim,
        out IdempotencyReplayResult? replay)
    {

        if (claim.State == IdempotencyClaimState.Completed
            && claim.TerminalStreamComplete
            && claim.StatusCode is int status
            && claim.ResponseBody is not null)
        {

            replay = new IdempotencyReplayResult(status, claim.ContentType, claim.ResponseBody);

            return true;

        }

        replay = null;

        return false;

    }

    private static bool IsLive(IdempotencyClaim claim, DateTimeOffset now) =>
        claim.State is IdempotencyClaimState.Running or IdempotencyClaimState.Claimed
        && claim.LeaseExpiresAt > now;

    /// <summary>
    /// Whether a response with this status code is safe to cache as a replayable Completed claim.
    /// A 5xx is always the installation's own fault, not the caller's request, and 429/408 are the
    /// two 4xx codes that explicitly mean "the same request would succeed later" rather than "this
    /// request is invalid" — none of the three should freeze a caller out of retrying with the same
    /// key for the rest of the claim's TTL. Every other 4xx (validation refusals, 409 conflicts, and
    /// so on) is deterministic for the same fingerprint and stays cacheable.
    /// </summary>
    internal static bool IsIdempotencyReplayableStatus(int statusCode) =>
        statusCode < StatusCodes.Status500InternalServerError
        && statusCode != StatusCodes.Status429TooManyRequests
        && statusCode != StatusCodes.Status408RequestTimeout;

    internal static string CreateOwnerId() =>
        string.Concat(ProcessInstanceId, ":", Guid.NewGuid().ToString("N"));

    internal static bool IsSameProcessOwner(string ownerId) =>
        ownerId.Length > ProcessInstanceId.Length
        && ownerId.StartsWith(ProcessInstanceId, StringComparison.Ordinal)
        && ownerId[ProcessInstanceId.Length] == ':';

    private static async Task<bool> TryRetireSameProcessOrphanAsync(
        IdempotencyClaim claim,
        string claimKeyHash,
        IIdempotencyClaimStore claimStore,
        ILogger logger,
        CancellationToken cancellationToken)
    {

        try
        {

            await claimStore.MarkFailedAsync(claim.Id, claim.OwnerId, cancellationToken).ConfigureAwait(false);

            return true;

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex)
        {

            LogStoreFault(logger, "same-process recovery", claimKeyHash, ex);

            return false;

        }

    }

    private static void LogStoreFault(
        ILogger logger,
        string operation,
        string claimKeyHash,
        Exception exception)
    {

        logger.LogWarning(
            "Idempotency claim {Operation} failed for {ClaimKeyHash}; exception type {ExceptionType}; proceeding without durable replay.",
            operation,
            claimKeyHash,
            exception.GetType().Name);

    }

    private static void ReleaseLocalFlight(string claimKeyHash, LocalFlight flight)
    {

        if (!flight.TryRelease())
        {

            return;

        }

        _ = InFlight.TryRemove(new KeyValuePair<string, LocalFlight>(claimKeyHash, flight));

        _ = flight.Completion.TrySetResult();

    }

    private static IResult BuildInProgressResult(HttpContext httpContext) =>
        BuildErrorResult(
            httpContext,
            ErrorCodes.Security.IdempotencyInProgress,
            "idempotency_in_progress",
            "A request with this Idempotency-Key is already in progress. Retry later.");

    private static IResult BuildConflictResult(HttpContext httpContext) =>
        BuildErrorResult(
            httpContext,
            ErrorCodes.Security.IdempotencyConflict,
            "idempotency_conflict",
            "Idempotency-Key reused with a different request fingerprint.");

    private static IResult BuildErrorResult(
        HttpContext httpContext,
        string nativeCode,
        string openAiCode,
        string message)
    {
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
                        Code: openAiCode)),
                ArcanumJsonContext.Default.OpenAiErrorResponse,
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Json(
            ApiResponse<string>.FromResult(
                Result<string>.Failure(new Error(nativeCode, message)),
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

        if (values.Count > 1)
        {

            // Ambiguity is a loud client error, never a silent downgrade to unprotected execution:
            // falling through here would run the side-effecting handler with no claim and no fingerprint
            // while the caller still believes the header it sent is protecting it.
            error = BuildKeyRejectionError(httpContext, AmbiguousKeyMessage, ErrorCodes.Security.IdempotencyKeyAmbiguous);

            return false;

        }

        string? candidate = values[0];

        if (string.IsNullOrEmpty(candidate))
        {

            return false;

        }

        if (candidate.Length > ArcanumSettingClamps.SecurityIdempotencyKeyMaxChars)
        {

            error = BuildKeyRejectionError(httpContext, KeyTooLongMessage, ErrorCodes.Security.IdempotencyKeyTooLong);

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

    private const string KeyTooLongMessage =
        "Idempotency-Key header exceeds the maximum allowed length (256 characters).";

    private const string AmbiguousKeyMessage =
        "Idempotency-Key header was supplied more than once; send exactly one value.";

    private static IResult BuildKeyRejectionError(HttpContext httpContext, string message, string nativeCode)
    {

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
            Result<string>.Failure(new Error(nativeCode, message)),
            traceId);

        return Results.Json(body, ArcanumJsonContext.Default.ApiResponseString, statusCode: StatusCodes.Status400BadRequest);

    }

    private sealed class LocalFlight
    {

        private int _released;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryRelease() => Interlocked.Exchange(ref _released, 1) == 0;

    }

    private sealed class OwnedClaimExecution(
        HttpContext httpContext,
        IIdempotencyClaimStore claimStore,
        Guid claimId,
        string ownerId,
        DateTimeOffset initialLeaseExpiresAt,
        IdempotencyBufferingStream teeStream,
        ILogger logger,
        IdempotencyLeaseTiming timing,
        Action release)
    {

        private readonly CancellationToken _originalRequestAborted = httpContext.RequestAborted;

        private readonly CancellationTokenSource _executionLifetime = new();

        private readonly CancellationTokenSource _ownershipLost = new();

        private CancellationTokenSource? _endpointCancellation;

        private Task _heartbeatTask = Task.CompletedTask;

        private int _finalized;

        public CancellationToken OwnershipLostToken => _ownershipLost.Token;

        public void BindEndpointCancellation()
        {

            _endpointCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _originalRequestAborted,
                _ownershipLost.Token);
            httpContext.RequestAborted = _endpointCancellation.Token;

        }

        public void StartHeartbeat()
        {

            _heartbeatTask = RunHeartbeatAsync(
                claimStore,
                claimId,
                ownerId,
                initialLeaseExpiresAt,
                logger,
                timing,
                LoseOwnership,
                _executionLifetime.Token);

        }

        public Task CompleteResponseAsync() => FinalizeAsync(responseCompleted: true);

        public Task FailAsync() => FinalizeAsync(responseCompleted: false);

        private async Task FinalizeAsync(bool responseCompleted)
        {

            if (Interlocked.Exchange(ref _finalized, 1) != 0)
            {

                return;

            }

            try
            {

                _executionLifetime.Cancel();

                try
                {

                    await _heartbeatTask.ConfigureAwait(false);

                }
                catch (OperationCanceledException) when (_executionLifetime.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {

                    logger.LogWarning(
                        "Idempotency heartbeat shutdown failed for {ClaimId}; exception type {ExceptionType}.",
                        claimId,
                        ex.GetType().Name);

                }

                if (responseCompleted)
                {

                    await PersistClaimAsync(httpContext, claimStore, claimId, ownerId, teeStream, logger)
                        .ConfigureAwait(false);

                }
                else
                {

                    try
                    {

                        await claimStore.MarkFailedAsync(claimId, ownerId, CancellationToken.None)
                            .ConfigureAwait(false);

                    }
                    catch (Exception ex)
                    {

                        logger.LogWarning(
                            "Failed to mark idempotency claim {ClaimId} after handler failure; exception type {ExceptionType}.",
                            claimId,
                            ex.GetType().Name);

                    }

                }

            }
            finally
            {

                httpContext.RequestAborted = _originalRequestAborted;
                _endpointCancellation?.Dispose();
                _ownershipLost.Dispose();
                _executionLifetime.Dispose();

                release();

            }

        }

        private void LoseOwnership()
        {

            try
            {

                _ownershipLost.Cancel();

            }
            catch (Exception ex)
            {

                logger.LogWarning(
                    "Idempotency ownership-loss cancellation failed for {ClaimId}; exception type {ExceptionType}.",
                    claimId,
                    ex.GetType().Name);

            }

        }

    }

    private static async Task RunHeartbeatAsync(
        IIdempotencyClaimStore claimStore,
        Guid claimId,
        string ownerId,
        DateTimeOffset initialLeaseExpiresAt,
        ILogger logger,
        IdempotencyLeaseTiming timing,
        Action loseOwnership,
        CancellationToken cancellationToken)
    {

        DateTimeOffset startedAt = timing.TimeProvider.GetUtcNow();

        DateTimeOffset deadline = startedAt.Add(timing.MaximumLifetime);

        DateTimeOffset leaseExpiresAt = initialLeaseExpiresAt;

        while (true)
        {

            DateTimeOffset beforeDelay = timing.TimeProvider.GetUtcNow();

            TimeSpan lifetimeRemaining = deadline - beforeDelay;

            if (lifetimeRemaining <= TimeSpan.Zero)
            {

                logger.LogWarning(
                    "Idempotency heartbeat lifetime expired for {ClaimId}; relinquishing ownership.",
                    claimId);

                loseOwnership();

                return;

            }

            TimeSpan leaseRemaining = leaseExpiresAt - beforeDelay;

            if (leaseRemaining <= TimeSpan.Zero)
            {

                logger.LogWarning(
                    "Idempotency lease expired for {ClaimId}; relinquishing ownership.",
                    claimId);

                loseOwnership();

                return;

            }

            TimeSpan halfLeaseRemaining = TimeSpan.FromTicks(Math.Max(1, leaseRemaining.Ticks / 2));
            TimeSpan delay = timing.HeartbeatInterval < halfLeaseRemaining
                ? timing.HeartbeatInterval
                : halfLeaseRemaining;

            if (lifetimeRemaining < delay)
            {

                delay = lifetimeRemaining;

            }

            await Task.Delay(delay, timing.TimeProvider, cancellationToken).ConfigureAwait(false);

            DateTimeOffset now = timing.TimeProvider.GetUtcNow();

            if (now >= deadline || now >= leaseExpiresAt)
            {

                logger.LogWarning(
                    "Idempotency heartbeat lifetime or lease expired for {ClaimId}; relinquishing ownership.",
                    claimId);

                loseOwnership();

                return;

            }

            try
            {

                DateTimeOffset renewedLeaseExpiresAt = now.Add(timing.LeaseDuration);

                Task<bool> heartbeat = claimStore.HeartbeatAsync(
                    claimId,
                    ownerId,
                    renewedLeaseExpiresAt,
                    cancellationToken);

                TimeSpan ownershipConfirmationTimeout = leaseExpiresAt - now;
                if (timing.HeartbeatInterval < ownershipConfirmationTimeout)
                {

                    ownershipConfirmationTimeout = timing.HeartbeatInterval;

                }

                bool renewed = await heartbeat
                    .WaitAsync(ownershipConfirmationTimeout, timing.TimeProvider, cancellationToken)
                    .ConfigureAwait(false);

                if (!renewed)
                {

                    logger.LogWarning(
                        "Idempotency heartbeat no longer owns {ClaimId}; cancelling owned execution.",
                        claimId);
                    loseOwnership();
                    return;

                }

                leaseExpiresAt = renewedLeaseExpiresAt;

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {

                return;

            }
            catch (TimeoutException)
            {

                logger.LogWarning(
                    "Idempotency heartbeat timed out for {ClaimId}; relinquishing ownership.",
                    claimId);

                loseOwnership();

                return;

            }
            catch (Exception ex)
            {

                logger.LogWarning(
                    "Idempotency heartbeat failed for {ClaimId}; exception type {ExceptionType}.",
                    claimId,
                    ex.GetType().Name);

                if (leaseExpiresAt - now <= timing.HeartbeatInterval)
                {

                    logger.LogWarning(
                        "Idempotency lease can no longer be renewed safely for {ClaimId}; relinquishing ownership.",
                        claimId);

                    loseOwnership();

                    return;

                }

            }

        }

    }

}

internal sealed record IdempotencyLeaseTiming(
    TimeProvider TimeProvider,
    TimeSpan LeaseDuration,
    TimeSpan HeartbeatInterval,
    TimeSpan MaximumLifetime)
{

    public static IdempotencyLeaseTiming Default { get; } = new(
        TimeProvider.System,
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromHours(24));

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
