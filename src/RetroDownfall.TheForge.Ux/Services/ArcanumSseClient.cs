using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Chronicle;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Thin typed wrapper over <see cref="ArcanumApiClient.GetSseAsync"/> that deserializes each frame's
/// <see cref="SseEvent.Data"/> payload into a concrete type for a specific SSE route.
/// </summary>
public sealed class ArcanumSseClient
{

    private readonly ArcanumApiClient _apiClient;

    private readonly ILogger<ArcanumSseClient> _logger;

    public ArcanumSseClient(ArcanumApiClient apiClient, ILogger<ArcanumSseClient> logger)
    {

        _apiClient = apiClient;

        _logger = logger;

    }

    /// <summary><c>GET /api/sessions/{id}/stream</c> — replays recent entries, then streams live. The Tome subscribes on open, unsubscribes on close.</summary>
    public IAsyncEnumerable<EntryDto> StreamSessionEntriesAsync(Guid sessionId, DateTimeOffset? since, CancellationToken cancellationToken)
    {

        string path = since is null
            ? $"/api/sessions/{sessionId}/stream"
            : $"/api/sessions/{sessionId}/stream?since={Uri.EscapeDataString(since.Value.ToString("O"))}";

        return DeserializeStreamAsync(path, ForgeJsonContext.Default.EntryDto, cancellationToken);

    }

    /// <summary>
    /// <c>GET /api/apprentices/{id}/chronicle</c> — parsed into the tolerant <see cref="ChronicleFrame"/>
    /// shape rather than <c>ApprenticeEvent</c> directly; see <see cref="ChronicleFrame"/> for why.
    /// </summary>
    public IAsyncEnumerable<ChronicleFrame> StreamChronicleAsync(Guid apprenticeId, CancellationToken cancellationToken) =>
        DeserializeStreamAsync($"/api/apprentices/{apprenticeId}/chronicle", ForgeJsonContext.Default.ChronicleFrame, cancellationToken);

    /// <summary><c>GET /api/events/mcp</c> — live MCP server lifecycle events (The Arsenal).</summary>
    public IAsyncEnumerable<McpServerEvent> StreamMcpEventsAsync(CancellationToken cancellationToken) =>
        DeserializeStreamAsync("/api/events/mcp", ForgeJsonContext.Default.McpServerEvent, cancellationToken);

    /// <summary><c>GET /api/events/logs</c> — live log tail (The Foundry Floor).</summary>
    public IAsyncEnumerable<LogEntry> StreamLogsAsync(CancellationToken cancellationToken) =>
        DeserializeStreamAsync("/api/events/logs", ForgeJsonContext.Default.LogEntry, cancellationToken);

    /// <summary><c>GET /api/events/daemon</c> — live daemon/Unseen Servant events (The Servants' Quarters).</summary>
    public IAsyncEnumerable<DaemonEvent> StreamDaemonEventsAsync(CancellationToken cancellationToken) =>
        DeserializeStreamAsync("/api/events/daemon", ForgeJsonContext.Default.DaemonEvent, cancellationToken);

    private async IAsyncEnumerable<TFrame> DeserializeStreamAsync<TFrame>(
        string path,
        JsonTypeInfo<TFrame> frameTypeInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        await foreach (SseEvent sse in _apiClient.GetSseAsync(path, cancellationToken).ConfigureAwait(false))
        {

            TFrame? frame = default;

            bool parsed = false;

            try
            {

                frame = JsonSerializer.Deserialize(sse.Data, frameTypeInfo);

                parsed = true;

            }
            catch (JsonException ex)
            {

                _logger.LogWarning(ex, "Could not parse SSE frame from {Path}.", path);

            }

            if (parsed && frame is not null)
            {

                yield return frame;

            }

        }

    }

}
