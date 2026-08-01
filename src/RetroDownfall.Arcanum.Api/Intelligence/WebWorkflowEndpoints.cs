using System.Diagnostics;

using System.Text.Json;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Api.TheForge;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class WebWorkflowEndpoints
{

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

        WebResearchWorkflowRequest effective = request ?? new();

        await foreach (WebResearchStreamFrame frame in service
            .ResearchAsync(effective, cancellationToken)
            .ConfigureAwait(false))
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

    }

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
