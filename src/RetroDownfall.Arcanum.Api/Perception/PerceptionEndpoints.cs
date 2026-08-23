using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Core.Chronosync;
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
                        new Error(ErrorCodes.Perception.InvalidPath, "The specified directory could not be resolved."));

                    return Results.BadRequest(ApiResponse<PatternSnapshot>.FromResult(invalid, badTraceId));
                }

                // The allowed-roots check runs before any existence probe so a denied path cannot be used
                // as a filesystem existence oracle (403 for "exists" vs 400 for "missing").
                string[] allowedRoots = settings.Value.ResolvePerceptionRoots();

                Result<string> allowed = WorkspaceRootPolicy.EnforceAllowedRoots(
                    resolved,
                    allowedRoots,
                    ErrorCodes.Perception.PathNotAllowed,
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

                if (!Directory.Exists(resolved))
                {
                    string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<PatternSnapshot> invalid = Result<PatternSnapshot>.Failure(
                        new Error(ErrorCodes.Perception.InvalidPath, "The specified directory does not exist or is inaccessible."));

                    return Results.BadRequest(ApiResponse<PatternSnapshot>.FromResult(invalid, badTraceId));
                }

                PatternSnapshot snapshot = await eye.PerceivePatternAsync(resolved, cancellationToken).ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<PatternSnapshot> ok = Result<PatternSnapshot>.Success(snapshot);

                return Results.Ok(ApiResponse<PatternSnapshot>.FromResult(ok, traceId));
            })
        .WithName("GetPerceptionLook");

        apiGroup.MapPost(
            "/perception/chronosync",
            async (
                PatternSnapshot snapshot,
                IChronosyncEngine chronosync,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {

                Result validation = PatternSnapshotValidator.Validate(snapshot);

                if (validation.IsFailure)
                {

                    string invalidTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<ChronosyncReport> invalid =
                        Result<ChronosyncReport>.Failure(validation.Error);

                    return Results.Json(
                        ApiResponse<ChronosyncReport>.FromResult(invalid, invalidTraceId),
                        ArcanumJsonContext.Default.ApiResponseChronosyncReport,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(validation.Error.Code));

                }

                ChronosyncReport report = await chronosync
                    .AnalyzeAndSyncAsync(snapshot, cancellationToken)
                    .ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<ChronosyncReport> result =
                    Result<ChronosyncReport>.Success(report);

                return Results.Ok(
                    ApiResponse<ChronosyncReport>.FromResult(result, traceId));

            })
        .WithName("PostPerceptionChronosync")
        .AddEndpointFilter(
            IdempotencyEndpointFilters.ForBoundArgument(
                0,
                ArcanumJsonContext.Default.PatternSnapshot));

        return apiGroup;
    }

}
