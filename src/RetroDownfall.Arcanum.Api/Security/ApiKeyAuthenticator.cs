using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// The constant-time API-key comparison itself (DESIGN §11.3), shared by the pre-binding
/// authentication middleware (<c>ApiBootstrapper.UseArcanumApiKeyAuthentication</c>) and by
/// <see cref="ApiKeyEndpointFilter"/>. Both gates share the singleton
/// <see cref="IApiKeyDigestCache"/>, so authenticating twice costs one extra SHA-256 of the presented
/// header and never a second secret-store read.
/// </summary>
/// <remarks>
/// A digest-cache miss is filled by at most one secret-store read per cache generation: concurrent
/// misses join the read in flight instead of each queueing their own (behind a parked Keychain call
/// that would be one blocking secure-storage read per request). A read that fails is remembered for
/// <see cref="FailedReadRetryDelay"/> within the same generation, so a request — including one with a
/// bogus key — never re-enters secure storage during a fault; it fails closed against whatever digest
/// is currently published. Rotation or invalidation starts a new generation and clears both.
/// </remarks>
public sealed class ApiKeyAuthenticator(
    ISecretStore secretStore,
    IApiKeyDigestCache digestCache,
    ArcanumProcessCapabilityService? processCapabilities = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>How long a failed secret-store read answers for further misses without a new read.</summary>
    internal static readonly TimeSpan FailedReadRetryDelay = TimeSpan.FromSeconds(5);

    private const int Sha256Bytes = 32;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private readonly Lock _refreshSync = new();

    private PendingRefresh? _refresh;

    private FailedRefresh? _failedRefresh;

    private sealed record PendingRefresh(long Generation, Task Completion);

    private sealed record FailedRefresh(long Generation, long FailedAtTimestamp);

    public async ValueTask<bool> IsAuthorizedAsync(HttpContext httpContext)
    {
        int maxHeaderUtf16 = ArcanumSettingClamps.MaxApiKeyHeaderUtf16Chars(
            ArcanumRuntimeDefaults.SecurityMaxApiKeyHeaderUtf16Chars);

        IHeaderDictionary headers = httpContext.Request.Headers;

        if (headers.TryGetValue(
                ArcanumApiHeaders.ProcessCapability,
                out StringValues capabilityHeader))
        {
            if (capabilityHeader.Count != 1
                || string.IsNullOrEmpty(capabilityHeader[0])
                || headers.ContainsKey(ArcanumApiHeaders.ApiKey)
                || headers.Authorization.Count > 0)
            {
                return false;
            }

            return ArcanumTransportPeer.IsLoopback(httpContext)
                && processCapabilities?.IsValidEncoded(capabilityHeader[0]) == true;
        }

        if (!TryExtractHeaderValue(headers, out string? headerValue))
        {
            return false;
        }

        if (headerValue.Length > maxHeaderUtf16)
        {
            return false;
        }

        byte[]? expectedDigest = await GetExpectedDigestAsync(httpContext.RequestAborted).ConfigureAwait(false);

        if (expectedDigest is null)
        {
            return false;
        }

        try
        {
            int headerByteCount = Encoding.UTF8.GetByteCount(headerValue);

            byte[]? rentedHeaderUtf8 = null;

            Span<byte> headerUtf8 = headerByteCount <= 256
                ? stackalloc byte[headerByteCount]
                : (rentedHeaderUtf8 = new byte[headerByteCount]);

            try
            {
                Encoding.UTF8.GetBytes(headerValue, headerUtf8);

                Span<byte> headerDigest = stackalloc byte[Sha256Bytes];

                try
                {
                    // The destination is exactly SHA-256's output size, so the throwing overload
                    // cannot fail and needs no failure arm. TryHashData's false result is only
                    // reachable with an undersized span.
                    _ = SHA256.HashData(headerUtf8, headerDigest);

                    return CryptographicOperations.FixedTimeEquals(
                        expectedDigest,
                        headerDigest);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(headerDigest);
                }
            }
            finally
            {
                ZeroHeaderUtf8(headerUtf8, rentedHeaderUtf8);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedDigest);
        }
    }

    /// <summary>
    /// Extracts the reusable credential from <c>X-Arcanum-Key</c> or a <c>Bearer</c>
    /// <c>Authorization</c> header. Process capabilities are handled first and can never be mixed
    /// with either reusable-key form.
    /// </summary>
    /// <remarks>
    /// Contract relied upon by <see cref="IsAuthorizedAsync"/>: when this returns <see langword="true"/>,
    /// <paramref name="headerValue"/> is non-null <b>and non-empty</b> — every success path ends in
    /// <c>return !string.IsNullOrEmpty(headerValue)</c>. The caller therefore performs no emptiness
    /// check of its own. Any future exit that can return <see langword="true"/> must preserve that,
    /// or the caller will hash an empty credential. A duplicated header of either kind is rejected
    /// rather than resolved, so a proxy cannot smuggle a second candidate credential.
    /// </remarks>
    private static bool TryExtractHeaderValue(
        IHeaderDictionary headers,
        [NotNullWhen(true)] out string? headerValue)
    {
        headerValue = null;

        if (headers.TryGetValue(ArcanumApiHeaders.ApiKey, out StringValues apiKeyHeader) && apiKeyHeader.Count > 0)
        {
            if (apiKeyHeader.Count > 1)
            {
                return false;
            }

            headerValue = apiKeyHeader[0];

            return !string.IsNullOrEmpty(headerValue);
        }

        StringValues auth = headers.Authorization;

        if (auth.Count == 0)
        {
            return false;
        }

        if (auth.Count > 1)
        {
            return false;
        }

        string? raw = auth[0];

        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        if (!raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        headerValue = raw.AsSpan(7).Trim().ToString();

        return !string.IsNullOrEmpty(headerValue);
    }

    /// <summary>
    /// Returns a caller-owned copy of the digest to compare against, or null to fail closed. A miss
    /// joins (or starts) the one refresh for the generation it observed and then answers from
    /// whatever that refresh — or a concurrent rotation — left published. The request token only
    /// stops this request waiting; the shared read is bounded by the secret store itself.
    /// </summary>
    private async Task<byte[]?> GetExpectedDigestAsync(CancellationToken cancellationToken)
    {
        if (digestCache.TryGetDigest(
                out byte[]? cached,
                out long observedGeneration))
        {
            return cached;
        }

        Task refresh;

        lock (_refreshSync)
        {
            if (_failedRefresh is { } failed
                && failed.Generation == observedGeneration
                && _time.GetElapsedTime(failed.FailedAtTimestamp) < FailedReadRetryDelay)
            {
                // A read just failed for this generation: fail closed without re-entering storage.
                refresh = Task.CompletedTask;
            }
            else
            {
                if (_refresh is null
                    || _refresh.Generation != observedGeneration
                    || _refresh.Completion.IsCompleted)
                {
                    // Started off the lock: a store's peek may run synchronously before it yields.
                    _refresh = new PendingRefresh(
                        observedGeneration,
                        Task.Run(() => RefreshAsync(observedGeneration), CancellationToken.None));
                }

                refresh = _refresh.Completion;
            }
        }

        await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (digestCache.TryGetDigest(out byte[]? currentDigest))
        {
            return currentDigest;
        }

        // A locked keychain at startup was answered from the current mirror (DESIGN §11.2 item 4) and
        // peeks keep failing closed while it stays locked. Only then does the request path keep the
        // key this process adopted, through the retained digest that rotation or invalidation clears.
        return secretStore.ServesMasterApiKeyFromMirrorDuringOsFailure
            && digestCache.TryGetPresenceDigest(out byte[]? adoptedDigest)
                ? adoptedDigest
                : null;
    }

    /// <summary>
    /// The single secret-store read for one cache generation. Publishes only while that generation is
    /// still current, so a read that started before a rotation can never republish the old key, and
    /// remembers a failure so the next misses of the same generation do not repeat it.
    /// </summary>
    private async Task RefreshAsync(long generation)
    {
        // Something may have published since the miss (startup seed, a rotation's fill); then no
        // secret-store read is needed at all.
        if (digestCache.TryGetDigest(out byte[]? published, out _))
        {
            CryptographicOperations.ZeroMemory(published);

            return;
        }

        SecretStoreReadResult expectedRead;

        try
        {
            expectedRead = await secretStore
                .PeekApiKeyReadResultAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fail closed exactly like an unreadable store; the store logs its own fault.
            RecordFailedRefresh(generation);

            return;
        }

        if (expectedRead.Status != SecretStoreReadStatus.Ok
            || string.IsNullOrEmpty(expectedRead.Value))
        {
            RecordFailedRefresh(generation);

            return;
        }

        byte[] expectedUtf8 = Encoding.UTF8.GetBytes(expectedRead.Value);
        byte[] digest;

        try
        {
            digest = SHA256.HashData(expectedUtf8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedUtf8);
        }

        int ttlSeconds = ArcanumSettingClamps.ApiKeyCacheTtlSeconds(
            ArcanumRuntimeDefaults.SecurityApiKeyCacheTtlSeconds);

        try
        {
            // The cache takes its own copy on success; a lost race leaves the winner published.
            _ = digestCache.TryStoreDigest(
                digest,
                ttlSeconds,
                generation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private void RecordFailedRefresh(long generation)
    {
        lock (_refreshSync)
        {
            _failedRefresh = new FailedRefresh(generation, _time.GetTimestamp());
        }
    }

    private static void ZeroHeaderUtf8(Span<byte> headerUtf8, byte[]? rentedHeaderUtf8)
    {
        if (rentedHeaderUtf8 is not null)
        {
            CryptographicOperations.ZeroMemory(rentedHeaderUtf8);

            return;
        }

        CryptographicOperations.ZeroMemory(headerUtf8);
    }

    /// <summary>
    /// The single 401 both gates emit: <c>ApiResponse&lt;string&gt;</c> carrying <c>Auth.Unauthorized</c>
    /// (DESIGN §11.3), and under <c>/v1</c> the OpenAI error shape (<c>invalid_request_error</c> /
    /// <c>invalid_api_key</c>), because an OpenAI client reads <c>error.code</c> and every other failure on
    /// that surface already speaks it.
    /// </summary>
    public static IResult Unauthorized(HttpContext httpContext)
    {
        if (httpContext.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(
                new OpenAiErrorResponse(
                    new OpenAiErrorDetail(
                        "Invalid or missing API key.",
                        "invalid_request_error",
                        Param: null,
                        Code: "invalid_api_key")),
                ArcanumJsonContext.Default.OpenAiErrorResponse,
                statusCode: StatusCodes.Status401Unauthorized);
        }

        string? traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        ApiResponse<string> body = new(null, false, new Error(ErrorCodes.Auth.Unauthorized, "Invalid or missing API key."), traceId);

        return Results.Json(body, ArcanumJsonContext.Default.ApiResponseString, statusCode: StatusCodes.Status401Unauthorized);
    }
}
