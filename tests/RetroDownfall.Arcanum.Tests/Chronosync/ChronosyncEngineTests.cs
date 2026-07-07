using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Chronosync;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Chronosync;

[Collection("Grimoire")]
public sealed class ChronosyncEngineTests
{

    [SkippableFact]
    public async Task AnalyzeAndSyncAsync_reports_thread_delta_after_baseline()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FixedTimeProvider time = new(DateTimeOffset.Parse("2026-06-20T12:00:00Z"));

        RecordingGrimoireRepository grimoire = new();

        ChronosyncEngine engine = new(
            grimoire,
            time,
            NullLogger<ChronosyncEngine>.Instance);

        string workspacePath = Path.Combine(Path.GetTempPath(), "arcanum-chronosync", Guid.NewGuid().ToString("N"));

        PatternSnapshot baseline = new(
            DomainType.SoftwareEngineering,
            workspacePath,
            ["src/Core", "src/Infrastructure"]);

        ChronosyncReport first = await engine.AnalyzeAndSyncAsync(baseline);

        Assert.Null(first.PreviousSnapshotTime);

        Assert.Empty(first.NewThreads);

        Assert.Empty(first.MissingThreads);

        Assert.False(first.DomainChanged);

        time.Advance(TimeSpan.FromMinutes(5));

        PatternSnapshot updated = new(
            DomainType.SoftwareEngineering,
            workspacePath,
            ["src/Core", "src/Api", "tests"]);

        ChronosyncReport second = await engine.AnalyzeAndSyncAsync(updated);

        Assert.NotNull(second.PreviousSnapshotTime);

        Assert.Equal(["src/Api", "tests"], second.NewThreads);

        Assert.Equal(["src/Infrastructure"], second.MissingThreads);

        Assert.False(second.DomainChanged);

        Assert.Equal(2, grimoire.Snapshots.Count);

    }

    [SkippableFact]
    public async Task AnalyzeAndSyncAsync_detects_domain_change()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FixedTimeProvider time = new(DateTimeOffset.Parse("2026-06-20T13:00:00Z"));

        RecordingGrimoireRepository grimoire = new();

        ChronosyncEngine engine = new(
            grimoire,
            time,
            NullLogger<ChronosyncEngine>.Instance);

        string workspacePath = Path.Combine(Path.GetTempPath(), "arcanum-chronosync-domain", Guid.NewGuid().ToString("N"));

        _ = await engine.AnalyzeAndSyncAsync(
            new PatternSnapshot(DomainType.SoftwareEngineering, workspacePath, ["a"]));

        time.Advance(TimeSpan.FromMinutes(1));

        ChronosyncReport report = await engine.AnalyzeAndSyncAsync(
            new PatternSnapshot(DomainType.Research, workspacePath, ["a"]));

        Assert.True(report.DomainChanged);

        Assert.Equal(DomainType.SoftwareEngineering, report.PreviousDomain);

    }

    private sealed class FixedTimeProvider : TimeProvider
    {

        private DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {

            _utcNow = utcNow;

        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta)
        {

            _utcNow = _utcNow.Add(delta);

        }

    }

    private sealed class RecordingGrimoireRepository : IGrimoireRepository
    {

        public List<WorkspaceContext> Snapshots { get; } = [];

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(
            string workspacePath,
            CancellationToken cancellationToken = default)
        {

            WorkspaceContext? latest = Snapshots
                .Where(s => s.WorkspacePath == workspacePath)
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefault();

            return Task.FromResult(latest);

        }

        public Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default)
        {

            Snapshots.Add(context);

            return Task.CompletedTask;

        }

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
            Guid? sessionId,
            string prompt,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task FinalizeAssistantEntryAsync(
            Guid assistantEntryId,
            string fullContent,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DiscardAssistantEntryAsync(
            Guid assistantEntryId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AppendToolInteractionAsync(
            Guid sessionId,
            string toolName,
            string arguments,
            string result,
            string modelUsed,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveCompletedExchangeAsync(
            string userPrompt,
            string assistantText,
            string modelUsed,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> PurgeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Session?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Session?> GetSessionHeaderAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(
            Guid sessionId,
            int takeLast,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(
            Guid sessionId,
            Guid entryId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteEntryAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SetEntryPinnedAsync(Guid sessionId, Guid entryId, bool pinned, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetPinnedEntryCountAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(
            int threshold,
            DateTime idleCutoff,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<Entry>> GetUnsummarizedEntriesAsync(
            Guid sessionId,
            DateTime watermark,
            int batchSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task IncrementSessionTokensAsync(
            Guid sessionId,
            long totalTokens,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task IncrementSessionTokensAndCostAsync(
            Guid sessionId,
            long totalTokens,
            decimal costUsd,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateSessionCampaignRollupAsync(
            Guid sessionId,
            string summary,
            DateTime lastSummarizedMessageAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ListPageResult<LoreDto>> ListLoreAsync(
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

}
