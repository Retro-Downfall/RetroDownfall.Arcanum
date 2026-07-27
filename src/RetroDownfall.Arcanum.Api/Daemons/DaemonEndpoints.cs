using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Daemons;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Api.Daemons;

internal static class DaemonEndpoints
{
    public static RouteGroupBuilder MapDaemonEndpoints(this RouteGroupBuilder apiGroup)
    {

        RouteGroupBuilder unseenServant = apiGroup.MapGroup("/unseen-servant");

        MapUnseenServantJobRoutes(unseenServant, routeNamePrefix: "UnseenServant");

        apiGroup.MapGet(
            "/daemons",
            async (IDaemonRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                DaemonJobInfo[] jobs = await registry.GetAllAsync(ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(ApiResponse<DaemonJobInfo[]>.FromResult(Result<DaemonJobInfo[]>.Success(jobs), traceId));
            })
        .WithName("ListDaemons");

        apiGroup.MapGet(
            "/daemons/{id}",
            async (string id, IDaemonRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                DaemonJobInfo? job = await registry.GetAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return job is null
                    ? Results.Json(
                        ApiResponse<DaemonJobInfo>.FromResult(
                            Result<DaemonJobInfo>.Failure(new Error(ErrorCodes.Daemon.NotFound, "No daemon job exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseDaemonJobInfo,
                        statusCode: StatusCodes.Status404NotFound)
                    : Results.Ok(ApiResponse<DaemonJobInfo>.FromResult(Result<DaemonJobInfo>.Success(job), traceId));
            })
        .WithName("GetDaemon");

        apiGroup.MapPost(
            "/daemons/{id}/run",
            async (string id, IDaemonRunner runner, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<DaemonExecutionSummary> result = await runner
                    .RunAsync(id, force: true, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<DaemonExecutionSummary>.FromResult(result, traceId))
                    : Results.BadRequest(ApiResponse<DaemonExecutionSummary>.FromResult(
                        Result<DaemonExecutionSummary>.Failure(result.Error), traceId));
            })
        .WithName("RunDaemon");

        apiGroup.MapGet(
            "/daemons/{id}/history",
            async (string id, IDaemonExecutionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                DaemonExecutionSummary[] history = await repo
                    .GetHistoryAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Ok(ApiResponse<DaemonExecutionSummary[]>.FromResult(
                    Result<DaemonExecutionSummary[]>.Success(history), traceId));
            })
        .WithName("GetDaemonHistory");

        apiGroup.MapGet(
            "/executions/{id}",
            async (string id, IDaemonExecutionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                DaemonExecutionDetail? execution = await repo
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return execution is null
                    ? Results.Json(
                        ApiResponse<DaemonExecutionDetail>.FromResult(
                            Result<DaemonExecutionDetail>.Failure(new Error("Execution.NotFound", "No execution exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseDaemonExecutionDetail,
                        statusCode: StatusCodes.Status404NotFound)
                    : Results.Ok(ApiResponse<DaemonExecutionDetail>.FromResult(
                        Result<DaemonExecutionDetail>.Success(execution), traceId));
            })
        .WithName("GetExecution");

        apiGroup.MapPost(
            "/executions/{id}/cancel",
            async (string id, IDaemonExecutionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                try
                {
                    DaemonExecutionSummary summary = await repo
                        .CancelAsync(id, ctx.RequestAborted)
                        .ConfigureAwait(false);

                    return Results.Ok(ApiResponse<DaemonExecutionSummary>.FromResult(
                        Result<DaemonExecutionSummary>.Success(summary), traceId));
                }
                catch (InvalidOperationException)
                {
                    return Results.BadRequest(ApiResponse<DaemonExecutionSummary>.FromResult(
                        Result<DaemonExecutionSummary>.Failure(
                            new Error("Daemon.NotRunning", "Execution is not running or does not exist.")),
                        traceId));
                }
            })
        .WithName("CancelExecution");

        apiGroup.MapPost(
            "/commlink/send",
            async (
                HttpContext httpContext,
                ICommLinkDispatcher commLink,
                CancellationToken cancellationToken) =>
            {
                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                CommLinkMessageRequestDto? body;

                IResult? jsonError;

                (body, jsonError) = await ApiRequestJson.ReadAsync(
                    httpContext,
                    ArcanumJsonContext.Default.CommLinkMessageRequestDto,
                    static ctx => ApiRequestJson.InvalidBodyResult(
                        ctx,
                        ApiRequestJson.MalformedJsonMessage),
                    cancellationToken).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (body is null)
                {
                    Result<bool> invalid = Result<bool>.Failure(
                        new Error(ErrorCodes.Validation.InvalidBody, "Request body is required."));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
                }

                if (string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Body))
                {
                    Result<bool> invalid = Result<bool>.Failure(
                        new Error("Validation.InvalidFields", "Title and body must not be empty."));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
                }

                string title = body.Title.Trim();

                string bodyText = body.Body.Trim();

                string source = string.IsNullOrWhiteSpace(body.Source) ? "api" : body.Source.Trim();

                CommLinkMessage message = new(title, bodyText, body.Severity, source);

                Result<CommLinkDeliveryResult> dispatch = await commLink
                    .DispatchAsync(message, cancellationToken)
                    .ConfigureAwait(false);

                if (dispatch.IsFailure)
                {
                    Result<bool> failed = Result<bool>.Failure(dispatch.Error);

                    return Results.Json(
                        ApiResponse<bool>.FromResult(failed, traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status502BadGateway);
                }

                // Delivered and Suppressed are both successful outcomes from the API's perspective.
                Result<bool> ok = Result<bool>.Success(true);

                return Results.Ok(ApiResponse<bool>.FromResult(ok, traceId));
            })
        .WithName("PostCommLinkSend");

        return apiGroup;
    }

    private static UnseenServantJobStatusDto ToUnseenServantJobStatusDto(
        UnseenServantJob job,
        IUnseenServantPacer pacer,
        IUnseenServantJobTracker tracker)
    {

        int effectiveInterval = pacer.GetEffectiveInterval(job);

        return new UnseenServantJobStatusDto(
            job.Name,
            job.TargetSpell,
            job.IntervalMinutes,
            effectiveInterval,
            job.Enabled,
            tracker.GetLastRunAt(job),
            tracker.GetNextDueAt(job, effectiveInterval),
            tracker.GetLastResult(job));

    }

    private static void MapUnseenServantJobRoutes(RouteGroupBuilder group, string routeNamePrefix)
    {

        group.MapGet(
            "/jobs",
            (IOptionsMonitor<ArcanumSettings> settings, IUnseenServantPacer pacer, IUnseenServantJobTracker tracker, HttpContext httpContext) =>
            {
                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                UnseenServantJobStatusDto[] dtos = (settings.CurrentValue.Daemon?.Jobs ?? [])
                    .Select(job => ToUnseenServantJobStatusDto(job, pacer, tracker))
                    .ToArray();

                Result<UnseenServantJobStatusDto[]> ok = Result<UnseenServantJobStatusDto[]>.Success(dtos);

                return Results.Ok(ApiResponse<UnseenServantJobStatusDto[]>.FromResult(ok, traceId));
            })
        .WithName($"{routeNamePrefix}GetDaemonJobs");

        group.MapPost(
            "/jobs/{name}/initiative",
            async (
                string name,
                HttpContext httpContext,
                IUnseenServantPacer pacer,
                IUnseenServantJobTracker tracker,
                IOptionsMonitor<ArcanumSettings> settings,
                CancellationToken cancellationToken) =>
            {
                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                AdjustInitiativeRequestDto? body;

                IResult? jsonError;

                (body, jsonError) = await ApiRequestJson.ReadAsync(
                    httpContext,
                    ArcanumJsonContext.Default.AdjustInitiativeRequestDto,
                    static ctx => ApiRequestJson.InvalidBodyResult<UnseenServantJobStatusDto>(
                        ctx,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseUnseenServantJobStatusDto),
                    cancellationToken).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (body is null)
                {
                    Result<UnseenServantJobStatusDto> invalid = Result<UnseenServantJobStatusDto>.Failure(
                        new Error(ErrorCodes.Validation.InvalidBody, "Request body is required."));

                    return Results.BadRequest(ApiResponse<UnseenServantJobStatusDto>.FromResult(invalid, traceId));
                }

                string trimmedName = name.Trim();

                if (trimmedName.Length == 0)
                {
                    Result<UnseenServantJobStatusDto> invalid = Result<UnseenServantJobStatusDto>.Failure(
                        new Error("Validation.InvalidJobName", "Job name must not be empty."));

                    return Results.BadRequest(ApiResponse<UnseenServantJobStatusDto>.FromResult(invalid, traceId));
                }

                pacer.SetDynamicInterval(trimmedName, body.IntervalMinutes);

                UnseenServantJob? configured = (settings.CurrentValue.Daemon?.Jobs ?? []).FirstOrDefault(
                    job => string.Equals(job.Name.Trim(), trimmedName, StringComparison.Ordinal));

                UnseenServantJob jobForInterval = configured
                    ?? new UnseenServantJob
                    {
                        Name = trimmedName,
                        TargetSpell = string.Empty,
                        IntervalMinutes = 60,
                        Enabled = false,
                    };

                UnseenServantJobStatusDto dto = ToUnseenServantJobStatusDto(jobForInterval, pacer, tracker);

                Result<UnseenServantJobStatusDto> ok = Result<UnseenServantJobStatusDto>.Success(dto);

                return Results.Ok(ApiResponse<UnseenServantJobStatusDto>.FromResult(ok, traceId));
            })
        .WithName($"{routeNamePrefix}PostDaemonJobInitiative");
    }
}
