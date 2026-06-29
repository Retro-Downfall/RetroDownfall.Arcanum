using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
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
                            Result<EntryDto>.Failure(new Error("Session.EmptyContent", "Entry content is required.")),
                            traceId));
                }

                Entry entry = new()
                {
                    Id = Guid.NewGuid(),
                    Role = request.Role,
                    Content = request.Content.Trim(),
                    ModelUsed = request.ModelUsed ?? string.Empty,
                };

                try
                {
                    Entry saved = await repo.AddEntryAsync(id, entry, ctx.RequestAborted).ConfigureAwait(false);

                    eventHub.Publish(id, saved);

                    return Results.Ok(
                        ApiResponse<EntryDto>.FromResult(
                            Result<EntryDto>.Success(SessionMapping.ToEntryDto(saved)),
                            traceId));
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(
                        ApiResponse<EntryDto>.FromResult(
                            Result<EntryDto>.Failure(new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseEntryDto,
                        statusCode: StatusCodes.Status404NotFound);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("archived", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(
                        ApiResponse<EntryDto>.FromResult(
                            Result<EntryDto>.Failure(new Error("Session.Archived", ex.Message)),
                            traceId));
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("Session.TooManyEntries:", StringComparison.Ordinal))
                {
                    return Results.BadRequest(
                        ApiResponse<EntryDto>.FromResult(
                            Result<EntryDto>.Failure(new Error("Session.TooManyEntries", ex.Message)),
                            traceId));
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("Session.EntryTooLarge:", StringComparison.Ordinal))
                {
                    return Results.BadRequest(
                        ApiResponse<EntryDto>.FromResult(
                            Result<EntryDto>.Failure(new Error("Session.EntryTooLarge", ex.Message)),
                            traceId));
                }
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
                                    new Error("Session.InvalidStatus", "Status must be 'active' or 'archived'.")),
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

                await queue.QueueAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                string acceptedTraceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

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

                if (!sseGate.TryAcquire(out SseConnectionLease? sseLease))
                {

                    return SseConnectionResults.TooManyConnections(httpContext);

                }

                using (sseLease)
                {

                SseStreamWriter.PrepareResponse(httpContext);

                int channelCapacity = ArcanumSettingClamps.EventBusChannelCapacity(
                    options.CurrentValue.EventBus?.ChannelCapacity ?? new EventBusSettings().ChannelCapacity);

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

                SessionSettings sessionSettings = options.CurrentValue.Sessions ?? new SessionSettings();

                int replayLimit = ArcanumSettingClamps.SessionStreamReplayLimit(
                    sessionSettings.MaxStreamReplayEntries);

                if (since is Guid sinceEntryId)
                {
                    Entry? sinceEntry = await repo
                        .GetEntryAsync(id, sinceEntryId, httpContext.RequestAborted)
                        .ConfigureAwait(false);

                    if (sinceEntry is null)
                    {
                        await pumpCts.CancelAsync().ConfigureAwait(false);

                        try
                        {
                            await pumpTask.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }

                        return Results.Json(
                            ApiResponse<EntryDto>.FromResult(
                                Result<EntryDto>.Failure(new Error("Session.EntryNotFound", "No entry exists with that id in this session.")),
                                Activity.Current?.Id ?? httpContext.TraceIdentifier),
                            ArcanumJsonContext.Default.ApiResponseEntryDto,
                            statusCode: StatusCodes.Status404NotFound);
                    }

                    List<Entry> catchUp = await repo
                        .GetEntriesAfterAsync(id, sinceEntry.CreatedAt, sinceEntry.Id, replayLimit, httpContext.RequestAborted)
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
                        options.CurrentValue.EventBus?.HeartbeatSeconds ?? new EventBusSettings().HeartbeatSeconds));

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
