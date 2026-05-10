using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Api.Security;

public sealed class ApiKeyEndpointFilter(ISecretStore secretStore, IOptionsMonitor<ArcanumSettings> arcOptions) : IEndpointFilter
{
    private byte[]? _cachedExpectedUtf8;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        int maxHeaderUtf16 = ArcanumSettingClamps.MaxApiKeyHeaderUtf16Chars(
            arcOptions.CurrentValue.Security.MaxApiKeyHeaderUtf16Chars);

        IHeaderDictionary headers = context.HttpContext.Request.Headers;

        string? headerValue = null;

        if (headers.TryGetValue(ArcanumApiHeaders.ApiKey, out StringValues apiKeyHeader) && apiKeyHeader.Count > 0)
        {
            headerValue = apiKeyHeader[0];
        }
        else if (headers.Authorization.Count > 0)
        {
            string? auth = headers.Authorization.ToString();

            if (!string.IsNullOrEmpty(auth)
                && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                headerValue = auth.AsSpan(7).Trim().ToString();
            }
        }

        byte[]? expectedUtf8 = Volatile.Read(ref _cachedExpectedUtf8);

        if (expectedUtf8 is null)
        {
            string? expected = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

            if (expected is null)
            {
                return Unauthorized(context.HttpContext);
            }

            expectedUtf8 = Encoding.UTF8.GetBytes(expected);

            Volatile.Write(ref _cachedExpectedUtf8, expectedUtf8);
        }

        if (string.IsNullOrEmpty(headerValue))
        {
            return Unauthorized(context.HttpContext);
        }

        if (headerValue.Length > maxHeaderUtf16)
        {
            return Unauthorized(context.HttpContext);
        }

        int headerByteCount = Encoding.UTF8.GetByteCount(headerValue);

        Span<byte> headerUtf8 = headerByteCount <= 256
            ? stackalloc byte[headerByteCount]
            : new byte[headerByteCount];

        Encoding.UTF8.GetBytes(headerValue, headerUtf8);

        if (!CryptographicOperations.FixedTimeEquals(expectedUtf8, headerUtf8))
        {
            return Unauthorized(context.HttpContext);
        }

        return await next(context).ConfigureAwait(false);
    }

    private static IResult Unauthorized(HttpContext httpContext)
    {
        string? traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        ApiResponse<string> body = new(null, false, new Error("Unauthorized", "Invalid or missing API key."), traceId);

        return Results.Json(body, ArcanumJsonContext.Default.ApiResponseString, statusCode: StatusCodes.Status401Unauthorized);
    }
}
