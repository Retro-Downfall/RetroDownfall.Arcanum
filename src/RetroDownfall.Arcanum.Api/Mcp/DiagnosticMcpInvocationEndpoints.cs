using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Mcp;

/// <summary>
/// Diagnostic MCP Invocation endpoint — <c>POST /api/mcp/tools/invoke</c>. Available only in
/// <see cref="ArcanumEdition.Development"/>. Policy-constrained direct invocation of
/// <strong>external</strong> MCP tools by an operator. Not model execution; not unauthenticated
/// (inherits <c>X-Arcanum-Key</c>). Internal <c>arcanum-internal</c> server and all Forbidden Arts
/// are blocked. See <see cref="DiagnosticMcpInvocationService"/> for the full policy.
/// </summary>
internal static class DiagnosticMcpInvocationEndpoints
{

    internal static RouteGroupBuilder MapDiagnosticMcpInvocationEndpoints(this RouteGroupBuilder group)
    {

        _ = group.MapPost("/mcp/tools/invoke", HandleInvokeAsync)
            .WithName("PostDiagnosticMcpInvoke");

        return group;

    }

    private static async Task<IResult> HandleInvokeAsync(
        McpToolInvokeRequest body,
        DiagnosticMcpInvocationService service,
        IOptionsSnapshot<ArcanumSettings> settings,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        ArcanumEdition edition = ArcanumEnvironment.ResolveEdition(settings.Value.Edition);

        if (edition != ArcanumEdition.Development)
        {
            return Results.Json(
                ApiResponse<McpToolInvokeResponse>.FromResult(
                    Result<McpToolInvokeResponse>.Failure(new Error(
                        ErrorCodes.Mcp.DiagnosticDisabled,
                        "Diagnostic MCP invocation is available only when Arcanum:Edition=Development (or ARCANUM_EDITION=development).")),
                    traceId),
                ArcanumJsonContext.Default.ApiResponseMcpToolInvokeResponse,
                statusCode: StatusCodes.Status404NotFound);
        }

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync(body.ToolName, body.Arguments, body.ServerName, body.WorkingDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            int statusCode = ArcanumErrorMapper.ResolveStatusCode(result.Error.Code);

            return Results.Json(
                ApiResponse<McpToolInvokeResponse>.FromResult(
                    Result<McpToolInvokeResponse>.Failure(result.Error),
                    traceId),
                ArcanumJsonContext.Default.ApiResponseMcpToolInvokeResponse,
                statusCode: statusCode);

        }

        DiagnosticMcpInvocationOutcome outcome = result.Value;

        McpToolInvokeResponse response = new()
        {
            Result = outcome.Result,
            ServerName = outcome.ServerName,
            ToolName = outcome.ToolName,
            DurationMs = outcome.DurationMs,
            Truncated = outcome.Truncated,
        };

        return Results.Ok(ApiResponse<McpToolInvokeResponse>.FromResult(
            Result<McpToolInvokeResponse>.Success(response),
            traceId));

    }

}
