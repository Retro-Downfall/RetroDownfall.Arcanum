using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class BudgetEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public BudgetEndpointTests(ArcanumWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetBudget_Disabled_ReturnsEnabledFalseAndZeroSpend()
    {

        await using ArcanumWebApplicationFactory factory = _factory.WithBudget(
            new BudgetPolicySettings { Enabled = false, DailyLimitUsd = 10m },
            todaySpend: 999m);

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/budget");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<BudgetSummaryDto>? body = await response.Content.ReadFromJsonAsync(
            ArcanumJsonContext.Default.ApiResponseBudgetSummaryDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.False(body.Data!.Enabled);

        Assert.Equal(0m, body.Data.TodaySpendUsd);

    }

    [Fact]
    public async Task GetBudget_DistinguishesLocalFromDelegatedSpendAndFlagsUnpricedSendings()
    {

        await using ArcanumWebApplicationFactory factory = _factory.WithBudget(
            new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 20m },
            todaySpend: 5m,
            externalSpend: new ExternalSpendSummary(3m, 900, 2, 1));

        HttpClient client = factory.CreateAuthenticatedClient();

        ApiResponse<BudgetSummaryDto>? body = await (await client.GetAsync("/api/budget")).Content
            .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseBudgetSummaryDto);

        BudgetSummaryDto summary = body!.Data!;

        Assert.Equal(5m, summary.LocalSpendUsd);

        Assert.Equal(3m, summary.ExternalSpendUsd);

        Assert.Equal(8m, summary.TodaySpendUsd);

        Assert.Equal(12m, summary.RemainingUsd);

        // The unpriced Sending is counted, never costed: adding it at zero would make the total look
        // complete when part of the day's delegated work has no price at all (issue #69).
        Assert.Equal(1, summary.UnpricedDelegatedSendings);

    }

    [Fact]
    public async Task GetBudget_Enabled_ReturnsSpendAndRemaining()
    {

        await using ArcanumWebApplicationFactory factory = _factory.WithBudget(
            new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 20m },
            todaySpend: 5m);

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/budget");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<BudgetSummaryDto>? body = await response.Content.ReadFromJsonAsync(
            ArcanumJsonContext.Default.ApiResponseBudgetSummaryDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.True(body.Data!.Enabled);

        Assert.Equal(20m, body.Data.DailyLimitUsd);

        Assert.Equal(5m, body.Data.TodaySpendUsd);

        Assert.Equal(15m, body.Data.RemainingUsd);

        Assert.Equal(25, body.Data.SpentPercent);

        Assert.Equal(80, body.Data.AlertThresholdPercent);

    }

    [Fact]
    public async Task GetBudget_ReportsCommittedSpendPlusOutstandingReservations_NotTheSessionProjection()
    {

        // A session opened yesterday that is still being worked in today contributes nothing to the
        // Sessions.TotalCostUsd projection for today (it is keyed on Sessions.CreatedAt), and a call in
        // flight has no completed BillableOperations row at all. The reported figure must be the same
        // authority the gate enforces on: committed + outstanding (DESIGN §22.2).
        await using ArcanumWebApplicationFactory factory = _factory.WithBudget(
            new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 20m },
            todaySpend: 5m,
            outstandingReservations: 2m,
            sessionProjectionSpend: 0m);

        HttpClient client = factory.CreateAuthenticatedClient();

        ApiResponse<BudgetSummaryDto>? body = await (await client.GetAsync("/api/budget")).Content
            .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseBudgetSummaryDto);

        BudgetSummaryDto summary = body!.Data!;

        Assert.Equal(7m, summary.LocalSpendUsd);

        Assert.Equal(7m, summary.TodaySpendUsd);

        Assert.Equal(13m, summary.RemainingUsd);

        Assert.Equal(35, summary.SpentPercent);

    }

}

internal static class BudgetEndpointTestFactoryExtensions
{

    public static ArcanumWebApplicationFactory WithBudget(
        this ArcanumWebApplicationFactory factory,
        BudgetPolicySettings budget,
        decimal todaySpend,
        ExternalSpendSummary? externalSpend = null,
        decimal outstandingReservations = 0m,
        decimal? sessionProjectionSpend = null)
    {

        return new ArcanumWebApplicationFactory
        {

            SettingsOverride = settings => settings with
            {
                Cost = settings.Cost with { Budget = budget },
            },

            ServiceOverrides = services =>
            {

                services.RemoveAll<IGrimoireRepository>();

                services.AddScoped<IGrimoireRepository>(
                    _ => new StubGrimoireRepository(sessionProjectionSpend ?? todaySpend));

                services.RemoveAll<IBudgetReservationService>();

                services.AddScoped<IBudgetReservationService>(
                    _ => new StubBudgetReservationService(todaySpend, outstandingReservations));

                services.RemoveAll<IExternalSpendLedger>();

                services.AddScoped<IExternalSpendLedger>(
                    _ => new StubExternalSpendLedger(externalSpend ?? ExternalSpendSummary.None));

            },

        };

    }

    private sealed class StubExternalSpendLedger(ExternalSpendSummary summary) : IExternalSpendLedger
    {

        public Task<ExternalSpendSummary> GetTodayAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(summary);

    }

    private sealed class StubBudgetReservationService(decimal committedUsd, decimal outstandingUsd)
        : IBudgetReservationService
    {

        public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(committedUsd);

        public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(outstandingUsd);

        // The remaining IBudgetReservationService members are not exercised by /api/budget.

        public Task<Result<BudgetReservation>> ReserveAsync(BudgetReservationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> AdjustAsync(Guid reservationId, decimal reservedUsd, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReconcileAsync(Guid reservationId, decimal actualCostUsd, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> SweepExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class StubGrimoireRepository : IGrimoireRepository
    {

        private readonly decimal _todaySpend;

        public StubGrimoireRepository(decimal todaySpend)
        {
            _todaySpend = todaySpend;
        }

        public Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_todaySpend);

        // The remaining IGrimoireRepository members are not exercised by /api/budget.

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
