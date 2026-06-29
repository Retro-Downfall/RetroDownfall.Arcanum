using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Infrastructure.Chronosync;

public sealed class ChronosyncEngine : IChronosyncEngine
{
    private readonly IGrimoireRepository _grimoire;

    private readonly TimeProvider _time;

    private readonly ILogger<ChronosyncEngine> _logger;

    public ChronosyncEngine(
        IGrimoireRepository grimoire,
        TimeProvider time,
        ILogger<ChronosyncEngine> logger)
    {
        _grimoire = grimoire;

        _time = time;

        _logger = logger;
    }

    public async Task<ChronosyncReport> AnalyzeAndSyncAsync(
        PatternSnapshot currentSnapshot,
        CancellationToken cancellationToken = default)
    {
        WorkspaceContext? latest = await _grimoire
            .GetLatestWorkspaceContextAsync(currentSnapshot.RootPath, cancellationToken)
            .ConfigureAwait(false);

        PatternSnapshot? previous = null;

        DateTimeOffset? previousTime = null;

        if (latest is not null)
        {
            try
            {
                previous = JsonSerializer.Deserialize<PatternSnapshot>(
                    latest.SerializedSnapshot,
                    GrimoireJsonContext.Default.PatternSnapshot);

                if (previous is not null)
                {
                    previousTime = latest.CreatedAt;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not deserialize stored PatternSnapshot for workspace {WorkspacePath}; treating as no prior baseline.",
                    currentSnapshot.RootPath);
            }
        }

        bool domainChanged = previous is not null && previous.Domain != currentSnapshot.Domain;

        string[] newThreads;

        string[] missingThreads;

        if (previous is null)
        {
            newThreads = [];

            missingThreads = [];
        }
        else
        {
            HashSet<string> prevSet = new(previous.Threads, StringComparer.Ordinal);

            HashSet<string> currSet = new(currentSnapshot.Threads, StringComparer.Ordinal);

            newThreads = currentSnapshot.Threads.Where(t => !prevSet.Contains(t)).ToArray();

            missingThreads = previous.Threads.Where(t => !currSet.Contains(t)).ToArray();
        }

        string json = JsonSerializer.Serialize<PatternSnapshot>(
            currentSnapshot,
            GrimoireJsonContext.Default.PatternSnapshot);

        await _grimoire
            .RecordWorkspaceContextAsync(
                new WorkspaceContext
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = _time.GetUtcNow(),
                    WorkspacePath = currentSnapshot.RootPath,
                    SerializedSnapshot = json,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new ChronosyncReport(previousTime, newThreads, missingThreads, domainChanged, previous?.Domain);
    }
}
