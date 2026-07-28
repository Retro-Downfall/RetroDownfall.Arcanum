using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class BudgetMonitorTests
{

    [Fact]
    public async Task CheckAsync_Disabled_ReturnsSuccessWithoutCheckingSpend()
    {

        BudgetSettings budget = new() { Enabled = false, DailyLimitUsd = 10m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 999m };

        FakeBudgetAlertRepository alerts = new();

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, alerts),
            commLink,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Budget = budget }),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Empty(commLink.Dispatched);

        Assert.Equal(0, grimoire.TodaySpendCalls);

    }

    [Fact]
    public async Task CheckAsync_ZeroLimit_ReturnsSuccess()
    {

        BudgetSettings budget = new() { Enabled = true, DailyLimitUsd = 0m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 5m };

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, new FakeBudgetAlertRepository()),
            new FakeCommLinkDispatcher(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Budget = budget }),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(0, grimoire.TodaySpendCalls);

    }

    [Fact]
    public async Task CheckAsync_SpendAtLimit_ReturnsExceededFailure()
    {

        BudgetSettings budget = new() { Enabled = true, DailyLimitUsd = 10m, AlertThresholdPercent = 80 };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 10m };

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, new FakeBudgetAlertRepository()),
            commLink,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Budget = budget }),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);

        Assert.Contains(commLink.Dispatched, m => m.Severity == CommLinkSeverity.Critical);

    }

    [Fact]
    public async Task CheckAsync_SpendAboveLimit_ReturnsExceededFailure()
    {

        BudgetSettings budget = new() { Enabled = true, DailyLimitUsd = 10m, AlertThresholdPercent = 80 };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 15m };

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, new FakeBudgetAlertRepository()),
            new FakeCommLinkDispatcher(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Budget = budget }),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);

    }

    [Fact]
    public async Task CheckAsync_SpendAboveAlertThreshold_DispatchesWarningAndRecords()
    {

        BudgetSettings budget = new() { Enabled = true, DailyLimitUsd = 10m, AlertThresholdPercent = 80 };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 8m };

        FakeBudgetAlertRepository alerts = new();

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, alerts),
            commLink,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Budget = budget }),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Single(commLink.Dispatched);

        Assert.Equal(CommLinkSeverity.Warning, commLink.Dispatched[0].Severity);

        Assert.Contains(alerts.AlertedThresholdsToday, t => t == 80);

    }

    [Fact]
    public async Task CheckAsync_AlreadyAlertedToday_DoesNotRedispatch()
    {

        BudgetSettings budget = new() { Enabled = true, DailyLimitUsd = 10m, AlertThresholdPercent = 80 };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 8m };

        FakeBudgetAlertRepository alerts = new();

        alerts.AlertedThresholdsToday.Add(80);

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, alerts),
            commLink,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Budget = budget }),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Empty(commLink.Dispatched);

    }

    [Fact]
    public async Task CheckAsync_SpendBelowThreshold_ReturnsSuccessWithoutAlert()
    {

        BudgetSettings budget = new() { Enabled = true, DailyLimitUsd = 10m, AlertThresholdPercent = 80 };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 5m };

        FakeBudgetAlertRepository alerts = new();

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, alerts),
            commLink,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Budget = budget }),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Empty(commLink.Dispatched);

        Assert.Empty(alerts.AlertedThresholdsToday);

    }

    private static IServiceScopeFactory CreateScopeFactory(
        IGrimoireRepository grimoire,
        IBudgetAlertRepository budgetAlerts)
    {

        ServiceCollection services = new();

        services.AddScoped(_ => grimoire);

        services.AddScoped(_ => budgetAlerts);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    }

    private sealed class TrackingGrimoireRepository : IGrimoireRepository
    {

        public decimal TodaySpend { get; set; }

        public int TodaySpendCalls { get; private set; }

        public Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default)
        {
            TodaySpendCalls++;
            return Task.FromResult(TodaySpend);
        }

        // The remaining IGrimoireRepository members are not exercised by BudgetMonitor.

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(Guid? sessionId, string prompt, string model, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task FinalizeAssistantEntryAsync(Guid assistantEntryId, string fullContent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DiscardAssistantEntryAsync(Guid assistantEntryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AppendToolInteractionAsync(Guid sessionId, string toolName, string arguments, string result, string modelUsed, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveCompletedExchangeAsync(string userPrompt, string assistantText, string modelUsed, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> PurgeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Core.Storage.Entities.Session?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Core.Storage.Entities.Session?> GetSessionHeaderAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(Guid sessionId, int takeLast, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteEntryAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SetEntryPinnedAsync(Guid sessionId, Guid entryId, bool pinned, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetPinnedEntryCountAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(int threshold, DateTime idleCutoff, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<Core.Storage.Entities.Entry>> GetUnsummarizedEntriesAsync(Guid sessionId, DateTime watermark, int batchSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task IncrementSessionTokensAsync(Guid sessionId, long totalTokens, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task IncrementSessionTokensAndCostAsync(Guid sessionId, long totalTokens, decimal costUsd, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateSessionCampaignRollupAsync(Guid sessionId, string summary, DateTime lastSummarizedMessageAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ListPageResult<LoreDto>> ListLoreAsync(int? limit = null, int offset = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(string workspacePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

}
