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

public sealed partial class BudgetMonitorTests
{
    [Fact]
    public async Task A_stale_precheck_does_not_bypass_insert_before_notification()
    {
        ControlledAlerts alerts = new(_ => Task.FromResult(false));

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(CreateScopeFactory(new TrackingGrimoireRepository { TodaySpend = 8m }, alerts),
            commLink, new TestOptionsMonitor<ArcanumSettings>(Settings(new() { Enabled = true, DailyLimitUsd = 10m })),
            NullLogger<BudgetMonitor>.Instance);

        for (int check = 0; check < 4; check++) Assert.True((await monitor.CheckAsync()).IsSuccess);

        Assert.Equal(4, alerts.RecordCalls);

        Assert.Single(commLink.Dispatched);
    }

    [Fact]
    public async Task Cancellation_during_the_precheck_never_records_or_dispatches()
    {
        using CancellationTokenSource cancellation = new();

        ControlledAlerts alerts = new(token =>
        {
            cancellation.Cancel();

            return Task.FromCanceled<bool>(token);
        });

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(CreateScopeFactory(new TrackingGrimoireRepository { TodaySpend = 8m }, alerts),
            commLink, new TestOptionsMonitor<ArcanumSettings>(Settings(new() { Enabled = true, DailyLimitUsd = 10m })),
            NullLogger<BudgetMonitor>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.CheckAsync(cancellation.Token));

        Assert.Equal(0, alerts.RecordCalls);

        Assert.Empty(commLink.Dispatched);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public async Task Repeated_checks_above_threshold_skip_the_insert_after_the_first_alert(int spend)
    {
        FakeBudgetAlertRepository alerts = new();

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(CreateScopeFactory(new TrackingGrimoireRepository { TodaySpend = spend }, alerts),
            commLink, new TestOptionsMonitor<ArcanumSettings>(Settings(new() { Enabled = true, DailyLimitUsd = 10m })),
            NullLogger<BudgetMonitor>.Instance);

        for (int check = 0; check < 5; check++)
        {
            Result result = await monitor.CheckAsync();

            Assert.Equal(spend < 10, result.IsSuccess);
        }

        Assert.Equal(1, alerts.RecordCalls);

        Assert.Equal(5, alerts.PrecheckCalls);

        Assert.Single(commLink.Dispatched);

        // A later UTC day's repository observation is authoritative; no singleton cache can hide it.
        alerts.AlertedThresholdsToday.Clear();

        _ = await monitor.CheckAsync();

        Assert.Equal(2, alerts.RecordCalls);

        Assert.Equal(2, commLink.Dispatched.Count);
    }

    [Fact]
    public async Task CheckAsync_Disabled_ReturnsSuccessWithoutCheckingSpend()
    {
        BudgetPolicySettings budget = new() { Enabled = false, DailyLimitUsd = 10m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 999m };

        FakeBudgetAlertRepository alerts = new();

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, alerts),
            commLink,
            new TestOptionsMonitor<ArcanumSettings>(Settings(budget)),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Empty(commLink.Dispatched);

        Assert.Equal(0, grimoire.TodaySpendCalls);
    }

