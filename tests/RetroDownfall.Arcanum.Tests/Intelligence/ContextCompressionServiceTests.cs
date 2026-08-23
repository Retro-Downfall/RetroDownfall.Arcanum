using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ContextCompressionServiceTests
{
    [Fact]
    public async Task CompressSessionAsync_MissingSession_ReturnsEmptyResult()
    {
        CompressionGrimoireRepository grimoire = new();
        ContextCompressionService service = CreateService(grimoire);

        CompactResult result = await service.CompressSessionAsync(
            Guid.NewGuid(),
            256,
            CancellationToken.None);

        Assert.Equal(new CompactResult(0, 0, 0), result);
        Assert.Empty(grimoire.DeletedEntryIds);
    }

    [Fact]
    public async Task CompressSessionAsync_ContextUnderDefaultLimit_PreservesMeasuredTokens()
    {
        Session session = CreateSession(
            [.. Enumerable.Range(0, 6)
                .Select(index => CreateEntry($"short context {index}", createdAtOffset: index))]);
        CompressionGrimoireRepository grimoire = new() { Session = session };
        ContextCompressionService service = CreateService(grimoire);

        CompactResult result = await service.CompressSessionAsync(
            session.Id,
            contextWindowLimit: 0,
            CancellationToken.None);

        Assert.True(result.TokensBefore > 0);
        Assert.Equal(result.TokensBefore, result.TokensAfter);
        Assert.Equal(0, result.EntriesRemoved);
        Assert.Empty(grimoire.DeletedEntryIds);
    }

    [Fact]
    public async Task CompressSessionAsync_OverLimit_DeletesOldestEntryAndRecounts()
    {
        Entry oldest = CreateEntry(new string('a', 20_000), createdAtOffset: 0);
        Entry retained = CreateEntry("retained", createdAtOffset: 1);
        Entry[] fillers = Enumerable.Range(2, 4)
            .Select(index => CreateEntry($"filler-{index}", isPinned: true, createdAtOffset: index))
            .ToArray();
        Session session = CreateSession([oldest, retained, .. fillers]);
        CompressionGrimoireRepository grimoire = new() { Session = session };
        ContextCompressionService service = CreateService(grimoire);

        CompactResult result = await service.CompressSessionAsync(
            session.Id,
            256,
            CancellationToken.None);

        Assert.Equal([oldest.Id], grimoire.DeletedEntryIds);
        Assert.Equal(1, result.EntriesRemoved);
        Assert.True(result.TokensBefore > result.TokensAfter);
        Assert.DoesNotContain(session.Entries, entry => entry.Id == oldest.Id);
        Assert.Contains(session.Entries, entry => entry.Id == retained.Id);
    }

    [Fact]
    public async Task CompressSessionAsync_OnlyPinnedEntries_LeavesOverLimitContextUntouched()
    {
        Entry[] pinned = Enumerable.Range(0, 6)
            .Select(index => CreateEntry(
                new string('p', 20_000),
                isPinned: true,
                createdAtOffset: index))
            .ToArray();
        Session session = CreateSession(pinned);
        CompressionGrimoireRepository grimoire = new() { Session = session };
        ContextCompressionService service = CreateService(grimoire);

        CompactResult result = await service.CompressSessionAsync(
            session.Id,
            256,
            CancellationToken.None);

        Assert.True(result.TokensBefore > 128);
        Assert.Equal(result.TokensBefore, result.TokensAfter);
        Assert.Equal(0, result.EntriesRemoved);
        Assert.Empty(grimoire.DeletedEntryIds);
    }

    [Fact]
    public async Task CompressSessionAsync_ReloadMissingAfterDelete_ReportsConservativeCounts()
    {
        Entry removable = CreateEntry(string.Empty, createdAtOffset: 0);
        Entry pinned = CreateEntry(new string('p', 20_000), isPinned: true, createdAtOffset: 1);
        Entry[] fillers = Enumerable.Range(2, 4)
            .Select(index => CreateEntry($"filler-{index}", isPinned: true, createdAtOffset: index))
            .ToArray();
        Session session = CreateSession([removable, pinned, .. fillers]);
        CompressionGrimoireRepository grimoire = new()
        {
            Session = session,
            ReturnNullAfterFirstLoad = true,
        };
        CapturingLogger<ContextCompressionService> logger = new();
        ContextCompressionService service = CreateService(
            grimoire,
            logger);

        CompactResult result = await service.CompressSessionAsync(
            session.Id,
            256,
            CancellationToken.None);

        Assert.Equal([removable.Id], grimoire.DeletedEntryIds);
        Assert.Equal(1, result.EntriesRemoved);
        Assert.Equal(result.TokensBefore, result.TokensAfter);
        Assert.Contains(
            logger.Messages,
            message => message.Contains("context remains", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompressSessionAsync_CancelledLoad_PropagatesCancellation()
    {
        CompressionGrimoireRepository grimoire = new();
        ContextCompressionService service = CreateService(grimoire);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CompressSessionAsync(Guid.NewGuid(), 256, cancellation.Token));
    }

    private static ContextCompressionService CreateService(
        CompressionGrimoireRepository grimoire,
        ILogger<ContextCompressionService>? logger = null)
    {
        InferenceTokenizerResolver tokenizerResolver = new(
            NullLogger<InferenceTokenizerResolver>.Instance);

        return new ContextCompressionService(
            grimoire,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            tokenizerResolver,
            logger ?? NullLogger<ContextCompressionService>.Instance);
    }

    private static Session CreateSession(params Entry[] entries)
    {
        Guid sessionId = Guid.NewGuid();
        foreach (Entry entry in entries)
        {
            entry.SessionId = sessionId;
        }

        return new Session
        {
            Id = sessionId,
            Entries = entries.ToList(),
        };
    }

    private static Entry CreateEntry(
        string content,
        bool isPinned = false,
        int createdAtOffset = 0) =>
        new()
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = content,
            IsPinned = isPinned,
            CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(createdAtOffset),
        };

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class CompressionGrimoireRepository : IGrimoireRepository
    {
        private int _sessionLoadCount;

        public Session? Session { get; init; }

        public bool ReturnNullAfterFirstLoad { get; init; }

        public List<Guid> DeletedEntryIds { get; } = [];

        public Task<Session?> GetSessionAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sessionLoadCount++;

            return Task.FromResult(
                ReturnNullAfterFirstLoad && _sessionLoadCount > 1
                    ? null
                    : Session);
        }

        public Task<bool> DeleteEntryAsync(
            Guid sessionId,
            Guid entryId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeletedEntryIds.Add(entryId);

            Entry? entry = Session?.Entries.SingleOrDefault(candidate => candidate.Id == entryId);
            if (entry is not null)
            {
                _ = Session!.Entries.Remove(entry);
            }

            return Task.FromResult(entry is not null);
        }

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
            Guid? sessionId,
            string prompt,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task FinalizeAssistantEntryAsync(
            Guid assistantEntryId,
            string fullContent,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task DiscardAssistantEntryAsync(
            Guid assistantEntryId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task AppendToolInteractionAsync(
            Guid sessionId,
            string toolName,
            string arguments,
            string result,
            string modelUsed,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task SaveCompletedExchangeAsync(
            string userPrompt,
            string assistantText,
            string modelUsed,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> PurgeSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Session?> GetSessionHeaderAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(
            Guid sessionId,
            int takeLast,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(
            Guid sessionId,
            Guid entryId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> SetEntryPinnedAsync(
            Guid sessionId,
            Guid entryId,
            bool pinned,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> GetPinnedEntryCountAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(
            int threshold,
            DateTime idleCutoff,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<Entry>> GetUnsummarizedEntriesAsync(
            Guid sessionId,
            DateTime watermark,
            int batchSize,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> SessionExistsAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task IncrementSessionTokensAsync(
            Guid sessionId,
            long totalTokens,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task IncrementSessionTokensAndCostAsync(
            Guid sessionId,
            long totalTokens,
            decimal costUsd,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<decimal> GetTodaySpendAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task AdvanceCampaignLogWatermarkAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task UpdateSessionCampaignRollupAsync(
            Guid sessionId,
            string summary,
            DateTime lastSummarizedMessageAt,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string?> ReadLoreAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<LoreDto> ScribeLoreAsync(
            string key,
            string value,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteLoreAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ListPageResult<LoreDto>> ListLoreAsync(
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<LoreDto?> GetLoreAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string> SearchArchivesAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task RecordWorkspaceContextAsync(
            WorkspaceContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(
            string workspacePath,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
