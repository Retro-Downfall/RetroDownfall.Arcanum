using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
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
public sealed class ApiKeyAuthenticator(
    ISecretStore secretStore,
    IApiKeyDigestCache digestCache)
{
    private const int Sha256Bytes = 32;

    public async ValueTask<bool> IsAuthorizedAsync(HttpContext httpContext)
    {
        int maxHeaderUtf16 = ArcanumSettingClamps.MaxApiKeyHeaderUtf16Chars(
            ArcanumRuntimeDefaults.SecurityMaxApiKeyHeaderUtf16Chars);

        IHeaderDictionary headers = httpContext.Request.Headers;

        if (!TryExtractHeaderValue(headers, out string? headerValue))
        {
            return false;
        }

        byte[]? expectedDigest = await GetExpectedDigestAsync().ConfigureAwait(false);

        if (expectedDigest is null)
        {
            return false;
        }

        if (headerValue.Length > maxHeaderUtf16)
        {
            return false;
        }

        int headerByteCount = Encoding.UTF8.GetByteCount(headerValue);

        byte[]? rentedHeaderUtf8 = null;

        Span<byte> headerUtf8 = headerByteCount <= 256
            ? stackalloc byte[headerByteCount]
            : (rentedHeaderUtf8 = new byte[headerByteCount]);

        Encoding.UTF8.GetBytes(headerValue, headerUtf8);

        Span<byte> headerDigest = stackalloc byte[Sha256Bytes];

        // The destination is exactly SHA-256's output size, so the throwing overload cannot fail and
        // needs no failure arm. TryHashData's false result is only reachable with an undersized span.
        _ = SHA256.HashData(headerUtf8, headerDigest);

        bool matched = CryptographicOperations.FixedTimeEquals(expectedDigest, headerDigest);

        ZeroHeaderUtf8(headerUtf8, rentedHeaderUtf8);

        return matched;
    }

    /// <summary>
    /// Extracts the presented credential from <c>X-Arcanum-Api-Key</c> or a <c>Bearer</c>
    /// <c>Authorization</c> header.
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

    private async Task<byte[]?> GetExpectedDigestAsync()
    {

        if (digestCache.TryGetDigest(out byte[]? cached))
        {

            return cached;

        }

        string? expected = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (expected is null)
        {

            return null;

        }

        byte[] expectedUtf8 = Encoding.UTF8.GetBytes(expected);

        byte[] digest = SHA256.HashData(expectedUtf8);

        CryptographicOperations.ZeroMemory(expectedUtf8);

        int ttlSeconds = ArcanumSettingClamps.ApiKeyCacheTtlSeconds(
            ArcanumRuntimeDefaults.SecurityApiKeyCacheTtlSeconds);

        digestCache.StoreDigest(digest, ttlSeconds);

        return digest;

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
    /// The single 401 shape both gates emit: <c>ApiResponse&lt;string&gt;</c> carrying
    /// <c>Auth.Unauthorized</c> (DESIGN §11.3).
    /// </summary>
    public static IResult Unauthorized(HttpContext httpContext)
    {
        string? traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        ApiResponse<string> body = new(null, false, new Error("Auth.Unauthorized", "Invalid or missing API key."), traceId);

        return Results.Json(body, ArcanumJsonContext.Default.ApiResponseString, statusCode: StatusCodes.Status401Unauthorized);
    }
}
