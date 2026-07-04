using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Api.Streaming;

internal static class SseConnectionResults
{

    /// <summary>
    /// Maps an <see cref="SseConnectionDenial"/> from <see cref="SseConnectionGate.TryAcquire"/>
    /// to the appropriate 503 response — the global message for <see cref="SseDenialReason.Global"/>,
    /// the type-specific message for <see cref="SseDenialReason.PerType"/>.
    /// </summary>
    public static IResult FromDenial(HttpContext httpContext, SseConnectionDenial denial) =>
        denial.Reason == SseDenialReason.PerType
            ? TooManyConnections(httpContext, denial.EventType, denial.Limit)
            : TooManyConnections(httpContext);

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

    /// <summary>
    /// 503 response for a per-event-type SSE connection cap denial. Reuses
    /// <see cref="ErrorCodes.Api.TooManyConnections"/>; callers differentiate the global vs.
    /// per-type case by message only, per design.
    /// </summary>
    public static IResult TooManyConnections(HttpContext httpContext, string eventType, int limit)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        return Results.Json(
            ApiResponse<bool>.FromResult(
                Result<bool>.Failure(
                    new Error(
                        ErrorCodes.Api.TooManyConnections,
                        $"Too many connections for event type '{eventType}' (limit: {limit})")),
                traceId),
            ArcanumJsonContext.Default.ApiResponseBoolean,
            statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Api.TooManyConnections));

    }

}
