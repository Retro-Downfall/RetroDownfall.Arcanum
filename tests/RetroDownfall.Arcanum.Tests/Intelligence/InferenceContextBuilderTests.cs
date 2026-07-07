using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Tests.Support;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class InferenceContextBuilderTests
{

    [Fact]
    public void HasStatelessMessages_WhenMessagesPresent_ReturnsTrue()
    {

        PingRequest request = new(
            Prompt: "x",
            Model: "m",
            WorkingDirectory: string.Empty,
            StatelessMessages: [new CoreChatMessage("user", "hi")]);

        Assert.True(InferenceContextBuilder.HasStatelessMessages(request));

    }

    [Fact]
    public void MapGrimoireToMeAiMessages_StripsTrailingEmptyAssistant()
    {

        Session session = new()
        {

            Id = Guid.NewGuid(),

            Entries =
            [

                new Entry { Role = MessageRole.User, Content = "q", CreatedAt = DateTimeOffset.UtcNow },

                new Entry { Role = MessageRole.Assistant, Content = string.Empty, CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1) },

            ],

        };

        List<MeAiChatMessage> messages = InferenceContextBuilder.MapGrimoireToMeAiMessages(session, "next");

        Assert.Equal(2, messages.Count);

        Assert.Equal(ChatRole.User, messages[0].Role);

        Assert.Equal("q", messages[0].Text);

        Assert.Equal(ChatRole.User, messages[1].Role);

        Assert.Equal("next", messages[1].Text);

    }

    [Fact]
    public void ComputeEffectiveCompressionLimit_MatchesThresholdMath()
    {

        int limit = InferenceContextBuilder.ComputeEffectiveCompressionLimit(8192, 80);

        Assert.Equal(6553, limit);

    }

    [Fact]
    public void BuildInitialMeAiChatMessages_Stateless_MapsRoles()
    {

        PingRequest request = new(
            Prompt: "ignored",
            Model: "m",
            WorkingDirectory: string.Empty,
            StatelessMessages:
            [
                new CoreChatMessage("system", "sys"),
                new CoreChatMessage("user", "hello"),
            ]);

        List<MeAiChatMessage> messages = InferenceContextBuilder.BuildInitialMeAiChatMessages(request, null, "ignored");

        Assert.Equal(2, messages.Count);

        Assert.Equal(ChatRole.System, messages[0].Role);

        Assert.Equal(ChatRole.User, messages[1].Role);

    }

    [Fact]
    public async Task LoadThreadAsync_StatelessRequest_ReturnsNull()
    {

        InferenceContextBuilder builder = CreateBuilder(new NullGrimoireRepository());

        Session? thread = await builder.LoadThreadAsync(
            new PingRequest(
                Prompt: "x",
                Model: "m",
                WorkingDirectory: string.Empty,
                StatelessMessages: [new CoreChatMessage("user", "hi")]),
            CancellationToken.None);

        Assert.Null(thread);

    }

    private static InferenceContextBuilder CreateBuilder(IGrimoireRepository grimoire)
    {

        ArcanumSettings settings = new();

        ManaPreflight manaPreflight = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        InferenceTokenizerResolver tokenizerResolver = new(NullLogger<InferenceTokenizerResolver>.Instance);

        IContextCompressionService compression = new ContextCompressionService(
            grimoire,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            manaPreflight,
            tokenizerResolver,
            NullLogger<ContextCompressionService>.Instance);

        return new InferenceContextBuilder(
            grimoire,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            NullLogger<InferenceContextBuilder>.Instance,
            manaPreflight,
            compression);

    }

    private sealed class NullGrimoireRepository : IGrimoireRepository
    {

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
            Guid? sessionId,
            string prompt,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task FinalizeAssistantEntryAsync(Guid assistantEntryId, string fullContent, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task DiscardAssistantEntryAsync(Guid assistantEntryId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task AppendToolInteractionAsync(
            Guid sessionId,
            string toolName,
            string arguments,
            string result,
            string modelUsed,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task SaveCompletedExchangeAsync(string userPrompt, string assistantText, string modelUsed, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> PurgeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(Guid sessionId, int takeLast, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteEntryAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> SetEntryPinnedAsync(Guid sessionId, Guid entryId, bool pinned, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> GetPinnedEntryCountAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(int threshold, DateTime idleCutoff, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<Entry>> GetUnsummarizedEntriesAsync(Guid sessionId, DateTime watermark, int batchSize, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task IncrementSessionTokensAsync(Guid sessionId, long totalTokens, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task IncrementSessionTokensAndCostAsync(Guid sessionId, long totalTokens, decimal costUsd, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task UpdateSessionCampaignRollupAsync(Guid sessionId, string summary, DateTime lastSummarizedMessageAt, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ListPageResult<LoreDto>> ListLoreAsync(int? limit = null, int offset = 0, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(string workspacePath, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Session?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Session?>(null);

        public Task<Session?> GetSessionHeaderAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Session?>(null);

    }

}
