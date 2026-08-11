using System.Diagnostics;

using System.Text.Json;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Api.Streaming;

using RetroDownfall.Arcanum.Api.TheForge;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class WebWorkflowEndpoints
{

    /// <summary>
    /// What the operator sees when the research orchestration faults. Sanitized per DESIGN §11.9 —
    /// the exception's own text never reaches the wire.
    /// </summary>
    private const string PublicResearchFailureMessage =
        "The research stream failed unexpectedly. Check the host logs for details.";

    internal static void MapWebWorkflowEndpoints(
        this RouteGroupBuilder group)
    {

        _ = group.MapPost(
                "/web/search",
                HandleSearchAsync)
            .WithName("PostWebSearch");

        _ = group.MapPost(
                "/web/browse",
                HandleBrowseAsync)
            .WithName("PostWebBrowse");

        _ = group.MapPost(
                "/web/research",
                HandleResearchAsync)
            .WithName("PostWebResearch");

    }

    private static async Task<IResult> HandleSearchAsync(
        WebSearchWorkflowRequest? request,
        WebResearchWorkflowService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {

        if (request is null)
        {

            return InvalidBody<WebSearchWorkflowResult>(httpContext);

        }

        Result<WebSearchWorkflowResult> result = await service
            .SearchAsync(request, cancellationToken)
            .ConfigureAwait(false);

        string traceId = Activity.Current?.Id
            ?? httpContext.TraceIdentifier;

        ApiResponse<WebSearchWorkflowResult> response =
            ApiResponse<WebSearchWorkflowResult>.FromResult(
                result,
                traceId);

        return result.IsSuccess
            ? Results.Ok(response)
            : Results.Json(
                response,
                ArcanumJsonContext.Default.ApiResponseWebSearchWorkflowResult,
                statusCode: ArcanumErrorMapper.ResolveStatusCode(
                    result.Error.Code));

    }

    private static async Task<IResult> HandleBrowseAsync(
        WebBrowseWorkflowRequest? request,
        WebResearchWorkflowService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {

        if (request is null)
        {

            return InvalidBody<WebBrowseWorkflowResult>(httpContext);

        }

        Result<WebBrowseWorkflowResult> result = await service
            .BrowseAsync(request, cancellationToken)
            .ConfigureAwait(false);

        string traceId = Activity.Current?.Id
            ?? httpContext.TraceIdentifier;

        ApiResponse<WebBrowseWorkflowResult> response =
            ApiResponse<WebBrowseWorkflowResult>.FromResult(
                result,
                traceId);

        return result.IsSuccess
            ? Results.Ok(response)
            : Results.Json(
                response,
                ArcanumJsonContext.Default.ApiResponseWebBrowseWorkflowResult,
                statusCode: ArcanumErrorMapper.ResolveStatusCode(
                    result.Error.Code));

    }

    private static async Task HandleResearchAsync(
        WebResearchWorkflowRequest? request,
        WebResearchWorkflowService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {

        httpContext.Response.ContentType = "application/x-ndjson";

        httpContext.Response.Headers.CacheControl = "no-cache";

        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        WebResearchWorkflowRequest effective = request ?? new();

        // Once the first frame is flushed the response has started, and ArcanumExceptionHandler can
        // no longer produce an envelope — it logs and returns false, aborting the chunked body. A
        // caller would see a stream that simply stops, with the billable search and synthesis passes
        // already paid for. Every exit therefore lands on a frame.
        try
        {

            await foreach (WebResearchStreamFrame frame in service
                .ResearchAsync(effective, cancellationToken)
                .ConfigureAwait(false))
            {

                await WriteFrameAsync(httpContext, frame, cancellationToken).ConfigureAwait(false);

            }

        }
        catch (Exception exception) when (ClientDisconnect.IsClientDisconnect(exception, httpContext))
        {

            // The socket is gone; there is nowhere left to write a terminal frame.

        }
        catch (OperationCanceledException)
        {

            // Cooperative cancellation that did not abort the request. Nothing to report.

        }
        catch (Exception exception)
        {

            ResolveLogger(httpContext).LogError(
                exception,
                "Web research stream failed after {TraceId} started; emitting a terminal error frame.",
                httpContext.TraceIdentifier);

            try
            {

                await WriteFrameAsync(
                        httpContext,
                        new WebResearchStreamFrame
                        {

                            Type = WebResearchStreamFrameType.Error,

                            Code = ErrorCodes.Hub.Error,

                            Message = PublicResearchFailureMessage,

                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);

            }
            catch (Exception writeException)
                when (ClientDisconnect.IsClientDisconnect(writeException, httpContext))
            {

            }

        }

    }

    private static async Task WriteFrameAsync(
        HttpContext httpContext,
        WebResearchStreamFrame frame,
        CancellationToken cancellationToken)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            frame,
            ArcanumJsonContext.Default.WebResearchStreamFrame);

        await httpContext.Response.Body
            .WriteAsync(json, cancellationToken)
            .ConfigureAwait(false);

        await httpContext.Response.Body
            .WriteAsync("\n"u8.ToArray(), cancellationToken)
            .ConfigureAwait(false);

        await httpContext.Response.Body
            .FlushAsync(cancellationToken)
            .ConfigureAwait(false);

    }

    private static ILogger ResolveLogger(HttpContext httpContext)
    {

        string category = typeof(WebWorkflowEndpoints).FullName ?? nameof(WebWorkflowEndpoints);

        return httpContext.RequestServices
                ?.GetService<ILoggerFactory>()
                ?.CreateLogger(category)
            ?? NullLoggerFactory.Instance.CreateLogger(category);

    }

    private static async Task WriteResearchFrameAsync(
        HttpContext httpContext,
        WebResearchStreamFrame frame,
        CancellationToken cancellationToken)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            frame,
            ArcanumJsonContext.Default.WebResearchStreamFrame);

        await httpContext.Response.Body
            .WriteAsync(json, cancellationToken)
            .ConfigureAwait(false);

        await httpContext.Response.Body
            .WriteAsync(NewLine, cancellationToken)
            .ConfigureAwait(false);

        await httpContext.Response.Body
            .FlushAsync(cancellationToken)
            .ConfigureAwait(false);

    }

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private static IResult InvalidBody<T>(HttpContext httpContext)
    {

        string traceId = Activity.Current?.Id
            ?? httpContext.TraceIdentifier;

        Result<T> result = Result<T>.Failure(
            new Error(
                ErrorCodes.Validation.InvalidBody,
                ApiRequestJson.DefaultInvalidBodyMessage));

        return Results.BadRequest(
            ApiResponse<T>.FromResult(result, traceId));

    }

}
