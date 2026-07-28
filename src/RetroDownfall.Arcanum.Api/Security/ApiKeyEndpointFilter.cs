using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Api.Security;

public sealed class ApiKeyEndpointFilter(
    ISecretStore secretStore,
    IApiKeyDigestCache digestCache) : IEndpointFilter
{
    private const int Sha256Bytes = 32;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        int maxHeaderUtf16 = ArcanumSettingClamps.MaxApiKeyHeaderUtf16Chars(
            ArcanumRuntimeDefaults.SecurityMaxApiKeyHeaderUtf16Chars);

        IHeaderDictionary headers = context.HttpContext.Request.Headers;

        if (!TryExtractHeaderValue(headers, out string? headerValue))
        {
            return Unauthorized(context.HttpContext);
        }

        byte[]? expectedDigest = await GetExpectedDigestAsync().ConfigureAwait(false);

        if (expectedDigest is null)
        {
            return Unauthorized(context.HttpContext);
        }

        if (string.IsNullOrEmpty(headerValue) || headerValue.Length > maxHeaderUtf16)
        {
            return Unauthorized(context.HttpContext);
        }

        int headerByteCount = Encoding.UTF8.GetByteCount(headerValue);

        byte[]? rentedHeaderUtf8 = null;

        Span<byte> headerUtf8 = headerByteCount <= 256
            ? stackalloc byte[headerByteCount]
            : (rentedHeaderUtf8 = new byte[headerByteCount]);

        Encoding.UTF8.GetBytes(headerValue, headerUtf8);

        Span<byte> headerDigest = stackalloc byte[Sha256Bytes];

        if (!SHA256.TryHashData(headerUtf8, headerDigest, out int written) || written != Sha256Bytes)
        {

            ZeroHeaderUtf8(headerUtf8, rentedHeaderUtf8);

            return Unauthorized(context.HttpContext);

        }

        if (!CryptographicOperations.FixedTimeEquals(expectedDigest, headerDigest))
        {

            ZeroHeaderUtf8(headerUtf8, rentedHeaderUtf8);

            return Unauthorized(context.HttpContext);

        }

        ZeroHeaderUtf8(headerUtf8, rentedHeaderUtf8);

        return await next(context).ConfigureAwait(false);
    }

    private static bool TryExtractHeaderValue(IHeaderDictionary headers, out string? headerValue)
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

    private static IResult Unauthorized(HttpContext httpContext)
    {
        string? traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        ApiResponse<string> body = new(null, false, new Error("Auth.Unauthorized", "Invalid or missing API key."), traceId);

        return Results.Json(body, ArcanumJsonContext.Default.ApiResponseString, statusCode: StatusCodes.Status401Unauthorized);
    }
}
