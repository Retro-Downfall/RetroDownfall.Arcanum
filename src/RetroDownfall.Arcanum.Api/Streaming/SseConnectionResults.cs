using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Streaming;

internal static class SseConnectionResults
{

    public static IResult TooManyConnections(HttpContext httpContext)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        return Results.Json(
            ApiResponse<bool>.FromResult(
                Result<bool>.Failure(
                    new Error(
                        ErrorCodes.Api.TooManyConnections,
                        "The server has reached the maximum number of concurrent SSE connections.")),
                traceId),
            ArcanumJsonContext.Default.ApiResponseBoolean,
            statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Api.TooManyConnections));

    }

}
