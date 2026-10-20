using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;

public sealed record MemoryDelegationRequest(PingRequest Parent, MemoryDelegationGrant Grant);

internal static class MemoryDelegationRequestJson
{
    // Matches the ordinary native parent transport ceiling, including attachments and the bounded grant.
    internal const int RequestBytes = 16 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static readonly ArcanumJsonContext Closed = new(new JsonSerializerOptions(ArcanumJsonContext.Default.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    });

    internal static async Task<(MemoryDelegationRequest? Body, IResult? Error)> ReadAsync(HttpContext http)
    {
        CovenantRequestFeatures.MarkProtectedResponse(http);

        if (!http.Request.HasJsonContentType()
            || !MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var media)
            || media.Charset.HasValue && !media.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            || http.Request.ContentLength > RequestBytes) return (null, Invalid());

        try
        {
            using MemoryStream buffer = new();

            byte[] chunk = new byte[16 * 1024];

            while (buffer.Length <= RequestBytes)
            {
                int read = await http.Request.Body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, RequestBytes + 1L - buffer.Length)), http.RequestAborted).ConfigureAwait(false);

                if (read == 0) break;

                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length > RequestBytes) return (null, Invalid());

            ReadOnlySpan<byte> bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);

            _ = Utf8.GetCharCount(bytes);

            var body = JsonSerializer.Deserialize(bytes, Closed.MemoryDelegationRequest);

            return body is null ? (null, Invalid()) : (body, null);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or NotSupportedException or ArgumentException or IOException or BadHttpRequestException)
        {
            return (null, Invalid());
        }
    }

    private static IResult Invalid() => MemoryDelegationEndpoints.Failure(new(ErrorCodes.MemoryDelegation.InvalidGrant, "A bounded strict UTF-8 delegation request is required."));
}
