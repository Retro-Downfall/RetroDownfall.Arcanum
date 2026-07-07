using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

/// <summary>
/// Diagnostic endpoint for directly invoking built-in tools by name.
/// </summary>
internal static class ToolInvokeEndpoints
{

    internal static void MapToolInvokeEndpoints(this RouteGroupBuilder group)
    {
        _ = group.MapPost("/tools/invoke", HandleInvokeAsync)
            .WithName("PostToolInvoke");
    }

    private static async Task<IResult> HandleInvokeAsync(
        ToolInvokeRequest body,
        IBuiltInToolRegistry registry,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (string.IsNullOrWhiteSpace(body.ToolName))
        {
            Result<ToolInvokeResponse> invalid = Result<ToolInvokeResponse>.Failure(
                new Error(ErrorCodes.Validation.InvalidBody, "'toolName' is required."));

            return Results.Json(
                ApiResponse<ToolInvokeResponse>.FromResult(invalid, traceId),
                ArcanumJsonContext.Default.ApiResponseToolInvokeResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        Result<JsonElement> result = await registry
            .InvokeAsync(body.ToolName, body.Arguments, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            int statusCode = ArcanumErrorMapper.ResolveStatusCode(result.Error.Code);

            return Results.Json(
                ApiResponse<ToolInvokeResponse>.FromResult(
                    Result<ToolInvokeResponse>.Failure(result.Error),
                    traceId),
                ArcanumJsonContext.Default.ApiResponseToolInvokeResponse,
                statusCode: statusCode);
        }

        ToolInvokeResponse response = new() { Result = result.Value };

        Result<ToolInvokeResponse> ok = Result<ToolInvokeResponse>.Success(response);

        return Results.Ok(ApiResponse<ToolInvokeResponse>.FromResult(ok, traceId));
    }

}
