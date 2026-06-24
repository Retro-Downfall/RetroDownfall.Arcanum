using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Api.Mcp;

internal static class McpEndpoints
{

    public static RouteGroupBuilder MapMcpEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapPost("/mcp/reload", async (OptionalWorkspaceRequest? body, IMcpConnectionManager mcp, HttpContext httpContext, CancellationToken ct) =>
        {
            string workingDirectory = body?.WorkingDirectory ?? string.Empty;

            await mcp.ReloadAsync(workingDirectory, ct).ConfigureAwait(false);

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result<string> ok = Result<string>.Success("MCP partitions cleared; global re-bootstrapped.");

            return Results.Ok(ApiResponse<string>.FromResult(ok, traceId));
        })
        .WithName("PostMcpReload");

        apiGroup.MapGet("/mcp", async (IMcpConnectionManager manager, HttpContext httpContext, CancellationToken ct) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            McpServerInfo[] servers = await manager.GetAllStatusesAsync(ct).ConfigureAwait(false);

            Result<McpServerInfo[]> ok = Result<McpServerInfo[]>.Success(servers);

            return Results.Ok(ApiResponse<McpServerInfo[]>.FromResult(ok, traceId));
        })
        .WithName("GetMcpServers");

        apiGroup.MapGet("/mcp/{name}", async (string name, string? workingDirectory, IMcpConnectionManager manager, HttpContext httpContext, CancellationToken ct) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                McpServerInfo[] all = await manager.GetAllStatusesAsync(ct).ConfigureAwait(false);

                List<McpServerInfo> matches = all.Where(s => string.Equals(s.Name, name, StringComparison.Ordinal)).ToList();

                if (matches.Count > 1)
                {
                    Result<McpServerInfo> ambiguous = Result<McpServerInfo>.Failure(
                        new Error("Mcp.AmbiguousServer", $"Multiple MCP servers named '{name}' exist; specify workingDirectory."));

                    return Results.BadRequest(ApiResponse<McpServerInfo>.FromResult(ambiguous, traceId));
                }

                if (matches.Count == 0)
                {
                    Result<McpServerInfo> notFound = Result<McpServerInfo>.Failure(
                        new Error("Mcp.ServerNotFound", $"No MCP server named '{name}' was found."));

                    return Results.NotFound(ApiResponse<McpServerInfo>.FromResult(notFound, traceId));
                }

                Result<McpServerInfo> found = Result<McpServerInfo>.Success(matches[0]);

                return Results.Ok(ApiResponse<McpServerInfo>.FromResult(found, traceId));
            }

            McpServerInfo? server = await manager.GetStatusAsync(name, workingDirectory, ct).ConfigureAwait(false);

            if (server is null)
            {
                Result<McpServerInfo> notFound = Result<McpServerInfo>.Failure(
                    new Error("Mcp.ServerNotFound", $"No MCP server named '{name}' was found."));

                return Results.NotFound(ApiResponse<McpServerInfo>.FromResult(notFound, traceId));
            }

            Result<McpServerInfo> statusOk = Result<McpServerInfo>.Success(server);

            return Results.Ok(ApiResponse<McpServerInfo>.FromResult(statusOk, traceId));
        })
        .WithName("GetMcpServer");

        apiGroup.MapPost("/mcp/{name}/start", async (string name, string? workingDirectory, IMcpConnectionManager manager, HttpContext httpContext, CancellationToken ct) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result result = await manager.StartAsync(name, workingDirectory, httpContext.RequestAborted).ConfigureAwait(false);

            return result.IsSuccess
                ? Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId))
                : Results.BadRequest(ApiResponse<bool>.FromResult(Result<bool>.Failure(result.Error), traceId));
        })
        .WithName("StartMcpServer");

        apiGroup.MapPost("/mcp/{name}/stop", async (string name, string? workingDirectory, IMcpConnectionManager manager, HttpContext httpContext, CancellationToken ct) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result result = await manager.StopAsync(name, workingDirectory, httpContext.RequestAborted).ConfigureAwait(false);

            return result.IsSuccess
                ? Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId))
                : Results.BadRequest(ApiResponse<bool>.FromResult(Result<bool>.Failure(result.Error), traceId));
        })
        .WithName("StopMcpServer");

        apiGroup.MapPost("/mcp/{name}/restart", async (string name, string? workingDirectory, IMcpConnectionManager manager, HttpContext httpContext, CancellationToken ct) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result result = await manager.RestartAsync(name, workingDirectory, httpContext.RequestAborted).ConfigureAwait(false);

            return result.IsSuccess
                ? Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId))
                : Results.BadRequest(ApiResponse<bool>.FromResult(Result<bool>.Failure(result.Error), traceId));
        })
        .WithName("RestartMcpServer");

        apiGroup.MapPost("/mcp/trust-workspace", async (OptionalWorkspaceRequest? body, IMcpConnectionManager manager, HttpContext httpContext, CancellationToken ct) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            if (body is null || string.IsNullOrWhiteSpace(body.WorkingDirectory))
            {

                return Results.BadRequest(
                    ApiResponse<bool>.FromResult(
                        Result<bool>.Failure(new Error("Mcp.MissingWorkspace", "workingDirectory is required.")),
                        traceId));

            }

            Result result = await manager.TrustWorkspaceAsync(body.WorkingDirectory!, httpContext.RequestAborted).ConfigureAwait(false);

            return result.IsSuccess
                ? Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId))
                : Results.BadRequest(ApiResponse<bool>.FromResult(Result<bool>.Failure(result.Error), traceId));

        })
        .WithName("TrustMcpWorkspace");

        return apiGroup;
    }

}
