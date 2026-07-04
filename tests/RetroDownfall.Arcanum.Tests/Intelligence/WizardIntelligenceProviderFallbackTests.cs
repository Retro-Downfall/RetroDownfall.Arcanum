using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Resilience;
using RetroDownfall.Arcanum.Tests.Support;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Direct-construction fallback-loop tests for <see cref="WizardIntelligenceProvider"/>, mirroring the
/// pattern in <c>WizardIntelligenceProviderTests.cs</c> (own scripted <see cref="IChatClientFactory"/>
/// double + a real <see cref="ProviderHealthTracker"/>) rather than the HTTP <c>ArcanumWebApplicationFactory</c>
/// path, since that fixture unconditionally substitutes <see cref="IArcanumIntelligenceProvider"/> with a fake.
/// </summary>
public sealed class WizardIntelligenceProviderFallbackTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    private const string ModelName = "wizard-fallback-test-model";

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public async Task ExecutePromptAsync_retries_on_connectivity_failure()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");

        ScriptingChatClient chatB = new();

        chatB.EnqueueText("answer from B");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("answer from B", result.Value!.Text);

        Assert.Equal([providerA.Name, providerB.Name], factory.CandidateCallOrder);

        Assert.False(tracker.IsHealthy(providerA.Name));

        Assert.True(tracker.IsHealthy(providerB.Name));

    }

    [Fact]
    public async Task ExecutePromptAsync_stops_after_max_attempts()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        ProviderSettings providerC = MakeProvider("provider-c");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");

        factory.CandidateExceptions[providerB.Name] = new HttpRequestException("connection refused");

        factory.CandidateExceptions[providerC.Name] = new HttpRequestException("connection refused");

        ProviderHealthTracker tracker = CreateTracker();

        WizardIntelligenceProvider wizard = CreateWizard(
            factory,
            tracker,
            maxFallbackAttempts: 2,
            providerA,
            providerB,
            providerC);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal([providerA.Name, providerB.Name], factory.CandidateCallOrder);

    }

    [Fact]
    public async Task ExecutePromptAsync_does_not_retry_on_model_error()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        ScriptingChatClient chatA = new();

        chatA.EnqueueException(new InvalidOperationException("content filter rejected the request"));

        factory.CandidateResolvers[providerA.Name] = () => MakeLease(chatA, providerA);

        ScriptingChatClient chatB = new();

        chatB.EnqueueText("should never be reached");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker();

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal([providerA.Name], factory.CandidateCallOrder);

        Assert.True(tracker.IsHealthy(providerA.Name));

    }

    [Fact]
    public async Task StreamPromptAsync_retries_pre_stream_failure()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");

        ScriptingChatClient chatB = new();

        chatB.EnqueueStreamTokens("he", "llo");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        List<IntelligenceEvent> events = await CollectStreamAsync(wizard, BaseRequest());

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Token && e.Data == "he");

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Token && e.Data == "llo");

        Assert.DoesNotContain(events, static e => e.Type == IntelligenceEventType.Error);

        Assert.Equal([providerA.Name, providerB.Name], factory.CandidateCallOrder);

        Assert.False(tracker.IsHealthy(providerA.Name));

        Assert.True(tracker.IsHealthy(providerB.Name));

    }

    [Fact]
    public async Task Marks_provider_unhealthy_on_failure()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");

        ScriptingChatClient chatB = new();

        chatB.EnqueueText("answer from B");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 1);

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        await wizard.ExecutePromptAsync(BaseRequest(), CancellationToken.None);

        Assert.False(tracker.IsHealthy(providerA.Name));

    }

    [Fact]
    public async Task Marks_provider_healthy_on_success()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        ProviderSettings providerB = MakeProvider("provider-b");

        RecordingChatClientFactory factory = new();

        factory.CandidateExceptions[providerA.Name] = new HttpRequestException("connection refused");

        ScriptingChatClient chatB = new();

        chatB.EnqueueText("answer from B");

        factory.CandidateResolvers[providerB.Name] = () => MakeLease(chatB, providerB);

        ProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 3);

        // Pre-seed providerB with a prior (degraded, still-healthy) failure to prove success resets it.
        tracker.MarkFailed(providerB.Name);

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, providerA, providerB);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(BaseRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess);

        ProviderHealthStatus statusB = Assert.Single(tracker.GetAllStatuses(), s => s.ProviderName == providerB.Name);

        Assert.True(statusB.IsHealthy);

        Assert.Equal(0, statusB.ConsecutiveFailures);

    }

    [Fact]
    public async Task Resilience_disabled_skips_fallback()
    {

        ProviderSettings providerA = MakeProvider("provider-a");

        RecordingChatClientFactory factory = new();

        // The non-resilience single-resolution path only catches InvalidOperationException (the
        // ProviderResolver "no model resolved" contract) — matches the disabled-path production code.
        factory.SingleCallException = new InvalidOperationException("No AI model could be resolved.");

        ProviderHealthTracker tracker = CreateTracker();

        WizardIntelligenceProvider wizard = CreateWizard(factory, tracker, resilienceEnabled: false, providerA);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(BaseRequest(), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, factory.SingleCallCount);

        Assert.Empty(factory.CandidateCallOrder);

        Assert.Empty(tracker.GetAllStatuses());

    }

    // ===== Helpers =====

    private static ProviderHealthTracker CreateTracker(int healthFailureThreshold = 3) =>
        new(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings
            {
                Resilience = new ResilienceSettings { HealthFailureThreshold = healthFailureThreshold },
            }),
            NullLogger<ProviderHealthTracker>.Instance);

    private static ProviderSettings MakeProvider(string name) => new()
    {
        Name = name,
        Type = AiProviderKind.OpenAICompatible,
        Endpoint = "https://example.test/v1",
        Models = [ModelName],
        ContextWindowLimit = 8192,
    };

    private static ChatClientLease MakeLease(ScriptingChatClient chatClient, ProviderSettings provider) =>
        new(chatClient, ollamaApi: null, provider, ModelName, isOllama: false, ownedHttpClient: null);

    private static PingRequest BaseRequest() =>
        new(Prompt: "hello", Model: ModelName, WorkingDirectory: string.Empty, SkipSpellRouting: true, DisableMcpTools: true);

    private static WizardIntelligenceProvider CreateWizard(
        IChatClientFactory factory,
        IProviderHealthTracker? healthTracker,
        params ProviderSettings[] providers) =>
        CreateWizard(factory, healthTracker, resilienceEnabled: true, maxFallbackAttempts: 3, providers);

    private static WizardIntelligenceProvider CreateWizard(
        IChatClientFactory factory,
        IProviderHealthTracker? healthTracker,
        int maxFallbackAttempts,
        params ProviderSettings[] providers) =>
        CreateWizard(factory, healthTracker, resilienceEnabled: true, maxFallbackAttempts, providers);

    private static WizardIntelligenceProvider CreateWizard(
        IChatClientFactory factory,
        IProviderHealthTracker? healthTracker,
        bool resilienceEnabled,
        params ProviderSettings[] providers) =>
        CreateWizard(factory, healthTracker, resilienceEnabled, maxFallbackAttempts: 3, providers);

    private static WizardIntelligenceProvider CreateWizard(
        IChatClientFactory factory,
        IProviderHealthTracker? healthTracker,
        bool resilienceEnabled,
        int maxFallbackAttempts,
        params ProviderSettings[] providers)
    {

        ArcanumSettings settings = new()
        {

            DefaultModel = ModelName,

            Providers = providers,

            Resilience = new ResilienceSettings
            {
                Enabled = resilienceEnabled,
                MaxFallbackAttempts = maxFallbackAttempts,
            },

        };

        FakeGrimoireRepository grimoire = new();

        FakeWard ward = new();

        FakeMcpConnectionManager mcp = new();

        FakeCampaignRepository campaignRepository = new();

        ConfigurableSanctumGuard sanctumGuard = new();

        return new WizardIntelligenceProvider(
            factory,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            NullLogger<WizardIntelligenceProvider>.Instance,
            grimoire,
            mcp,
            campaignRepository,
            new ToolExecutionPipeline(
                new TestOptionsSnapshot<ArcanumSettings>(settings),
                ward,
                sanctumGuard,
                NullLogger<ToolExecutionPipeline>.Instance),
            new GrimoireTurnWriter(
                grimoire,
                new SessionEventHub(new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<SessionEventHub>.Instance),
                NullLogger<GrimoireTurnWriter>.Instance),
            new InferenceContextBuilder(
                grimoire,
                new TestOptionsSnapshot<ArcanumSettings>(settings),
                NullLogger<InferenceContextBuilder>.Instance,
                new ManaPreflight(new TestOptionsMonitor<ArcanumSettings>(settings)),
                new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)),
            sanctumGuard,
            new ProcessResourceLimiter(),
            healthTracker);

    }

    private static async Task<List<IntelligenceEvent>> CollectStreamAsync(
        WizardIntelligenceProvider wizard,
        PingRequest request)
    {
        List<IntelligenceEvent> events = [];

        await foreach (IntelligenceEvent evt in wizard.StreamPromptAsync(request, CancellationToken.None))
        {
            events.Add(evt);
        }

        return events;
    }

    // ===== Test doubles =====

    private sealed class RecordingChatClientFactory : IChatClientFactory
    {

        public List<string> CandidateCallOrder { get; } = [];

        public Dictionary<string, Func<ChatClientLease>> CandidateResolvers { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Exception> CandidateExceptions { get; } = new(StringComparer.Ordinal);

        public int SingleCallCount { get; private set; }

        public Exception? SingleCallException { get; set; }

        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken)
        {

            SingleCallCount++;

            if (SingleCallException is not null)
            {
                throw SingleCallException;
            }

            throw new InvalidOperationException("No AI model could be resolved.");

        }

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken)
        {

            CandidateCallOrder.Add(provider.Name);

            if (CandidateExceptions.TryGetValue(provider.Name, out Exception? ex))
            {
                throw ex;
            }

            if (CandidateResolvers.TryGetValue(provider.Name, out Func<ChatClientLease>? resolver))
            {
                return Task.FromResult(resolver());
            }

            throw new InvalidOperationException($"No resolver configured for provider '{provider.Name}'.");

        }

    }

    private sealed class ScriptingChatClient : IChatClient
    {

        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _buffered = new();

        private readonly Queue<Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>> _streaming = new();

        public void EnqueueText(string text) =>
            _buffered.Enqueue(_ => Task.FromResult(new ChatResponse(new MeAiChatMessage(ChatRole.Assistant, text))));

        public void EnqueueException(Exception ex) =>
            _buffered.Enqueue(_ => throw ex);

        public void EnqueueStreamTokens(params string[] tokens) =>
            _streaming.Enqueue(_ => StreamTokens(tokens));

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {

            if (_buffered.Count == 0)
            {
                throw new InvalidOperationException("No scripted buffered response remaining.");
            }

            return _buffered.Dequeue()(cancellationToken);

        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {

            if (_streaming.Count == 0)
            {
                throw new InvalidOperationException("No scripted streaming response remaining.");
            }

            return _streaming.Dequeue()(cancellationToken);

        }

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamTokens(
            IEnumerable<string> tokens,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (string token in tokens)
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return new ChatResponseUpdate(ChatRole.Assistant, token);

                await Task.Yield();
            }
        }

    }

    private sealed class FakeGrimoireRepository : IGrimoireRepository
    {

        public Task<Session?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Session?>(null);

        public Task<Session?> GetSessionHeaderAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Session?>(null);

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
            Guid? sessionId,
            string prompt,
            string model,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((sessionId ?? Guid.NewGuid(), Guid.NewGuid()));

        public Task FinalizeAssistantEntryAsync(Guid assistantEntryId, string fullContent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DiscardAssistantEntryAsync(Guid assistantEntryId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(Guid sessionId, int takeLast, CancellationToken cancellationToken = default) =>
            Task.FromResult<List<GrimoireEntryDto>?>(null);

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GrimoireEntryDto?>(null);

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(int threshold, DateTime idleCutoff, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Guid>());

        public Task<List<Entry>> GetUnsummarizedEntriesAsync(Guid sessionId, DateTime watermark, int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Entry>());

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task IncrementSessionTokensAsync(Guid sessionId, long totalTokens, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

    }

    private sealed class FakeWard : IWard
    {

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WardResolution(true, null, DateTimeOffset.UtcNow));

        public ResolveStatus Resolve(string wardId, bool allow, string? reason) =>
            ResolveStatus.Success;

        public IReadOnlyList<ActiveWard> GetActiveWards() => [];

    }

    private sealed class FakeMcpConnectionManager : IMcpConnectionManager
    {

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> StartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> StopAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> RestartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<McpServerInfo?> GetStatusAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<McpServerInfo?>(null);

        public Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<McpServerInfo>());

        public Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AITool>>([]);

        public Task<List<McpServerStatusDto>> GetServerStatusesAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<McpServerStatusDto>());

        public Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> TrustWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

    }

    private sealed class FakeCampaignRepository : ICampaignRepository
    {

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            Core.Workspaces.WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Campaign> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

    }

    private sealed class ConfigurableSanctumGuard : ISanctumGuard
    {

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(string? workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            Core.Platform.ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

}
