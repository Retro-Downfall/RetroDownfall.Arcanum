using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Perception;

internal static class PerceptionEndpoints
{

    public static RouteGroupBuilder MapPerceptionEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet(
            "/perception/look",
            async (
                string? directory,
                IEyeOfTheWorld eye,
                IOptionsSnapshot<ArcanumSettings> settings,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                string path = string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory;

                string resolved;

                try
                {
                    resolved = Path.GetFullPath(path);
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<PatternSnapshot> invalid = Result<PatternSnapshot>.Failure(
                        new Error("Perception.InvalidPath", "The specified directory could not be resolved."));

                    return Results.BadRequest(ApiResponse<PatternSnapshot>.FromResult(invalid, badTraceId));
                }

                if (!Directory.Exists(resolved))
                {
                    string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<PatternSnapshot> invalid = Result<PatternSnapshot>.Failure(
                        new Error("Perception.InvalidPath", "The specified directory does not exist or is inaccessible."));

                    return Results.BadRequest(ApiResponse<PatternSnapshot>.FromResult(invalid, badTraceId));
                }

                string[] allowedRoots = settings.Value.ResolvePerceptionRoots();

                Result<string> allowed = WorkspaceRootPolicy.EnforceAllowedRoots(
                    resolved,
                    allowedRoots,
                    "Perception.PathNotAllowed",
                    "The specified directory is outside Arcanum:Security:PerceptionWorkspaceRoots.");

                if (allowed.IsFailure)
                {
                    string deniedTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<PatternSnapshot>.FromResult(
                            Result<PatternSnapshot>.Failure(allowed.Error),
                            deniedTraceId),
                        ArcanumJsonContext.Default.ApiResponsePatternSnapshot,
                        statusCode: StatusCodes.Status403Forbidden);
                }

                PatternSnapshot snapshot = await eye.PerceivePatternAsync(resolved, cancellationToken).ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<PatternSnapshot> ok = Result<PatternSnapshot>.Success(snapshot);

                return Results.Ok(ApiResponse<PatternSnapshot>.FromResult(ok, traceId));
            })
        .WithName("GetPerceptionLook");

        return apiGroup;
    }

}
