using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Workspaces;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class GrimoireTurnWriterTests
{

    [Fact]
    public async Task TryBeginBufferedAssistantReplyAsync_StatelessRequest_ReturnsEmptyHandle()
    {

        GrimoireTurnWriter writer = CreateWriter(new TrackingGrimoireRepository());

        GrimoireTurnWriter.TurnHandle handle = await writer.TryBeginBufferedAssistantReplyAsync(
            new PingRequest(
                Prompt: "hello",
                Model: "test-model",
                WorkingDirectory: string.Empty,
                StatelessMessages: [new CoreChatMessage("user", "prior")]),
            "hello",
            "test-model",
            CancellationToken.None);

        Assert.Null(handle.AssistantEntryId);

        Assert.Null(handle.SessionId);

        Assert.False(handle.IsFinalized);

    }

    [Fact]
    public async Task TryBeginBufferedAssistantReplyAsync_SessionRequest_BeginsAndPublishes()
    {

        Guid sessionId = Guid.NewGuid();

        TrackingGrimoireRepository grimoire = new()
        {

            FixedSessionId = sessionId,

        };

        GrimoireTurnWriter writer = CreateWriter(grimoire);

        GrimoireTurnWriter.TurnHandle handle = await writer.TryBeginBufferedAssistantReplyAsync(
            new PingRequest(
                Prompt: "hello",
                Model: "test-model",
                WorkingDirectory: string.Empty,
                SessionId: sessionId),
            "hello",
            "test-model",
            CancellationToken.None);

        Assert.Equal(sessionId, handle.SessionId);

        Assert.NotNull(handle.AssistantEntryId);

        Assert.Equal(1, grimoire.BeginCallCount);

        Assert.Equal(1, grimoire.RecentEntriesPublishCount);

    }

    [Fact]
    public async Task TryFinalizeBufferedAssistantEntryAsync_SetsFinalizedFlag()
    {

        TrackingGrimoireRepository grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire);

        GrimoireTurnWriter.TurnHandle handle = await writer.TryBeginBufferedAssistantReplyAsync(
            new PingRequest(
                Prompt: "hello",
                Model: "test-model",
                WorkingDirectory: string.Empty,
                SessionId: Guid.NewGuid()),
            "hello",
            "test-model",
            CancellationToken.None);

        await writer.TryFinalizeBufferedAssistantEntryAsync(handle, "done", "test-model", CancellationToken.None);

        Assert.True(handle.IsFinalized);

        Assert.Equal(1, grimoire.FinalizeCallCount);

        Assert.Equal(1, grimoire.EntryByIdPublishCount);

    }

    [Fact]
    public async Task ResolveInterruptedAsync_WithPartialContent_FinalizesEntry()
    {

        TrackingGrimoireRepository grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire);

        GrimoireTurnWriter.TurnHandle handle = new()
        {

            AssistantEntryId = Guid.NewGuid(),

        };

        await writer.ResolveInterruptedAsync(handle, "partial", CancellationToken.None);

        Assert.Equal(1, grimoire.FinalizeCallCount);

        Assert.Equal(0, grimoire.DiscardCallCount);

    }

    [Fact]
    public async Task ResolveInterruptedAsync_WithoutContent_DiscardsEntry()
    {

        TrackingGrimoireRepository grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire);

        GrimoireTurnWriter.TurnHandle handle = new()
        {

            AssistantEntryId = Guid.NewGuid(),

        };

        await writer.ResolveInterruptedAsync(handle, null, CancellationToken.None);

        Assert.Equal(0, grimoire.FinalizeCallCount);

        Assert.Equal(1, grimoire.DiscardCallCount);

    }

    [Fact]
    public async Task ResolveInterruptedAndMarkFinalizedAsync_SetsFinalizedFlag()
    {

        TrackingGrimoireRepository grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire);

        GrimoireTurnWriter.TurnHandle handle = new()
        {

            AssistantEntryId = Guid.NewGuid(),

        };

        await writer.ResolveInterruptedAndMarkFinalizedAsync(handle, null, CancellationToken.None);

        Assert.True(handle.IsFinalized);

    }

    [Fact]
    public async Task TryResolveInterruptedOnStreamExitAsync_UsesNonCancellableToken()
    {

        TrackingGrimoireRepository grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire);

        GrimoireTurnWriter.TurnHandle handle = new()
        {

            AssistantEntryId = Guid.NewGuid(),

        };

        using CancellationTokenSource cancelled = new();

        cancelled.Cancel();

        await writer.TryResolveInterruptedOnStreamExitAsync(handle, null);

        Assert.Equal(CancellationToken.None, grimoire.LastDiscardToken);

    }

    private static GrimoireTurnWriter CreateWriter(IGrimoireRepository grimoire) =>
        new(
            grimoire,
            new SessionEventHub(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), NullLogger<SessionEventHub>.Instance),
            NullLogger<GrimoireTurnWriter>.Instance);

    private sealed class TrackingGrimoireRepository : IGrimoireRepository
    {

        public Guid? FixedSessionId { get; init; }

        public int BeginCallCount { get; private set; }

        public int FinalizeCallCount { get; private set; }

        public int DiscardCallCount { get; private set; }

        public int RecentEntriesPublishCount { get; private set; }

        public int EntryByIdPublishCount { get; private set; }

        public CancellationToken LastDiscardToken { get; private set; }

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
            Guid? sessionId,
            string prompt,
            string model,
            CancellationToken cancellationToken = default)
        {

            BeginCallCount++;

            return Task.FromResult((FixedSessionId ?? sessionId ?? Guid.NewGuid(), Guid.NewGuid()));

        }

        public Task FinalizeAssistantEntryAsync(Guid assistantEntryId, string fullContent, CancellationToken cancellationToken = default)
        {

            FinalizeCallCount++;

            return Task.CompletedTask;

        }

        public Task DiscardAssistantEntryAsync(Guid assistantEntryId, CancellationToken cancellationToken = default)
        {

            DiscardCallCount++;

            LastDiscardToken = cancellationToken;

            return Task.CompletedTask;

        }

        public Task AppendToolInteractionAsync(
            Guid sessionId,
            string toolName,
            string arguments,
            string result,
            string modelUsed,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveCompletedExchangeAsync(string userPrompt, string assistantText, string modelUsed, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> PurgeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<List<GrimoireEntryDto>?>(null);

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(Guid sessionId, int takeLast, CancellationToken cancellationToken = default)
        {

            RecentEntriesPublishCount++;

            return Task.FromResult<List<GrimoireEntryDto>?>(null);

        }

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default)
        {

            EntryByIdPublishCount++;

            return Task.FromResult<GrimoireEntryDto?>(null);

        }

        public Task<bool> DeleteEntryAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> SetEntryPinnedAsync(Guid sessionId, Guid entryId, bool pinned, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> GetPinnedEntryCountAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(int threshold, DateTime idleCutoff, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Guid>());

        public Task<List<Entry>> GetUnsummarizedEntriesAsync(Guid sessionId, DateTime watermark, int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Entry>());

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task IncrementSessionTokensAsync(Guid sessionId, long totalTokens, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task IncrementSessionTokensAndCostAsync(Guid sessionId, long totalTokens, decimal costUsd, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateSessionCampaignRollupAsync(Guid sessionId, string summary, DateTime lastSummarizedMessageAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LoreDto(key, value, DateTime.UtcNow));

        public Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<ListPageResult<LoreDto>> ListLoreAsync(int? limit = null, int offset = 0, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<LoreDto>([], false));

        public Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<LoreDto?>(null);

        public Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(string workspacePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkspaceContext?>(null);

        public Task<Session?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Session?>(null);

        public Task<Session?> GetSessionHeaderAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Session?>(null);

    }

}
