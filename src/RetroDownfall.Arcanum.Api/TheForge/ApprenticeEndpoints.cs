using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class ApprenticeEndpoints
{

    private static readonly byte[] SseDone = "data: [DONE]\n\n"u8.ToArray();

    public static RouteGroupBuilder MapApprenticeEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapGet(
            "/apprentices",
            async (
                Guid? campaignId,
                string? status,
                int? limit,
                DateTimeOffset? beforeUpdatedAt,
                IApprenticeRepository repo,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                ListPageResult<Apprentice> page = await repo
                    .ListAsync(campaignId, status, limit, beforeUpdatedAt, ctx.RequestAborted)
                    .ConfigureAwait(false);

                ApprenticeSummaryDto[] dtos = page.Items.Select(ApprenticeMapping.ToSummaryDto).ToArray();

                ListPageResult<ApprenticeSummaryDto> response = new(
                    dtos,
                    page.HasMore,
                    NextBeforeUpdatedAt: page.NextBeforeUpdatedAt);

                return Results.Ok(
                    ApiResponse<ListPageResult<ApprenticeSummaryDto>>.FromResult(
                        Result<ListPageResult<ApprenticeSummaryDto>>.Success(response),
                        traceId));
            })
        .WithName("ListApprentices");

        apiGroup.MapGet(
            "/apprentices/{id:guid}",
            async (Guid id, IApprenticeRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Apprentice? apprentice = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (apprentice is null)
                {
                    return Results.Json(
                        ApiResponse<ApprenticeDetailDto>.FromResult(
                            Result<ApprenticeDetailDto>.Failure(new Error("Apprentice.NotFound", "Apprentice was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Ok(
                    ApiResponse<ApprenticeDetailDto>.FromResult(
                        Result<ApprenticeDetailDto>.Success(ApprenticeMapping.ToDetailDto(apprentice)),
                        traceId));
            })
        .WithName("GetApprentice");

        apiGroup.MapPost(
            "/apprentices",
            async (
                CreateApprenticeRequest request,
                IApprenticeRepository repo,
                ICampaignRepository campaignRepo,
                IOptionsSnapshot<ArcanumSettings> settings,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest(
                        ApiResponse<ApprenticeDetailDto>.FromResult(
                            Result<ApprenticeDetailDto>.Failure(new Error("Apprentice.InvalidName", "Apprentice name is required.")),
                            traceId));
                }

                if (string.IsNullOrWhiteSpace(request.Goal))
                {
                    return Results.BadRequest(
                        ApiResponse<ApprenticeDetailDto>.FromResult(
                            Result<ApprenticeDetailDto>.Failure(new Error("Apprentice.InvalidGoal", "Apprentice goal is required.")),
                            traceId));
                }

                Result<string> workspaceResult = await ResolveWorkspacePathAsync(
                    request,
                    campaignRepo,
                    settings.Value,
                    ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (workspaceResult.IsFailure)
                {
                    return MapApprenticeWorkspaceError(workspaceResult.Error, traceId);
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;

                Apprentice apprentice = new()
                {
                    Id = Guid.NewGuid(),
                    CampaignId = request.CampaignId,
                    Name = request.Name.Trim(),
                    Goal = request.Goal.Trim(),
                    Plan = "[]",
                    CurrentStep = 0,
                    Status = ApprenticeStatus.Idle.ToString(),
                    WorkspacePath = workspaceResult.Value!,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                await repo.AddAsync(apprentice, ctx.RequestAborted).ConfigureAwait(false);

                ApprenticeDetailDto dto = ApprenticeMapping.ToDetailDto(apprentice);

                return Results.Created(
                    $"/api/apprentices/{apprentice.Id}",
                    ApiResponse<ApprenticeDetailDto>.FromResult(Result<ApprenticeDetailDto>.Success(dto), traceId));
            })
        .WithName("CreateApprentice");

        apiGroup.MapDelete(
            "/apprentices/{id:guid}",
            async (Guid id, IApprenticeRepository repo, HttpContext ctx) =>
            {
                Apprentice? apprentice = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (apprentice is null)
                {
                    string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error("Apprentice.NotFound", "Apprentice was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (IsActiveStatus(apprentice.Status))
                {
                    string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<string>.FromResult(
                            Result<string>.Failure(new Error("Apprentice.Running", "Apprentice must be idle or in a terminal state before deletion.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseString,
                        statusCode: StatusCodes.Status409Conflict);
                }

                await repo.DeleteAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return Results.NoContent();
            })
        .WithName("DeleteApprentice");

        apiGroup.MapPost(
            "/apprentices/{id:guid}/start",
            async (Guid id, IApprenticeRuntime runtime, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string> result = await runtime.StartAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return MapRuntimeResult(result, traceId);
            })
        .WithName("StartApprentice");

        apiGroup.MapPost(
            "/apprentices/{id:guid}/pause",
            async (Guid id, IApprenticeRuntime runtime, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string> result = await runtime.PauseAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return MapRuntimeResult(result, traceId);
            })
        .WithName("PauseApprentice");

        apiGroup.MapPost(
            "/apprentices/{id:guid}/resume",
            async (Guid id, IApprenticeRuntime runtime, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string> result = await runtime.ResumeAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return MapRuntimeResult(result, traceId);
            })
        .WithName("ResumeApprentice");

        apiGroup.MapPost(
            "/apprentices/{id:guid}/cancel",
            async (Guid id, IApprenticeRuntime runtime, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string> result = await runtime.CancelAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return MapRuntimeResult(result, traceId);
            })
        .WithName("CancelApprentice");

        apiGroup.MapGet(
            "/apprentices/{id:guid}/chronicle",
            async (Guid id, IApprenticeRepository repo, IApprenticeRuntime runtime, HttpContext httpContext) =>
            {
                Apprentice? apprentice = await repo.GetByIdAsync(id, httpContext.RequestAborted).ConfigureAwait(false);

                if (apprentice is null)
                {
                    string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<ApprenticeDetailDto>.FromResult(
                            Result<ApprenticeDetailDto>.Failure(new Error("Apprentice.NotFound", "Apprentice was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                httpContext.Response.ContentType = "text/event-stream; charset=utf-8";

                httpContext.Response.Headers.CacheControl = "no-cache";

                httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

                ChronicleSseStreamWriter sseWriter = new(httpContext);

                // Subscribe before synthetic plan replay so chronicle events emitted during replay are not lost.
                Channel<ApprenticeEvent> liveBuffer = Channel.CreateUnbounded<ApprenticeEvent>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

                using CancellationTokenSource pumpCts = CancellationTokenSource.CreateLinkedTokenSource(
                    httpContext.RequestAborted);

                Task pumpTask = PumpChronicleLiveAsync(id, runtime, liveBuffer.Writer, pumpCts.Token);

                List<PlanStep> plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

                if (plan.Count > 0)
                {
                    await sseWriter.WriteEventAsync(
                        new ApprenticeEvent
                        {
                            Type = ApprenticeEventType.PlanGenerated,
                            ApprenticeId = id,
                            Timestamp = DateTimeOffset.UtcNow,
                            Plan = plan,
                        },
                        httpContext.RequestAborted).ConfigureAwait(false);
                }

                if (apprentice.CurrentStep < plan.Count
                    && string.Equals(plan[apprentice.CurrentStep].Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                {
                    PlanStep current = plan[apprentice.CurrentStep];

                    await sseWriter.WriteEventAsync(
                        new ApprenticeEvent
                        {
                            Type = ApprenticeEventType.StepStarted,
                            ApprenticeId = id,
                            Timestamp = DateTimeOffset.UtcNow,
                            StepIndex = current.Index,
                            Description = current.Description,
                        },
                        httpContext.RequestAborted).ConfigureAwait(false);
                }

                try
                {
                    while (liveBuffer.Reader.TryRead(out ApprenticeEvent? buffered) && buffered is not null)
                    {
                        await sseWriter.WriteEventAsync(buffered, httpContext.RequestAborted)
                            .ConfigureAwait(false);
                    }

                    await foreach (ApprenticeEvent ev in liveBuffer.Reader
                        .ReadAllAsync(httpContext.RequestAborted)
                        .ConfigureAwait(false))
                    {
                        await sseWriter.WriteEventAsync(ev, httpContext.RequestAborted)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        await httpContext.Response.Body.WriteAsync(SseDone, CancellationToken.None).ConfigureAwait(false);

                        await httpContext.Response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    await pumpCts.CancelAsync().ConfigureAwait(false);

                    try
                    {
                        await pumpTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                return Results.Empty;
            })
        .WithName("GetApprenticeChronicle");

        return apiGroup;
    }

    private static async Task PumpChronicleLiveAsync(
        Guid apprenticeId,
        IApprenticeRuntime runtime,
        ChannelWriter<ApprenticeEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ApprenticeEvent ev in runtime
                .SubscribeChronicleAsync(apprenticeId, cancellationToken)
                .ConfigureAwait(false))
            {
                await writer.WriteAsync(ev, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task<Result<string>> ResolveWorkspacePathAsync(
        CreateApprenticeRequest request,
        ICampaignRepository campaignRepo,
        ArcanumSettings settings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            return Core.Configuration.CampaignPathPolicy.ValidateAndNormalizePath(request.WorkspacePath, settings);
        }

        if (request.CampaignId is { } campaignId)
        {
            Campaign? campaign = await campaignRepo.GetByIdAsync(campaignId, cancellationToken).ConfigureAwait(false);

            if (campaign is not null)
            {
                return Result<string>.Success(campaign.Path);
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.Host?.Workspace))
        {
            return Core.Configuration.CampaignPathPolicy.ValidateAndNormalizePath(settings.Host.Workspace, settings);
        }

        return Core.Configuration.CampaignPathPolicy.ValidateAndNormalizePath(Directory.GetCurrentDirectory(), settings);
    }

    private static bool IsActiveStatus(string status) =>
        string.Equals(status, ApprenticeStatus.Planning.ToString(), StringComparison.Ordinal)
        || string.Equals(status, ApprenticeStatus.Running.ToString(), StringComparison.Ordinal)
        || string.Equals(status, ApprenticeStatus.Paused.ToString(), StringComparison.Ordinal);

    private static IResult MapRuntimeResult(Result<string> result, string traceId)
    {
        if (result.IsSuccess)
        {
            return Results.Accepted($"/api/apprentices/{result.Value}", ApiResponse<string>.FromResult(result, traceId));
        }

        return result.Error.Code switch
        {
            "Apprentice.NotFound" => Results.Json(
                ApiResponse<string>.FromResult(result, traceId),
                ArcanumJsonContext.Default.ApiResponseString,
                statusCode: StatusCodes.Status404NotFound),
            "Apprentice.AlreadyRunning" or "Apprentice.NotPaused" or "Apprentice.Running" or "Apprentice.MaxReached" =>
                Results.Json(
                    ApiResponse<string>.FromResult(result, traceId),
                    ArcanumJsonContext.Default.ApiResponseString,
                    statusCode: StatusCodes.Status409Conflict),
            _ => Results.BadRequest(ApiResponse<string>.FromResult(result, traceId)),
        };
    }

    private static IResult MapApprenticeWorkspaceError(Error error, string traceId)
    {
        if (error.Code == "Campaign.PathNotAllowed")
        {
            return Results.Json(
                ApiResponse<ApprenticeDetailDto>.FromResult(
                    Result<ApprenticeDetailDto>.Failure(new Error("Apprentice.InvalidWorkspace", error.Message)),
                    traceId),
                ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
                statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.BadRequest(
            ApiResponse<ApprenticeDetailDto>.FromResult(
                Result<ApprenticeDetailDto>.Failure(new Error("Apprentice.InvalidWorkspace", error.Message)),
                traceId));
    }

}
