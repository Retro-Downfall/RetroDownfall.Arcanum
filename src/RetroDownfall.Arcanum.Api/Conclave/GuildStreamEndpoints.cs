using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Api.Conclave;

internal static partial class GuildEndpoints
{
    internal static RouteGroupBuilder MapGuildStreamEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/guild/quests/{questId:guid}/events", WatchGuildTreeAsync)
            .WithName("GuildWatchTree")
            .WithMetadata(GrimoireStreamRouteMetadata.Quiesceable);

        api.MapGet("/guild/cohorts/{cohortId:guid}/events", WatchGuildCohortAsync)
            .WithName("GuildWatchCohort")
            .WithMetadata(GrimoireStreamRouteMetadata.Quiesceable);

        return api;
    }

    private static async Task<IResult> WatchGuildTreeAsync(Guid questId, string? after, GuildNativeHost host,
        GuildPortalStore portal, SseConnectionGate gate, IOptionsMonitor<ArcanumSettings> settings, HttpContext context)
    {
        CancellationToken cancellation = context.RequestAborted;

        var last = context.Request.Headers["Last-Event-ID"];

        if (last.Count > 1 || (last.Count == 1 && (string.IsNullOrWhiteSpace(last[0])
            || after is not null && !string.Equals(after, last[0], StringComparison.Ordinal))))
            return Reply(Result<GuildQuestDto>.Failure(new Error(ErrorCodes.Guild.CursorInvalid, "The stream cursors do not match.")),
                context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

        string? cursor = after ?? (last.Count == 1 ? last[0] : null);

        Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), cancellation).ConfigureAwait(false);

        if (caller.IsFailure) return Reply(Result<GuildQuestDto>.Failure(caller.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

        Guid root;

        GuildTreeSnapshotDto? snapshot = null;

        if (cursor is null)
        {
            Result<GuildTreeSnapshotDto> captured = await portal.CaptureTreeSnapshotAsync(caller.Value, questId, cancellation).ConfigureAwait(false);

            if (captured.IsFailure) return Reply(Result<GuildQuestDto>.Failure(captured.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            snapshot = captured.Value;

            root = snapshot.Summary.RootQuestId;

            cursor = snapshot.ResumeCursor;
        }
        else
        {
            if (!GuildPortalStore.TryGetReplayRoot(cursor, out root))
                return Reply(Result<GuildQuestDto>.Failure(new Error(ErrorCodes.Guild.CursorInvalid, "The stream cursor is invalid.")),
                    context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            Result<GuildPortalReplayPage> validated = await portal.ReadScopedReplayAsync(caller.Value, root, questId, null,
                cursor, cancellation, 1).ConfigureAwait(false);

            if (validated.IsFailure) return Reply(Result<GuildQuestDto>.Failure(validated.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);
        }

        if (!gate.TryAcquire(SseEventTypes.Guild, out SseConnectionLease? lease, out SseConnectionDenial denial))
            return SseConnectionResults.FromDenial(context, denial);

        using (lease)
        {
            Result<GuildPortalReplayPage> current = await portal.ReadScopedReplayAsync(caller.Value, root, questId, null,
                cursor, cancellation, 1).ConfigureAwait(false);

            if (current.IsFailure) return Reply(Result<GuildQuestDto>.Failure(current.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            SseStreamWriter.PrepareResponse(context);

            GrimoireStreamQuiescence quiescence = GrimoireStreamQuiescence.For(context);

            using CancellationTokenSource producer = quiescence.LinkProducer(cancellation);

            TimeSpan heartbeat = TimeSpan.FromSeconds(ArcanumSettingClamps.EventBusHeartbeatSeconds(
                settings.CurrentValue.ResolveEventBus().HeartbeatSeconds));

            long lastWrite = Stopwatch.GetTimestamp();

            try
            {
                if (snapshot is not null && !quiescence.IsQuiescing)
                    await WriteGuildFrameAsync(context, "snapshot", cursor, snapshot, GuildJsonContext.Default.GuildTreeSnapshotDto, cancellation).ConfigureAwait(false);

                while (!producer.IsCancellationRequested)
                {
                    Result<GuildPortalReplayPage> page = await portal.ReadScopedReplayAsync(caller.Value, root, questId, null,
                        cursor, producer.Token, 1).ConfigureAwait(false);

                    if (page.IsFailure)
                    {
                        if (page.Error.Code == ErrorCodes.Guild.CursorGone && !quiescence.IsQuiescing)
                        {
                            Result<GuildTreeSnapshotDto> fresh = await portal.CaptureTreeSnapshotAsync(caller.Value, questId, producer.Token).ConfigureAwait(false);

                            if (fresh.IsSuccess)
                            {
                                GuildResyncEventDto resync = new(page.Error.Code, $"/api/guild/quests/{questId:D}/tree", fresh.Value.ResumeCursor);

                                await WriteGuildFrameAsync(context, "resync", fresh.Value.ResumeCursor, resync,
                                    GuildJsonContext.Default.GuildResyncEventDto, cancellation).ConfigureAwait(false);
                            }
                        }

                        break;
                    }

                    if (quiescence.IsQuiescing) break;

                    cursor = page.Value.ResumeCursor;

                    if (page.Value.Page.Items.Length != 0)
                    {
                        await WriteGuildFrameAsync(context, "change", cursor, page.Value.Page.Items[0],
                            GuildJsonContext.Default.GuildChangeEventDto, cancellation).ConfigureAwait(false);

                        lastWrite = Stopwatch.GetTimestamp();
                    }
                    else if (page.Value.CaughtUp)
                    {
                        Result<GuildTerminalEventDto?> terminal = await portal.ReadTreeTerminalAsync(caller.Value, root,
                            questId, cursor, producer.Token).ConfigureAwait(false);

                        if (terminal.IsFailure)
                        {
                            if (terminal.Error.Code == ErrorCodes.Guild.CursorGone) continue;

                            break;
                        }

                        if (quiescence.IsQuiescing) break;

                        if (terminal.Value is { } final)
                        {
                            await WriteGuildFrameAsync(context, "terminal", cursor, final,
                                GuildJsonContext.Default.GuildTerminalEventDto, cancellation).ConfigureAwait(false);

                            break;
                        }

                        if (heartbeat > TimeSpan.Zero && Stopwatch.GetElapsedTime(lastWrite) >= heartbeat)
                        {
                            await SseStreamWriter.WriteKeepAliveAsync(context, cancellation).ConfigureAwait(false);

                            lastWrite = Stopwatch.GetTimestamp();
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1), producer.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (producer.IsCancellationRequested)
            {
                // Caller disconnect or maintenance ends the subscription between frames.
            }
            catch (IOException) when (cancellation.IsCancellationRequested)
            {
                // A disconnected client cannot receive a final control frame.
            }
        }

        return Results.Empty;
    }

    private static async Task<IResult> WatchGuildCohortAsync(Guid cohortId, string? after, GuildNativeHost host,
        GuildCohortStore cohorts, GuildPortalStore portal, SseConnectionGate gate, IOptionsMonitor<ArcanumSettings> settings, HttpContext context)
    {
        CancellationToken cancellation = context.RequestAborted;

        var last = context.Request.Headers["Last-Event-ID"];

        if (last.Count > 1 || (last.Count == 1 && (string.IsNullOrWhiteSpace(last[0])
            || after is not null && !string.Equals(after, last[0], StringComparison.Ordinal))))
            return Reply(Result<GuildQuestDto>.Failure(new Error(ErrorCodes.Guild.CursorInvalid, "The stream cursors do not match.")),
                context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

        string? cursor = after ?? (last.Count == 1 ? last[0] : null);

        Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), cancellation).ConfigureAwait(false);

        if (caller.IsFailure) return Reply(Result<GuildQuestDto>.Failure(caller.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

        Guid root;

        GuildCohortSnapshotDto? snapshot = null;

        if (cursor is null)
        {
            Result<GuildCohortSnapshotDto> captured = await portal.CaptureCohortSnapshotAsync(caller.Value, cohortId, cohorts, cancellation).ConfigureAwait(false);

            if (captured.IsFailure) return Reply(Result<GuildQuestDto>.Failure(captured.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            snapshot = captured.Value;

            root = snapshot.Summary.RootQuestId;

            cursor = snapshot.ResumeCursor;
        }
        else
        {
            if (!GuildPortalStore.TryGetReplayRoot(cursor, out root))
                return Reply(Result<GuildQuestDto>.Failure(new Error(ErrorCodes.Guild.CursorInvalid, "The stream cursor is invalid.")),
                    context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            Result<GuildPortalReplayPage> validated = await portal.ReadScopedReplayAsync(caller.Value, root, null, cohortId,
                cursor, cancellation, 1).ConfigureAwait(false);

            if (validated.IsFailure) return Reply(Result<GuildQuestDto>.Failure(validated.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);
        }

        if (!gate.TryAcquire(SseEventTypes.Guild, out SseConnectionLease? lease, out SseConnectionDenial denial))
            return SseConnectionResults.FromDenial(context, denial);

        using (lease)
        {
            Result<GuildPortalReplayPage> current = await portal.ReadScopedReplayAsync(caller.Value, root, null, cohortId,
                cursor, cancellation, 1).ConfigureAwait(false);

            if (current.IsFailure) return Reply(Result<GuildQuestDto>.Failure(current.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            SseStreamWriter.PrepareResponse(context);

            GrimoireStreamQuiescence quiescence = GrimoireStreamQuiescence.For(context);

            using CancellationTokenSource producer = quiescence.LinkProducer(cancellation);

            TimeSpan heartbeat = TimeSpan.FromSeconds(ArcanumSettingClamps.EventBusHeartbeatSeconds(
                settings.CurrentValue.ResolveEventBus().HeartbeatSeconds));

            long lastWrite = Stopwatch.GetTimestamp();

            try
            {
                if (snapshot is not null && !quiescence.IsQuiescing)
                    await WriteGuildFrameAsync(context, "snapshot", cursor, snapshot, GuildJsonContext.Default.GuildCohortSnapshotDto, cancellation).ConfigureAwait(false);

                while (!producer.IsCancellationRequested)
                {
                    Result<GuildPortalReplayPage> page = await portal.ReadScopedReplayAsync(caller.Value, root, null, cohortId,
                        cursor, producer.Token, 1).ConfigureAwait(false);

                    if (page.IsFailure)
                    {
                        if (page.Error.Code == ErrorCodes.Guild.CursorGone && !quiescence.IsQuiescing)
                        {
                            Result<GuildCohortSnapshotDto> fresh = await portal.CaptureCohortSnapshotAsync(caller.Value, cohortId, cohorts, producer.Token).ConfigureAwait(false);

                            if (fresh.IsSuccess)
                            {
                                GuildResyncEventDto resync = new(page.Error.Code, $"/api/guild/cohorts/{cohortId:D}", fresh.Value.ResumeCursor);

                                await WriteGuildFrameAsync(context, "resync", fresh.Value.ResumeCursor, resync,
                                    GuildJsonContext.Default.GuildResyncEventDto, cancellation).ConfigureAwait(false);
                            }
                        }

                        break;
                    }

                    if (quiescence.IsQuiescing) break;

                    cursor = page.Value.ResumeCursor;

                    if (page.Value.Page.Items.Length != 0)
                    {
                        await WriteGuildFrameAsync(context, "change", cursor, page.Value.Page.Items[0],
                            GuildJsonContext.Default.GuildChangeEventDto, cancellation).ConfigureAwait(false);

                        lastWrite = Stopwatch.GetTimestamp();
                    }
                    else if (page.Value.CaughtUp)
                    {
                        Result<GuildTerminalEventDto?> terminal = await portal.ReadCohortTerminalAsync(caller.Value, root,
                            cohortId, cursor, producer.Token).ConfigureAwait(false);

                        if (terminal.IsFailure)
                        {
                            if (terminal.Error.Code == ErrorCodes.Guild.CursorGone) continue;

                            break;
                        }

                        if (quiescence.IsQuiescing) break;

                        if (terminal.Value is { } final)
                        {
                            await WriteGuildFrameAsync(context, "terminal", cursor, final,
                                GuildJsonContext.Default.GuildTerminalEventDto, cancellation).ConfigureAwait(false);

                            break;
                        }

                        if (heartbeat > TimeSpan.Zero && Stopwatch.GetElapsedTime(lastWrite) >= heartbeat)
                        {
                            await SseStreamWriter.WriteKeepAliveAsync(context, cancellation).ConfigureAwait(false);

                            lastWrite = Stopwatch.GetTimestamp();
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1), producer.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (producer.IsCancellationRequested)
            {
                // Caller disconnect or maintenance ends the subscription between frames.
            }
            catch (IOException) when (cancellation.IsCancellationRequested)
            {
                // A disconnected client cannot receive a final control frame.
            }
        }

        return Results.Empty;
    }

    private static async Task WriteGuildFrameAsync<T>(HttpContext context, string name, string id, T payload,
        JsonTypeInfo<T> metadata, CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, metadata);

        byte[] prefix = Encoding.UTF8.GetBytes($"event: {name}\nid: {id}\ndata: ");

        await context.Response.Body.WriteAsync(prefix, ct).ConfigureAwait(false);

        await context.Response.Body.WriteAsync(json, ct).ConfigureAwait(false);

        await context.Response.Body.WriteAsync("\n\n"u8.ToArray(), ct).ConfigureAwait(false);

        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
