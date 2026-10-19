using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

/// <summary>Feature-local raw-byte ceiling and strict UTF-8; never transcodes or logs input.</summary>
internal static class MemoryEvaluationRequestJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    // Separate generated metadata closes shared nested types only for these request bodies.
    // The shared CovenantDigest shape and ordinary API options remain unchanged.
    private static readonly ArcanumJsonContext ClosedRequests = new(new JsonSerializerOptions(ArcanumJsonContext.Default.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    });

    internal static JsonTypeInfo<MemoryEvaluationRequest> RunTypeInfo => ClosedRequests.MemoryEvaluationRequest;

    internal static JsonTypeInfo<MemoryEvaluationGetRequest> GetTypeInfo => ClosedRequests.MemoryEvaluationGetRequest;

    internal static JsonTypeInfo<MemoryEvaluationDeleteRequest> DeleteTypeInfo => ClosedRequests.MemoryEvaluationDeleteRequest;

    internal static async Task<(T? Body, IResult? Error)> ReadAsync<T>(HttpContext http, JsonTypeInfo<T> info)
    {
        CovenantRequestFeatures.MarkProtectedResponse(http);

        if (!http.Request.HasJsonContentType()
            || !MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var contentType)
            || contentType.Charset.HasValue && !contentType.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            || http.Request.ContentLength > MemoryEvaluationLimits.RequestBytes) return (default, Invalid());

        try
        {
            byte[] bytes = new byte[MemoryEvaluationLimits.RequestBytes + 1];

            int count = 0;

            while (count < bytes.Length)
            {
                int read = await http.Request.Body.ReadAsync(bytes.AsMemory(count), http.RequestAborted);

                if (read == 0) break;

                count += read;
            }

            if (count > MemoryEvaluationLimits.RequestBytes) return (default, Invalid());

            _ = Utf8.GetCharCount(bytes, 0, count);

            try
            {
                T? body = JsonSerializer.Deserialize(bytes.AsSpan(0, count), info);

                return body is null ? (default, Invalid()) : (body, null);
            }
            catch (ArgumentException)
            {
                // CovenantDigest's JSON constructor rejects null or non-32-byte input with
                // argument exceptions. Only request deserialization treats those as bad input.
                return (default, Invalid());
            }
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or NotSupportedException)
        { return (default, Invalid()); }
        catch (BadHttpRequestException error)
        { return (default, ApiRequestJson.UnreadableBodyResult(http, error)); }
        catch (IOException)
        { return (default, Invalid()); }
    }

    private static IResult Invalid() => MemoryEvaluationEndpoints.Failure(
        new Error(ErrorCodes.MemoryEvaluation.InvalidRequest, "A bounded strict UTF-8 evaluation request is required."), ArcanumJsonContext.Default.ApiResponseBoolean);
}
