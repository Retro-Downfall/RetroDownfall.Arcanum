using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Api.TheForge;

[ExcludeFromCodeCoverage] // Reason: apprentice SSE streaming HTTP endpoints; covered via ApprenticeEndpointTests integration smoke.
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
                            Result<ApprenticeDetailDto>.Failure(new Error(ErrorCodes.Apprentice.NotFound, "Apprentice was not found.")),
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
                CreateApprenticeRequest? request,
                IApprenticeRepository repo,
                ICampaignRepository campaignRepo,
                IOptionsSnapshot<ArcanumSettings> settings,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {

                    return Results.BadRequest(
                        ApiResponse<ApprenticeDetailDto>.FromResult(
                            Result<ApprenticeDetailDto>.Failure(
                                new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage)),
                            traceId));

                }

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
                            Result<ApprenticeDetailDto>.Failure(new Error(ErrorCodes.Apprentice.InvalidGoal, "Apprentice goal is required.")),
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
                            Result<bool>.Failure(new Error(ErrorCodes.Apprentice.NotFound, "Apprentice was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (IsActiveStatus(apprentice.Status))
                {
                    string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<string>.FromResult(
                            Result<string>.Failure(new Error(ErrorCodes.Apprentice.Running, "Apprentice must be idle or in a terminal state before deletion.")),
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

        apiGroup.MapPost(
            "/apprentices/{id:guid}/reweave",
            async (
                Guid id,
                ReweaveApprenticeRequest? request,
                IApprenticeRuntime runtime,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null || request.Steps is null || request.Steps.Count == 0)
                {

                    return Results.BadRequest(
                        ApiResponse<ApprenticeDetailDto>.FromResult(
                            Result<ApprenticeDetailDto>.Failure(
                                new Error(ErrorCodes.Apprentice.InvalidPlan, "Request body must include at least one plan step.")),
                            traceId));

                }

                Result<ApprenticeDetailDto> result = await runtime
                    .ReweaveAsync(id, request.Steps, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return MapReweaveResult(result, traceId);

            })
        .WithName("ReweaveApprentice");

        apiGroup.MapPost(
            "/apprentices/{id:guid}/intervene",
            async (
                Guid id,
                InterveneApprenticeRequest? request,
                IApprenticeRuntime runtime,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null || string.IsNullOrWhiteSpace(request.Guidance))
                {

                    return Results.BadRequest(
                        ApiResponse<string>.FromResult(
                            Result<string>.Failure(
                                new Error(ErrorCodes.Apprentice.InvalidGuidance, "Dungeon Master guidance is required.")),
                            traceId));

                }

                Result<string> result = await runtime
                    .InterveneAsync(id, request.Guidance, request.Resume, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return MapInterveneResult(result, traceId);

            })
        .WithName("InterveneApprentice");

        apiGroup.MapPost(
            "/apprentices/{id:guid}/cast",
            async (
                Guid id,
                CastApprenticeRequest? request,
                IApprenticeRepository repo,
                IConclaveArchmage archmage,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null || string.IsNullOrWhiteSpace(request.Goal))
                {

                    return Results.BadRequest(
                        ApiResponse<ApprenticeDetailDto>.FromResult(
                            Result<ApprenticeDetailDto>.Failure(
                                new Error(ErrorCodes.Apprentice.InvalidGoal, "Apprentice goal is required.")),
                            traceId));

                }

                Apprentice? parent = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (parent is null)
                {

                    return Results.Json(
                        ApiResponse<ApprenticeDetailDto>.FromResult(
                            Result<ApprenticeDetailDto>.Failure(
                                new Error(ErrorCodes.Apprentice.NotFound, "Apprentice was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
                        statusCode: StatusCodes.Status404NotFound);

                }

                Result<Apprentice> result = await archmage
                    .CastAsync(
                        new ConclaveCastRequest(
                            request.Goal,
                            request.Name,
                            parent.WorkspacePath,
                            parent.CampaignId,
                            parent.Id),
                        ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure)
                {

                    return MapCastResult(result.Error, traceId);

                }

                ApprenticeDetailDto dto = ApprenticeMapping.ToDetailDto(result.Value!);

                return Results.Created(
                    $"/api/apprentices/{result.Value!.Id}",
                    ApiResponse<ApprenticeDetailDto>.FromResult(Result<ApprenticeDetailDto>.Success(dto), traceId));

            })
        .WithName("CastApprentice");

        apiGroup.MapGet(
            "/apprentices/{id:guid}/chronicle",
            async (Guid id, IApprenticeRepository repo, IApprenticeRuntime runtime, SseConnectionGate sseGate, IOptionsMonitor<ArcanumSettings> settings, HttpContext httpContext) =>
            {
                Apprentice? apprentice = await repo.GetByIdAsync(id, httpContext.RequestAborted).ConfigureAwait(false);

                if (apprentice is null)
                {
                    string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<ApprenticeDetailDto>.FromResult(
                            Result<ApprenticeDetailDto>.Failure(new Error(ErrorCodes.Apprentice.NotFound, "Apprentice was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (!sseGate.TryAcquire(SseEventTypes.Chronicle, out SseConnectionLease? sseLease, out SseConnectionDenial denial))
                {

                    return SseConnectionResults.FromDenial(httpContext, denial);

                }

                using (sseLease)
                {

                SseStreamWriter.PrepareResponse(httpContext);

                ChronicleSseStreamWriter sseWriter = new(httpContext);

                // Subscribe before synthetic plan replay so chronicle events emitted during replay are not lost.
                int channelCapacity = ArcanumSettingClamps.EventBusChannelCapacity(
                    settings.CurrentValue.ResolveEventBus().ChannelCapacity);

                Channel<ApprenticeEvent> liveBuffer = Channel.CreateBounded<ApprenticeEvent>(
                    new BoundedChannelOptions(channelCapacity)
                    {
                        SingleReader = true,
                        SingleWriter = true,
                        FullMode = BoundedChannelFullMode.DropOldest,
                    });

                using CancellationTokenSource pumpCts = CancellationTokenSource.CreateLinkedTokenSource(
                    httpContext.RequestAborted);

                Task pumpTask = PumpChronicleLiveAsync(id, runtime, liveBuffer.Writer, pumpCts.Token);

                List<PlanStep> plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

                TimeSpan heartbeatInterval = TimeSpan.FromSeconds(
                    ArcanumSettingClamps.EventBusHeartbeatSeconds(
                        settings.CurrentValue.ResolveEventBus().HeartbeatSeconds));

                try
                {

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

                    if (ApprenticeExecutionPolicy.IsEscalatedStatus(apprentice.Status))
                    {

                        ApprenticeCheckpoint? checkpoint = ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData);

                        await sseWriter.WriteEventAsync(
                            new ApprenticeEvent
                            {
                                Type = ApprenticeEventType.ApprenticeEscalated,
                                ApprenticeId = id,
                                Timestamp = DateTimeOffset.UtcNow,
                                StepIndex = apprentice.CurrentStep < plan.Count ? plan[apprentice.CurrentStep].Index : apprentice.CurrentStep + 1,
                                Error = checkpoint?.EscalationReason ?? apprentice.ErrorMessage,
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

                    while (liveBuffer.Reader.TryRead(out ApprenticeEvent? buffered) && buffered is not null)
                    {

                        await sseWriter.WriteEventAsync(buffered, httpContext.RequestAborted)
                            .ConfigureAwait(false);

                    }

                    await SseStreamWriter.StreamAsync(
                        httpContext,
                        liveBuffer.Reader.ReadAllAsync(httpContext.RequestAborted),
                        (ev, ct) => sseWriter.WriteEventAsync(ev, ct),
                        heartbeatInterval,
                        httpContext.RequestAborted).ConfigureAwait(false);

                }
                catch (Exception ex) when (ClientDisconnect.IsClientDisconnect(ex, httpContext))
                {

                    // W3.4 Group A (S10): client disconnected during plan replay, buffered
                    // drain, or the live chronicle stream. Break silently — no DONE frame
                    // to a dead socket. The SseStreamWriter.StreamAsync path also handles
                    // disconnect internally; this catch covers the direct WriteEventAsync
                    // calls (plan replay / step-start / escalated) that precede it. The
                    // finally cancels the pump CTS so the chronicle producer stops promptly.

                }
                catch (OperationCanceledException)
                {

                    await SseStreamWriter.WriteDoneAsync(httpContext).ConfigureAwait(false);

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

                }

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

        string? defaultWorkspace = settings.ResolveDefaultWorkspace();

        if (!string.IsNullOrWhiteSpace(defaultWorkspace))
        {
            return Core.Configuration.CampaignPathPolicy.ValidateAndNormalizePath(
                defaultWorkspace,
                settings);
        }

        return Core.Configuration.CampaignPathPolicy.ValidateAndNormalizePath(Directory.GetCurrentDirectory(), settings);
    }

    private static bool IsActiveStatus(string status) =>
        string.Equals(status, ApprenticeStatus.Planning.ToString(), StringComparison.Ordinal)
        || string.Equals(status, ApprenticeStatus.Running.ToString(), StringComparison.Ordinal)
        || string.Equals(status, ApprenticeStatus.Paused.ToString(), StringComparison.Ordinal)
        || ApprenticeExecutionPolicy.IsEscalatedStatus(status);

    private static IResult MapReweaveResult(Result<ApprenticeDetailDto> result, string traceId)
    {

        if (result.IsSuccess)
        {

            return Results.Ok(ApiResponse<ApprenticeDetailDto>.FromResult(result, traceId));

        }

        return Results.Json(
            ApiResponse<ApprenticeDetailDto>.FromResult(result, traceId),
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
            statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));

    }

    private static IResult MapCastResult(Error error, string traceId)
    {

        return Results.Json(
            ApiResponse<ApprenticeDetailDto>.FromResult(
                Result<ApprenticeDetailDto>.Failure(error),
                traceId),
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
            statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(error.Code));

    }

    private static IResult MapInterveneResult(Result<string> result, string traceId)
    {

        if (result.IsSuccess)
        {

            return Results.Accepted($"/api/apprentices/{result.Value}", ApiResponse<string>.FromResult(result, traceId));

        }

        return Results.Json(
            ApiResponse<string>.FromResult(result, traceId),
            ArcanumJsonContext.Default.ApiResponseString,
            statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));

    }

    private static IResult MapRuntimeResult(Result<string> result, string traceId)
    {

        if (result.IsSuccess)
        {

            return Results.Accepted($"/api/apprentices/{result.Value}", ApiResponse<string>.FromResult(result, traceId));

        }

        return Results.Json(
            ApiResponse<string>.FromResult(result, traceId),
            ArcanumJsonContext.Default.ApiResponseString,
            statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));

    }

    private static IResult MapApprenticeWorkspaceError(Error error, string traceId)
    {

        int statusCode = string.Equals(error.Code, ErrorCodes.Campaign.PathNotAllowed, StringComparison.Ordinal)
            ? ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Campaign.PathNotAllowed)
            : ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(ErrorCodes.Apprentice.InvalidWorkspace);

        return Results.Json(
            ApiResponse<ApprenticeDetailDto>.FromResult(
                Result<ApprenticeDetailDto>.Failure(new Error(ErrorCodes.Apprentice.InvalidWorkspace, error.Message)),
                traceId),
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
            statusCode: statusCode);

    }

}
