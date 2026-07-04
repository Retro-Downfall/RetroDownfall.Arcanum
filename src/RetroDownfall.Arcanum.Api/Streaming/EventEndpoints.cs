using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Logging;

namespace RetroDownfall.Arcanum.Api.Streaming;

internal static class EventEndpoints
{

    private static readonly byte[] SseDataPrefix = "data: "u8.ToArray();

    private static readonly byte[] SseLineBreak = "\n\n"u8.ToArray();

    private static readonly byte[] SseLogsConnected = "data: {\"connected\":true}\n\n"u8.ToArray();

    public static RouteGroupBuilder MapEventEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet(
            "/logs",
            async (
                Core.Logging.LogLevel? minLevel,
                string? category,
                DateTimeOffset? from,
                DateTimeOffset? to,
                string? search,
                int? limit,
                long? beforeSequence,
                ILogQueryService query,
                HttpContext ctx) =>
            {
                LogQueryRequest request = new(minLevel, category, from, to, search, limit, beforeSequence);

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                LogQueryResult result = await query
                    .QueryAsync(request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<LogQueryResult>.FromResult(
                        Result<LogQueryResult>.Success(result),
                        traceId));
            })
        .WithName("QueryLogs");

        apiGroup.MapGet(
            "/events/daemon",
            async (HttpContext httpContext, IEventBus eventBus, SseConnectionGate sseGate, IOptionsSnapshot<ArcanumSettings> settings, CancellationToken cancellationToken) =>
            {

                if (!sseGate.TryAcquire(SseEventTypes.Daemon, out SseConnectionLease? sseLease, out SseConnectionDenial denial))
                {

                    return SseConnectionResults.FromDenial(httpContext, denial);

                }

                using (sseLease)
                {

                using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(
                    httpContext.RequestAborted,
                    cancellationToken);

                CancellationToken ct = streamCts.Token;

                SseStreamWriter.PrepareResponse(httpContext);

                ArrayBufferWriter<byte> sseBuffer = new(512);

                Utf8JsonWriter sseJsonWriter = new(sseBuffer, new JsonWriterOptions { Indented = false });

                TimeSpan heartbeatInterval = ResolveSseHeartbeatInterval(settings.Value);

                try
                {

                    await SseStreamWriter.StreamAsync(
                        httpContext,
                        eventBus.Subscribe<DaemonEvent>(ct),
                        async (DaemonEvent ev, CancellationToken writeCt) =>
                        {
                            await WriteSseJsonAsync(httpContext, ev, ArcanumJsonContext.Default.DaemonEvent, sseBuffer, sseJsonWriter, writeCt).ConfigureAwait(false);
                        },
                        heartbeatInterval,
                        ct).ConfigureAwait(false);

                }
                catch (OperationCanceledException)
                {

                    await SseStreamWriter.WriteDoneAsync(httpContext).ConfigureAwait(false);

                }
                finally
                {

                    sseJsonWriter.Dispose();

                }

                return Results.Empty;

                }

            })
        .WithName("GetDaemonEvents");

        apiGroup.MapGet(
            "/events/mcp",
            async (HttpContext httpContext, IEventBus eventBus, SseConnectionGate sseGate, IOptionsSnapshot<ArcanumSettings> settings, CancellationToken cancellationToken) =>
            {

                if (!sseGate.TryAcquire(SseEventTypes.Mcp, out SseConnectionLease? sseLease, out SseConnectionDenial denial))
                {

                    return SseConnectionResults.FromDenial(httpContext, denial);

                }

                using (sseLease)
                {

                using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(
                    httpContext.RequestAborted,
                    cancellationToken);

                CancellationToken ct = streamCts.Token;

                SseStreamWriter.PrepareResponse(httpContext);

                ArrayBufferWriter<byte> sseBuffer = new(512);

                Utf8JsonWriter sseJsonWriter = new(sseBuffer, new JsonWriterOptions { Indented = false });

                TimeSpan heartbeatInterval = ResolveSseHeartbeatInterval(settings.Value);

                try
                {

                    await SseStreamWriter.StreamAsync(
                        httpContext,
                        eventBus.Subscribe<McpServerEvent>(ct),
                        async (McpServerEvent ev, CancellationToken writeCt) =>
                        {
                            await WriteSseJsonAsync(httpContext, ev, ArcanumJsonContext.Default.McpServerEvent, sseBuffer, sseJsonWriter, writeCt).ConfigureAwait(false);
                        },
                        heartbeatInterval,
                        ct).ConfigureAwait(false);

                }
                catch (OperationCanceledException)
                {

                    await SseStreamWriter.WriteDoneAsync(httpContext).ConfigureAwait(false);

                }
                finally
                {

                    sseJsonWriter.Dispose();

                }

                return Results.Empty;

                }

            })
        .WithName("GetMcpEvents");

        apiGroup.MapGet(
            "/events/logs",
            async (HttpContext httpContext, ILogQueryService query, SseConnectionGate sseGate, IOptionsSnapshot<ArcanumSettings> settings, CancellationToken cancellationToken) =>
            {

                if (!sseGate.TryAcquire(SseEventTypes.Logs, out SseConnectionLease? sseLease, out SseConnectionDenial denial))
                {

                    return SseConnectionResults.FromDenial(httpContext, denial);

                }

                using (sseLease)
                {

                using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(
                    httpContext.RequestAborted,
                    cancellationToken);

                CancellationToken ct = streamCts.Token;

                SseStreamWriter.PrepareResponse(httpContext);

                ArrayBufferWriter<byte> sseBuffer = new(512);

                Utf8JsonWriter sseJsonWriter = new(sseBuffer, new JsonWriterOptions { Indented = false });

                TimeSpan heartbeatInterval = ResolveSseHeartbeatInterval(settings.Value);

                try
                {

                    await httpContext.Response.Body.WriteAsync(SseLogsConnected, ct).ConfigureAwait(false);

                    await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);

                    await SseStreamWriter.StreamAsync(
                        httpContext,
                        query.StreamAsync(null, ct),
                        async (LogEntry entry, CancellationToken writeCt) =>
                        {
                            await WriteSseJsonAsync(
                                httpContext,
                                entry,
                                ArcanumJsonContext.Default.LogEntry,
                                sseBuffer,
                                sseJsonWriter,
                                writeCt).ConfigureAwait(false);
                        },
                        heartbeatInterval,
                        ct).ConfigureAwait(false);

                }
                catch (OperationCanceledException)
                {

                    await SseStreamWriter.WriteDoneAsync(httpContext).ConfigureAwait(false);

                }
                finally
                {

                    sseJsonWriter.Dispose();

                }

                return Results.Empty;

                }

            })
        .WithName("StreamLogs");

        return apiGroup;
    }

    private static async Task WriteSseJsonAsync<T>(
        HttpContext httpContext,
        T value,
        JsonTypeInfo<T> typeInfo,
        ArrayBufferWriter<byte> buffer,
        Utf8JsonWriter jsonWriter,
        CancellationToken cancellationToken)
    {

        buffer.Clear();

        buffer.Write(SseDataPrefix);

        jsonWriter.Reset();

        JsonSerializer.Serialize(jsonWriter, value, typeInfo);

        buffer.Write(SseLineBreak);

        await httpContext.Response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

        await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

    }

    private static TimeSpan ResolveSseHeartbeatInterval(ArcanumSettings settings)
    {

        int seconds = ArcanumSettingClamps.EventBusHeartbeatSeconds(
            settings.EventBus?.HeartbeatSeconds ?? new EventBusSettings().HeartbeatSeconds);

        return TimeSpan.FromSeconds(seconds);

    }

}
