using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Hosting;

/// <summary>
/// Operator-initiated host shutdown, so <c>arcanum serve quit</c> stays a thin client over the same
/// HTTP API as every other CLI verb instead of signalling a process id.
/// </summary>
internal static class ServerLifecycleEndpoints
{

    public static RouteGroupBuilder MapServerLifecycleEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapPost("/server/quit", (
            IHostApplicationLifetime lifetime,
            HttpContext httpContext) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            // Accept before stopping: StopApplication tears down Kestrel, so the response must be
            // committed first or the caller sees a dropped connection instead of an acknowledgement.
            httpContext.Response.OnCompleted(() =>
            {
                lifetime.StopApplication();

                return Task.CompletedTask;
            });

            return Results.Accepted(
                value: ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId));

        })
        .WithName("QuitServer");

        return apiGroup;

    }

}