    [Fact]
    public async Task CheckAsync_ZeroLimit_ReturnsSuccess()
    {
        BudgetPolicySettings budget = new() { Enabled = true, DailyLimitUsd = 0m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 5m };

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, new FakeBudgetAlertRepository()),
            new FakeCommLinkDispatcher(),
            new TestOptionsMonitor<ArcanumSettings>(Settings(budget)),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(0, grimoire.TodaySpendCalls);
    }

    [Fact]
    public async Task CheckAsync_SpendAtLimit_ReturnsExceededFailure()
    {
        BudgetPolicySettings budget = new() { Enabled = true, DailyLimitUsd = 10m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 10m };

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, new FakeBudgetAlertRepository()),
            commLink,
            new TestOptionsMonitor<ArcanumSettings>(Settings(budget)),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);
        Assert.Contains("00:00 UTC", result.Error.Message);
        Assert.Contains("Arcanum:Cost:Budget:DailyLimitUsd", result.Error.Message);
        Assert.Contains("Outstanding reservations from prior days may still count", result.Error.Message);

        Assert.Contains(commLink.Dispatched, m => m.Severity == CommLinkSeverity.Critical);
    }

    [Fact]
    public async Task CheckAsync_SpendAboveLimit_ReturnsExceededFailure()
    {
        BudgetPolicySettings budget = new() { Enabled = true, DailyLimitUsd = 10m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 15m };

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, new FakeBudgetAlertRepository()),
            new FakeCommLinkDispatcher(),
            new TestOptionsMonitor<ArcanumSettings>(Settings(budget)),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);
        Assert.Contains("00:00 UTC", result.Error.Message);
        Assert.Contains("Arcanum:Cost:Budget:DailyLimitUsd", result.Error.Message);
        Assert.Contains("Outstanding reservations from prior days may still count", result.Error.Message);
    }

    [Fact]
    public async Task CheckAsync_SpendAboveAlertThreshold_DispatchesWarningAndRecords()
    {
        BudgetPolicySettings budget = new() { Enabled = true, DailyLimitUsd = 10m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 8m };

        FakeBudgetAlertRepository alerts = new();

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, alerts),
            commLink,
            new TestOptionsMonitor<ArcanumSettings>(Settings(budget)),
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
        BudgetPolicySettings budget = new() { Enabled = true, DailyLimitUsd = 10m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 8m };

        FakeBudgetAlertRepository alerts = new();

        alerts.AlertedThresholdsToday.Add(80);

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, alerts),
            commLink,
            new TestOptionsMonitor<ArcanumSettings>(Settings(budget)),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Empty(commLink.Dispatched);
    }

    [Fact]
    public async Task CheckAsync_SpendBelowThreshold_ReturnsSuccessWithoutAlert()
    {
        BudgetPolicySettings budget = new() { Enabled = true, DailyLimitUsd = 10m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 5m };

        FakeBudgetAlertRepository alerts = new();

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, alerts),
            commLink,
            new TestOptionsMonitor<ArcanumSettings>(Settings(budget)),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Empty(commLink.Dispatched);

        Assert.Empty(alerts.AlertedThresholdsToday);
    }

    private static ArcanumSettings Settings(BudgetPolicySettings budget) =>
        new()
        {
            Cost = new CostSettings
            {
                Budget = budget,
            },
        };

    // ── #69 delegated spend counts against the ceiling ─────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_CeilingReachedPartlyThroughDelegation_ReturnsExceeded()
    {
        BudgetPolicySettings budget = new() { Enabled = true, DailyLimitUsd = 10m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 6m };

        FakeCommLinkDispatcher commLink = new();

        BudgetMonitor monitor = new(
            CreateScopeFactory(
                grimoire,
                new FakeBudgetAlertRepository(),
                new StubExternalSpendLedger(new ExternalSpendSummary(4m, 1_000, 2, 0))),
            commLink,
            new TestOptionsMonitor<ArcanumSettings>(Settings(budget)),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        // Local spend alone is under the limit. An operator who can delegate past a spend cap does not
        // have a spend cap (issue #69).
        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);
        Assert.Contains("00:00 UTC", result.Error.Message);
        Assert.Contains("Arcanum:Cost:Budget:DailyLimitUsd", result.Error.Message);
        Assert.Contains("Outstanding reservations from prior days may still count", result.Error.Message);
    }

    [Fact]
    public async Task CheckAsync_UnpricedDelegation_IsAllowedAndFlaggedRatherThanCharged()
    {
        BudgetPolicySettings budget = new() { Enabled = true, DailyLimitUsd = 10m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 3m };

        BudgetMonitor monitor = new(
            CreateScopeFactory(
                grimoire,
                new FakeBudgetAlertRepository(),
                new StubExternalSpendLedger(new ExternalSpendSummary(0m, 0, 0, 3))),
            new FakeCommLinkDispatcher(),
            new TestOptionsMonitor<ArcanumSettings>(Settings(budget)),
            NullLogger<BudgetMonitor>.Instance);

        // Allow-and-flag: three unpriced Sendings are neither charged as zero nor treated as a refusal.
        Assert.True((await monitor.CheckAsync(CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task CheckAsync_AtTheCeilingWithUnpricedDelegation_SaysTheFigureIsAFloor()
    {
        BudgetPolicySettings budget = new() { Enabled = true, DailyLimitUsd = 10m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 10m };

        BudgetMonitor monitor = new(
            CreateScopeFactory(
                grimoire,
                new FakeBudgetAlertRepository(),
                new StubExternalSpendLedger(new ExternalSpendSummary(0m, 0, 0, 2))),
            new FakeCommLinkDispatcher(),
            new TestOptionsMonitor<ArcanumSettings>(Settings(budget)),
            NullLogger<BudgetMonitor>.Instance);

        Result result = await monitor.CheckAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        // "Do not silently allow": the operator is told the number they hit the ceiling on is incomplete.
        Assert.Contains("reported no cost", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_WithoutAnExternalSpendLedger_BehavesExactlyAsBefore()
    {
        BudgetPolicySettings budget = new() { Enabled = true, DailyLimitUsd = 10m };

        TrackingGrimoireRepository grimoire = new() { TodaySpend = 1m };

        BudgetMonitor monitor = new(
            CreateScopeFactory(grimoire, new FakeBudgetAlertRepository()),
            new FakeCommLinkDispatcher(),
            new TestOptionsMonitor<ArcanumSettings>(Settings(budget)),
            NullLogger<BudgetMonitor>.Instance);

        Assert.True((await monitor.CheckAsync(CancellationToken.None)).IsSuccess);
    }

    private static IServiceScopeFactory CreateScopeFactory(
        IGrimoireRepository grimoire,
        IBudgetAlertRepository budgetAlerts,
        IExternalSpendLedger? externalSpend = null)
    {
        ServiceCollection services = new();

        services.AddScoped(_ => grimoire);

        services.AddScoped(_ => budgetAlerts);

        if (externalSpend is not null)
        {
            services.AddScoped(_ => externalSpend);
        }

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class ControlledAlerts(Func<CancellationToken, Task<bool>> precheck) : IBudgetAlertRepository
    {
        public int RecordCalls { get; private set; }

        public Task<bool> HasAlertedTodayAsync(int threshold, CancellationToken cancellationToken = default) => precheck(cancellationToken);

        public Task<bool> RecordAlertAsync(int threshold, decimal spendUsd, decimal dailyLimitUsd, CancellationToken cancellationToken = default) =>
            Task.FromResult(++RecordCalls == 1);
    }

    private sealed class StubExternalSpendLedger(ExternalSpendSummary summary) : IExternalSpendLedger
    {
        public Task<ExternalSpendSummary> GetTodayAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(summary);
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

        public Task<Result<string>> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(string workspacePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
