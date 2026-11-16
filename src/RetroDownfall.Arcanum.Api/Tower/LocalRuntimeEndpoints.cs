using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Tower;

internal static class LocalRuntimeEndpoints
{
    public static RouteGroupBuilder MapLocalRuntimeEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapGet("/local-runtimes/{runtimeId}", async (
            string runtimeId,
            LocalRuntimeInspector inspector,
            IOptionsMonitor<ArcanumSettings> settings,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            Result<LocalRuntimeInspection> result = await inspector.InspectAsync(settings.CurrentValue, runtimeId, cancellationToken).ConfigureAwait(false);

            return Results.Json(
                ApiResponse<LocalRuntimeInspection>.FromResult(result, Activity.Current?.Id ?? context.TraceIdentifier),
                ArcanumJsonContext.Default.ApiResponseLocalRuntimeInspection,
                statusCode: result.IsSuccess ? StatusCodes.Status200OK : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));
        })
        .WithName("GetLocalRuntimeInspection")
        .WithMetadata(GrimoireAdmissionExemptRouteMetadata.Instance);

        return apiGroup;
    }
}
