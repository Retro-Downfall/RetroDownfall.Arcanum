using System.Globalization;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Guild;
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
    public void Guild_runtime_registration_refuses_an_interface_only_budget_participant()
    {
        ServiceCollection services = new();

        services.AddSingleton<IBudgetReservationService>(new BudgetEndpointTestFactoryExtensions.StubBudgetReservationService(0m, 0m));

        services.AddGuildLocalExecution();

        using ServiceProvider provider = services.BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IGuildQuestAllocationParticipant>());

        Assert.Equal("Guild execution requires the physical daily budget participant.", error.Message);
    }

    [Theory]
    [InlineData(true, 0, HealthStatus.Degraded)]
    [InlineData(false, 0, HealthStatus.Healthy)]
    [InlineData(true, 10, HealthStatus.Healthy)]
    public async Task Health_reports_budget_enforcement_with_a_remedy_without_refusing_the_host(bool enabled, int limit, HealthStatus expected)
    {
        await using ArcanumWebApplicationFactory factory = await _factory.WithBudgetAsync(
            new BudgetPolicySettings { Enabled = enabled, DailyLimitUsd = limit }, todaySpend: 0m);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<HealthReportDto>? envelope = await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseHealthReportDto);

        HealthComponentDto row = Assert.Single(envelope!.Data!.Components, component => component.Name == "Budget");

        Assert.Equal(expected, row.Status);

        if (expected == HealthStatus.Degraded)
        {
            Assert.Contains("not enforced", row.Detail, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("Arcanum:Cost:Budget:DailyLimitUsd", row.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetBudget_Disabled_ReportsRealSpend()
    {
        await using ArcanumWebApplicationFactory factory = await _factory.WithBudgetAsync(
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

        Assert.Equal(999m, body.Data.TodaySpendUsd);
    }

    [Theory]
    [InlineData(false, 10, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 20, true)]
    public async Task Budget_enforcement_is_explicit_and_never_hides_recorded_spend(bool enabled, int limit, bool enforced)
    {
        await using ArcanumWebApplicationFactory factory = await _factory.WithBudgetAsync(
            new BudgetPolicySettings { Enabled = enabled, DailyLimitUsd = limit },
            todaySpend: 4.5m,
            outstandingReservations: 2m,
            sessionProjectionSpend: 999m,
            externalSpend: new ExternalSpendSummary(3.25m, 900, 2, 1));

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/budget");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement data = document.RootElement.GetProperty("data");

        Assert.Equal(6.5m, data.GetProperty("localSpendUsd").GetDecimal());

        Assert.Equal(3.25m, data.GetProperty("externalSpendUsd").GetDecimal());

        Assert.Equal(9.75m, data.GetProperty("todaySpendUsd").GetDecimal());

        Assert.Equal(1, data.GetProperty("unpricedDelegatedSendings").GetInt32());

        Assert.True(data.TryGetProperty("enforced", out JsonElement wireEnforced), "Budget status must distinguish enabled configuration from active enforcement.");

        Assert.Equal(enforced, wireEnforced.GetBoolean());

        Assert.Equal(enforced ? 10.25m : 0m, data.GetProperty("remainingUsd").GetDecimal());

        Assert.Equal(enforced ? 49 : 0, data.GetProperty("spentPercent").GetInt32());
    }

    [Fact]
    public async Task GetBudget_DistinguishesLocalFromDelegatedSpendAndFlagsUnpricedSendings()
    {
        await using ArcanumWebApplicationFactory factory = await _factory.WithBudgetAsync(
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
        await using ArcanumWebApplicationFactory factory = await _factory.WithBudgetAsync(
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
        await using ArcanumWebApplicationFactory factory = await _factory.WithBudgetAsync(
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
    public static async Task<ArcanumWebApplicationFactory> WithBudgetAsync(
        this ArcanumWebApplicationFactory factory,
        BudgetPolicySettings budget,
        decimal todaySpend,
        ExternalSpendSummary? externalSpend = null,
        decimal outstandingReservations = 0m,
        decimal? sessionProjectionSpend = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        ArcanumWebApplicationFactory configured = new()
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

                services.AddScoped<IBudgetReservationService>(sp => new BudgetReservationService(
                    sp.GetRequiredService<ArcanumDbContext>(), sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>())
                {
                    UtcNowForTesting = () => now,
                });

                services.RemoveAll<IExternalSpendLedger>();

                services.AddScoped<IExternalSpendLedger>(
                    _ => new StubExternalSpendLedger(externalSpend ?? ExternalSpendSummary.None));
            },
        };

        try
        {
            await using AsyncServiceScope scope = configured.Services.CreateAsyncScope();

            Assert.IsType<BudgetReservationService>(scope.ServiceProvider.GetRequiredService<IBudgetReservationService>());

            GuildBudgetLedger ledger = scope.ServiceProvider.GetRequiredService<GuildBudgetLedger>();

            Assert.Same(ledger, scope.ServiceProvider.GetRequiredService<IGuildQuestAllocationParticipant>());

            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            if (todaySpend > 0m || outstandingReservations > 0m)
            {
                await SeedAccountingAsync(db, now, todaySpend, outstandingReservations);
            }

            return configured;
        }
        catch
        {
            await configured.DisposeAsync();

            throw;
        }
    }

    private static async Task SeedAccountingAsync(ArcanumDbContext db, DateTimeOffset now, decimal committed, decimal outstanding)
    {
        Guid run = await new TurnRunWriter(db).StartRunAsync(new InferenceRunStart(
            Guid.NewGuid().ToString("N"), null, "test", "budget-endpoint", null, now.AddMinutes(-1)));

        await db.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        if (committed > 0m)
        {
            command.CommandText = """
                INSERT INTO BillableOperations
                    (Id,RunId,OperationType,Provider,Model,Purpose,StartedAt,CompletedAt,InputTokens,OutputTokens,
                     ReasoningTokens,CachedTokens,PricingSnapshotJson,ActualCostUsd,Status,ProviderRequestId)
                VALUES ($id,$run,$type,'test','model','budget-endpoint',$started,$now,1,1,0,0,'{}',$usd,$status,NULL)
                """;

            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));

            command.Parameters.AddWithValue("$run", run.ToString("N"));

            command.Parameters.AddWithValue("$type", (int)BillableOperationType.Chat);

            command.Parameters.AddWithValue("$started", UtcInstantText.Format(now.AddSeconds(-1)));

            command.Parameters.AddWithValue("$now", UtcInstantText.Format(now));

            ExactUsdText.AddParameter(command, "$usd", committed);

            command.Parameters.AddWithValue("$status", (int)BillableOperationStatus.Completed);

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        if (outstanding > 0m)
        {
            command.Parameters.Clear();

            command.CommandText = """
                INSERT INTO BudgetReservations
                    (Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd)
                VALUES ($id,$run,$period,$status,$expires,$now,$now,$usd,'0')
                """;

            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));

            command.Parameters.AddWithValue("$run", run.ToString("N"));

            command.Parameters.AddWithValue("$period", now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            command.Parameters.AddWithValue("$status", (int)BudgetReservationStatus.Reserved);

            command.Parameters.AddWithValue("$expires", UtcInstantText.Format(now.AddHours(1)));

            command.Parameters.AddWithValue("$now", UtcInstantText.Format(now));

            ExactUsdText.AddParameter(command, "$usd", outstanding);

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await transaction.CommitAsync();
    }

    private sealed class StubExternalSpendLedger(ExternalSpendSummary summary) : IExternalSpendLedger
    {
        public Task<ExternalSpendSummary> GetTodayAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(summary);
    }

    // Used only by the registration refusal control, never by a running API fixture.
    internal sealed class StubBudgetReservationService(decimal committedUsd, decimal outstandingUsd)
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

        public Task ExtendExpiryAsync(Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> RecheckDailyLimitAsync(Guid reservationId, decimal delegatedSpendUsd, CancellationToken cancellationToken = default) =>
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
