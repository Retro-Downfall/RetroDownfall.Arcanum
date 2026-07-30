using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class SessionEndpoints
{

    private static readonly byte[] SseDone = "data: [DONE]\n\n"u8.ToArray();

    private static readonly byte[] SseLiveSentinel = "data: {\"type\":\"live\"}\n\n"u8.ToArray();

    private static readonly byte[] SseDataPrefix = "data: "u8.ToArray();

    private static readonly byte[] SseLineBreak = "\n\n"u8.ToArray();

    public static RouteGroupBuilder MapSessionEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapPost(
            "/sessions",
            async (CreateSessionRequest? request, ISessionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {

                    return Results.BadRequest(
                        ApiResponse<SessionDetailDto>.FromResult(
                            Result<SessionDetailDto>.Failure(
                                new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage)),
                            traceId));

                }

                Session session = await repo
                    .CreateAsync(request.CampaignId, request.Title, ctx.RequestAborted)
                    .ConfigureAwait(false);

                int entryCount = 0;

                SessionDetailDto dto = SessionMapping.ToDetailDto(session, entryCount);

                return Results.Created(
                    $"/api/sessions/{session.Id:D}",
                    ApiResponse<SessionDetailDto>.FromResult(Result<SessionDetailDto>.Success(dto), traceId));
            })
        .WithName("CreateSession");

        apiGroup.MapGet(
            "/sessions/analytics",
            async (ISessionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SessionAnalytics analytics = await repo.GetAnalyticsAsync(ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SessionAnalytics>.FromResult(Result<SessionAnalytics>.Success(analytics), traceId));
            })
        .WithName("GetSessionAnalytics");

        apiGroup.MapGet(
            "/sessions",
            async (
                Guid? campaignId,
                string? status,
                string? search,
                string? title,
                MessageRole? role,
                string? model,
                DateTimeOffset? from,
                DateTimeOffset? to,
                int? limit,
                DateTimeOffset? beforeUpdatedAt,
                ISessionRepository repo,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SessionQueryRequest request = new(
                    campaignId,
                    status,
                    search,
                    title,
                    role,
                    model,
                    from,
                    to,
                    limit,
                    beforeUpdatedAt);

                SessionQueryResult result = await repo.QueryAsync(request, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SessionQueryResult>.FromResult(Result<SessionQueryResult>.Success(result), traceId));
            })
        .WithName("QuerySessions");

        apiGroup.MapGet(
            "/sessions/{id:guid}",
            async (Guid id, ISessionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Session? session = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (session is null)
                {
                    return Results.Json(
                        ApiResponse<SessionDetailDto>.FromResult(
                            Result<SessionDetailDto>.Failure(new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionDetailDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                int entryCount = await repo.GetEntryCountAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SessionDetailDto>.FromResult(
                        Result<SessionDetailDto>.Success(SessionMapping.ToDetailDto(session, entryCount)),
                        traceId));
            })
        .WithName("GetSession");

        apiGroup.MapGet(
            "/sessions/{id:guid}/entries",
            async (
                Guid id,
                int? offset,
                int? limit,
                DateTimeOffset? beforeCreatedAt,
                Guid? beforeId,
                bool? countOnly,
                ISessionRepository repo,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Session? session = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (session is null)
                {
                    return Results.Json(
                        ApiResponse<EntryDto[]>.FromResult(
                            Result<EntryDto[]>.Failure(new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseEntryDtoArray,
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (countOnly is true)
                {
                    int count = await repo.GetEntryCountAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                    return Results.Ok(
                        ApiResponse<SessionEntryCountDto>.FromResult(
                            Result<SessionEntryCountDto>.Success(new SessionEntryCountDto(count)),
                            traceId));
                }

                List<Entry> entries = await repo
                    .GetEntriesAsync(
                        id,
                        offset ?? 0,
                        limit ?? 100,
                        beforeCreatedAt,
                        beforeId,
                        ct: ctx.RequestAborted)
                    .ConfigureAwait(false);

                EntryDto[] dtos = entries.Select(SessionMapping.ToEntryDto).ToArray();

                return Results.Ok(
                    ApiResponse<EntryDto[]>.FromResult(Result<EntryDto[]>.Success(dtos), traceId));
            })
        .WithName("GetSessionEntries");

        apiGroup.MapGet(
            "/sessions/{id:guid}/attachments",
            async (
                Guid id,
                ISessionRepository repo,
                ISessionAttachmentStore store,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Session? session = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (session is null)
                {
                    return Results.Json(
                        ApiResponse<SessionAttachmentDto[]>.FromResult(
                            Result<SessionAttachmentDto[]>.Failure(
                                new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray,
                        statusCode: StatusCodes.Status404NotFound);
                }

                IReadOnlyList<SessionAttachmentRecord> bound = await store
                    .ListBoundAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                SessionAttachmentDto[] dtos = bound.Select(SessionMapping.ToAttachmentDto).ToArray();

                return Results.Ok(
                    ApiResponse<SessionAttachmentDto[]>.FromResult(
                        Result<SessionAttachmentDto[]>.Success(dtos),
                        traceId));
            })
        .WithName("GetSessionAttachments");

        apiGroup.MapPost(
            "/sessions/{id:guid}/entries",
            async (Guid id, AppendEntryRequest? request, ISessionRepository repo, SessionEventHub eventHub, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {

                    return Results.BadRequest(
                        ApiResponse<EntryDto>.FromResult(
                            Result<EntryDto>.Failure(
                                new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage)),
                            traceId));

                }

                if (string.IsNullOrWhiteSpace(request.Content))
                {
                    return Results.BadRequest(
                        ApiResponse<EntryDto>.FromResult(
                            Result<EntryDto>.Failure(
                                new Error(ErrorCodes.Session.EmptyContent, "Entry content is required.")),
                            traceId));
                }

                Entry entry = new()
                {
                    Id = Guid.NewGuid(),
                    Role = request.Role,
                    Content = request.Content.Trim(),
                    ModelUsed = request.ModelUsed ?? string.Empty,
                };

                Result<Entry> addResult = await repo.AddEntryAsync(id, entry, ctx.RequestAborted).ConfigureAwait(false);

                if (addResult.IsFailure)
                {

                    return Results.Json(
                        ApiResponse<EntryDto>.FromResult(
                            Result<EntryDto>.Failure(addResult.Error),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseEntryDto,
                        statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(addResult.Error.Code));

                }

                eventHub.Publish(id, addResult.Value);

                return Results.Ok(
                    ApiResponse<EntryDto>.FromResult(
                        Result<EntryDto>.Success(SessionMapping.ToEntryDto(addResult.Value)),
                        traceId));
            })
        .WithName("AppendSessionEntry");

        apiGroup.MapPatch(
            "/sessions/{id:guid}",
            async (Guid id, UpdateSessionRequest? request, ISessionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {

                    return Results.BadRequest(
                        ApiResponse<SessionDetailDto>.FromResult(
                            Result<SessionDetailDto>.Failure(
                                new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage)),
                            traceId));

                }

                Session? session = await GetSessionForUpdateAsync(repo, id, ctx.RequestAborted).ConfigureAwait(false);

                if (session is null)
                {
                    return Results.Json(
                        ApiResponse<SessionDetailDto>.FromResult(
                            Result<SessionDetailDto>.Failure(new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionDetailDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (request.Title is not null)
                {
                    session.Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
                }

                if (request.Status is not null)
                {
                    string status = request.Status.Trim();

                    if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(status, "archived", StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.BadRequest(
                            ApiResponse<SessionDetailDto>.FromResult(
                                Result<SessionDetailDto>.Failure(
                                    new Error(ErrorCodes.Session.InvalidStatus, "Status must be 'active' or 'archived'.")),
                                traceId));
                    }

                    session.Status = string.Equals(status, "archived", StringComparison.OrdinalIgnoreCase)
                        ? "archived"
                        : "active";
                }

                await repo.UpdateSessionAsync(session, ctx.RequestAborted).ConfigureAwait(false);

                int entryCount = await repo.GetEntryCountAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SessionDetailDto>.FromResult(
                        Result<SessionDetailDto>.Success(SessionMapping.ToDetailDto(session, entryCount)),
                        traceId));
            })
        .WithName("UpdateSession");

        apiGroup.MapDelete(
            "/sessions/{id:guid}",
            async (Guid id, ISessionRepository repo, HttpContext ctx) =>
            {
                Session? session = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (session is null)
                {
                    string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(ErrorCodes.Session.NotFound, "No session exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                await repo.ArchiveAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return Results.NoContent();
            })
        .WithName("ArchiveSession");

        apiGroup.MapGet(
            "/sessions/{id:guid}/export",
            async (Guid id, SessionExportFormat format, ISessionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<SessionExportResult> result = await repo
                    .ExportAsync(id, format, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure && result.Error.Code == ErrorCodes.Session.NotFound)
                {
                    return Results.Json(
                        ApiResponse<SessionExportResult>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionExportResult,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<SessionExportResult>.FromResult(result, traceId))
                    : Results.BadRequest(ApiResponse<SessionExportResult>.FromResult(result, traceId));
            })
        .WithName("ExportSession");

        apiGroup.MapPost(
            "/sessions/{id:guid}/fork",
            async (Guid id, ForkSessionRequest? request, ISessionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<Session> result = await repo
                    .ForkAsync(id, request ?? new ForkSessionRequest(), ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return Results.Json(
                        ApiResponse<SessionDetailDto>.FromResult(Result<SessionDetailDto>.Failure(result.Error), traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionDetailDto,
                        statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));
                }

                Session fork = result.Value;

                int entryCount = await repo.GetEntryCountAsync(fork.Id, ctx.RequestAborted).ConfigureAwait(false);

                SessionDetailDto dto = SessionMapping.ToDetailDto(fork, entryCount);

                return Results.Created(
                    $"/api/sessions/{fork.Id:D}",
                    ApiResponse<SessionDetailDto>.FromResult(Result<SessionDetailDto>.Success(dto), traceId));
            })
        .WithName("ForkSession")
        .WithLargeRequestBody();

        apiGroup.MapPost(
            "/sessions/{id:guid}/rest",
            async (Guid id, IGrimoireRepository grimoire, ICampaignLoggerQueue queue, HttpContext ctx) =>
            {
                if (!await grimoire.SessionExistsAsync(id, ctx.RequestAborted).ConfigureAwait(false))
                {
                    string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                    Result<bool> notFound = Result<bool>.Failure(
                        new Error(ErrorCodes.Session.NotFound, "No session exists with that id."));

                    return Results.NotFound(ApiResponse<bool>.FromResult(notFound, traceId));
                }

                string acceptedTraceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (!queue.TryQueue(id))
                {
                    Result<bool> rejected = Result<bool>.Failure(
                        new Error(
                            ErrorCodes.Session.RestQueueFull,
                            "Campaign Log consolidation could not be queued; the queue is full. Try again shortly."));

                    return Results.Json(
                        ApiResponse<bool>.FromResult(rejected, acceptedTraceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                Result<bool> queued = Result<bool>.Success(true);

                return Results.Json(
                    ApiResponse<bool>.FromResult(queued, acceptedTraceId),
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    statusCode: StatusCodes.Status202Accepted);
            })
        .WithName("PostSessionRest");

        apiGroup.MapGet(
            "/sessions/{id:guid}/stream",
            async (
                Guid id,
                Guid? since,
                ISessionRepository repo,
                SessionEventHub eventHub,
                SseConnectionGate sseGate,
                IOptionsMonitor<ArcanumSettings> options,
                HttpContext httpContext) =>
            {
                Session? session = await repo.GetByIdAsync(id, httpContext.RequestAborted).ConfigureAwait(false);

                if (session is null)
                {
                    string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<SessionDetailDto>.FromResult(
                            Result<SessionDetailDto>.Failure(new Error(ErrorCodes.Session.NotFound, "No session exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionDetailDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Entry? sinceEntry = null;

                if (since is Guid sinceEntryId)
                {
                    sinceEntry = await repo
                        .GetEntryAsync(id, sinceEntryId, httpContext.RequestAborted)
                        .ConfigureAwait(false);

                    if (sinceEntry is null)
                    {
                        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                        return Results.Json(
                            ApiResponse<EntryDto>.FromResult(
                                Result<EntryDto>.Failure(new Error(ErrorCodes.Session.EntryNotFound, "No entry exists with that id in this session.")),
                                traceId),
                            ArcanumJsonContext.Default.ApiResponseEntryDto,
                            statusCode: StatusCodes.Status404NotFound);
                    }
                }

                if (!sseGate.TryAcquire(SseEventTypes.Session, out SseConnectionLease? sseLease, out SseConnectionDenial denial))
                {

                    return SseConnectionResults.FromDenial(httpContext, denial);

                }

                using (sseLease)
                {

                SseStreamWriter.PrepareResponse(httpContext);

                int channelCapacity = ArcanumSettingClamps.EventBusChannelCapacity(
                    options.CurrentValue.ResolveEventBus().ChannelCapacity);

                Channel<Entry> liveBuffer = Channel.CreateBounded<Entry>(
                    new BoundedChannelOptions(channelCapacity)
                    {
                        SingleReader = true,
                        SingleWriter = true,
                        FullMode = BoundedChannelFullMode.DropOldest,
                    });

                using CancellationTokenSource pumpCts = CancellationTokenSource.CreateLinkedTokenSource(
                    httpContext.RequestAborted);

                Task pumpTask = PumpSessionLiveAsync(id, eventHub, liveBuffer.Writer, pumpCts.Token);

                HashSet<Guid> replayIds = [];

                SessionSettings sessionSettings = options.CurrentValue.ResolveSessions();

                int replayLimit = ArcanumSettingClamps.SessionStreamReplayLimit(
                    sessionSettings.MaxStreamReplayEntries);

                if (sinceEntry is not null)
                {
                    List<Entry> catchUp = await repo
                        .GetEntriesAfterAsync(id, sinceEntry.Sequence, replayLimit, httpContext.RequestAborted)
                        .ConfigureAwait(false);

                    foreach (Entry entry in catchUp)
                    {
                        replayIds.Add(entry.Id);

                        await WriteEntrySseAsync(httpContext, entry, httpContext.RequestAborted).ConfigureAwait(false);
                    }
                }
                else
                {
                    List<Entry> replay = await repo
                        .GetEntriesAscendingAsync(id, replayLimit, httpContext.RequestAborted)
                        .ConfigureAwait(false);

                    replayIds = replay.Select(e => e.Id).ToHashSet();

                    foreach (Entry entry in replay)
                    {
                        await WriteEntrySseAsync(httpContext, entry, httpContext.RequestAborted).ConfigureAwait(false);
                    }
                }

                await httpContext.Response.Body.WriteAsync(SseLiveSentinel, httpContext.RequestAborted).ConfigureAwait(false);

                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);

                TimeSpan heartbeatInterval = TimeSpan.FromSeconds(
                    ArcanumSettingClamps.EventBusHeartbeatSeconds(
                        options.CurrentValue.ResolveEventBus().HeartbeatSeconds));

                try
                {

                    while (liveBuffer.Reader.TryRead(out Entry? buffered) && buffered is not null)
                    {

                        if (!replayIds.Contains(buffered.Id))
                        {

                            await WriteEntrySseAsync(httpContext, buffered, httpContext.RequestAborted).ConfigureAwait(false);

                        }

                    }

                    await SseStreamWriter.StreamAsync(
                        httpContext,
                        liveBuffer.Reader.ReadAllAsync(httpContext.RequestAborted),
                        (entry, ct) => WriteEntrySseAsync(httpContext, entry, ct),
                        heartbeatInterval,
                        httpContext.RequestAborted).ConfigureAwait(false);

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
        .WithName("StreamSession");

        apiGroup.MapDelete(
            "/sessions/{id:guid}/entries/{entryId:guid}",
            async (Guid id, Guid entryId, IGrimoireRepository grimoire, IOptionsMonitor<ArcanumSettings> options, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SessionSettings sessionSettings = options.CurrentValue.ResolveSessions();

                if (!sessionSettings.AllowMemoryManagement)
                {
                    return Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(
                                new Error(ErrorCodes.Session.MemoryManagementDisabled, "Memory management is disabled.")),
                            traceId));
                }

                GrimoireEntryDto? entry = await grimoire
                    .GetEntryByIdAsync(id, entryId, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (entry is null)
                {
                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(ErrorCodes.Session.EntryNotFound, "Entry was not found in this session.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                await grimoire.DeleteEntryAsync(id, entryId, ctx.RequestAborted).ConfigureAwait(false);

                return Results.NoContent();
            })
        .WithName("DeleteSessionEntry");

        apiGroup.MapPost(
            "/sessions/{id:guid}/entries/{entryId:guid}/pin",
            async (Guid id, Guid entryId, IGrimoireRepository grimoire, IOptionsMonitor<ArcanumSettings> options, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SessionSettings sessionSettings = options.CurrentValue.ResolveSessions();

                if (!sessionSettings.AllowMemoryManagement)
                {
                    return Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(
                                new Error(ErrorCodes.Session.MemoryManagementDisabled, "Memory management is disabled.")),
                            traceId));
                }

                GrimoireEntryDto? entry = await grimoire
                    .GetEntryByIdAsync(id, entryId, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (entry is null)
                {
                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(ErrorCodes.Session.EntryNotFound, "Entry was not found in this session.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (!entry.IsPinned)
                {
                    int maxPinned = ArcanumSettingClamps.SessionMaxPinnedEntries(sessionSettings.MaxPinnedEntries);

                    int currentPinned = await grimoire.GetPinnedEntryCountAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                    if (currentPinned >= maxPinned)
                    {
                        return Results.Json(
                            ApiResponse<bool>.FromResult(
                                Result<bool>.Failure(
                                    new Error(ErrorCodes.Session.TooManyPinned, $"Cannot pin more than {maxPinned} entries in this session.")),
                                traceId),
                            ArcanumJsonContext.Default.ApiResponseBoolean,
                            statusCode: StatusCodes.Status409Conflict);
                    }
                }

                await grimoire.SetEntryPinnedAsync(id, entryId, true, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId));
            })
        .WithName("PinSessionEntry");

        apiGroup.MapPost(
            "/sessions/{id:guid}/compact",
            async (Guid id, IContextCompressionService compression, IGrimoireRepository grimoire, IOptionsMonitor<ArcanumSettings> options, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SessionSettings sessionSettings = options.CurrentValue.ResolveSessions();

                if (!sessionSettings.AllowMemoryManagement)
                {
                    return Results.BadRequest(
                        ApiResponse<CompactResult>.FromResult(
                            Result<CompactResult>.Failure(
                                new Error(ErrorCodes.Session.MemoryManagementDisabled, "Memory management is disabled.")),
                            traceId));
                }

                if (!await grimoire.SessionExistsAsync(id, ctx.RequestAborted).ConfigureAwait(false))
                {
                    return Results.Json(
                        ApiResponse<CompactResult>.FromResult(
                            Result<CompactResult>.Failure(new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseCompactResult,
                        statusCode: StatusCodes.Status404NotFound);
                }

                ArcanumSettings settings = options.CurrentValue;

                int contextWindowLimit = 8192; // Matches ContextCompressionService.DefaultContextWindowLimit.

                if (ProviderResolver.TryResolveProviderForModel(settings, settings.DefaultModel, out ProviderSettings? provider, out _)
                    && provider is not null)
                {
                    contextWindowLimit = provider.ContextWindowLimit;
                }

                CompactResult result = await compression
                    .CompressSessionAsync(id, contextWindowLimit, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<CompactResult>.FromResult(Result<CompactResult>.Success(result), traceId));
            })
        .WithName("CompactSession");

        apiGroup.MapDelete(
            "/sessions/{id:guid}/entries/{entryId:guid}/pin",
            async (Guid id, Guid entryId, IGrimoireRepository grimoire, IOptionsMonitor<ArcanumSettings> options, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SessionSettings sessionSettings = options.CurrentValue.ResolveSessions();

                if (!sessionSettings.AllowMemoryManagement)
                {
                    return Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(
                                new Error(ErrorCodes.Session.MemoryManagementDisabled, "Memory management is disabled.")),
                            traceId));
                }

                GrimoireEntryDto? entry = await grimoire
                    .GetEntryByIdAsync(id, entryId, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (entry is null)
                {
                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(ErrorCodes.Session.EntryNotFound, "Entry was not found in this session.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                await grimoire.SetEntryPinnedAsync(id, entryId, false, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId));
            })
        .WithName("UnpinSessionEntry");

        return apiGroup;
    }

    private static async Task PumpSessionLiveAsync(
        Guid sessionId,
        SessionEventHub eventHub,
        ChannelWriter<Entry> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (Entry entry in eventHub.SubscribeAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
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

    private static async Task<Session?> GetSessionForUpdateAsync(ISessionRepository repo, Guid id, CancellationToken ct)
    {
        Session? session = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);

        return session?.CloneHeader();
    }

    private static async Task WriteEntrySseAsync(HttpContext httpContext, Entry entry, CancellationToken cancellationToken)
    {

        EntryDto dto = SessionMapping.ToEntryDto(entry);

        ArrayBufferWriter<byte> buffer = new(SseDataPrefix.Length + 512 + SseLineBreak.Length);

        buffer.Write(SseDataPrefix);

        Utf8JsonWriter jsonWriter = new(buffer);

        try
        {

            JsonSerializer.Serialize(jsonWriter, dto, ArcanumJsonContext.Default.EntryDto);

            jsonWriter.Flush();

            buffer.Write(SseLineBreak);

            await httpContext.Response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

            await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            jsonWriter.Dispose();

        }

    }

}
