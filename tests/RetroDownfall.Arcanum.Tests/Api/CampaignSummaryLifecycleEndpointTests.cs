using System.Net;
using System.Net.Http.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class CampaignSummaryLifecycleEndpointTests
{
    private static CancellationToken Token => CancellationToken.None;

    [SkippableFact]
    public async Task A_disabled_Campaign_summary_reset_is_exact_and_keeps_native_sequence_and_revision_tombstones()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Seed selected = await PublishAsync(factory);

        Seed outside = await PublishAsync(factory);

        using HttpClient client = factory.CreateAuthenticatedClient();

        MemoryResetRequest request = new(MemoryResetScope.CampaignSummary, CampaignId: selected.CampaignId);

        using HttpResponseMessage planned = await client.PostAsync("/api/data/memory/reset/plan",
            JsonContent.Create(request, ArcanumJsonContext.Default.MemoryResetRequest), Token);

        Assert.Equal(HttpStatusCode.OK, planned.StatusCode);

        DataRetentionPlan plan = await MemoryErasureRouteDriver.ReadDataAsync(planned, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

        Assert.NotEmpty(plan.CandidateIds);

        using HttpResponseMessage reset = await client.PostAsync("/api/data/memory/reset",
            JsonContent.Create(request with { ExpectedPlanId = plan.PlanId }, ArcanumJsonContext.Default.MemoryResetRequest), Token);

        Assert.True(reset.StatusCode == HttpStatusCode.OK, await reset.Content.ReadAsStringAsync(Token));

        Assert.Equal(0, await CountAsync(factory, "campaign_rollup_artifacts", selected.CampaignId));

        Assert.Equal(0, await CountAsync(factory, "campaign_contribution_artifacts", selected.CampaignId));

        Assert.Equal(0, await CountAsync(factory, "campaign_rollup_sources", selected.CampaignId));

        Assert.Equal(1, await CountAsync(factory, "campaign_rollup_artifacts", outside.CampaignId));

        Assert.Equal(1, await CountAsync(factory, "campaign_contribution_artifacts", outside.CampaignId));

        Assert.Equal(1, await CountAsync(factory, "campaign_contribution_state", selected.CampaignId,
            "CurrentArtifactId IS NULL AND Revision = 1 AND SummarizedThroughSequence = 1 AND RefoldRequired = 1 AND SourceGeneration > 0"));

        Assert.Equal(1, await CountAsync(factory, "campaign_rollup_state", selected.CampaignId,
            "CurrentArtifactId IS NULL AND Revision = 1 AND RefoldRequired = 1 AND SourceGeneration > 0"));

        Assert.Equal(1, await CountAsync(factory, "Sessions", selected.CampaignId));
    }

    [SkippableTheory]
    [InlineData("campaign")]
    [InlineData("session")]
    [InlineData("entry")]
    public async Task Owner_or_native_history_deletion_clears_only_the_exact_Campaign_continuity_closure(string target)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { MemoryManagement = true },
            },
        };

        Seed selected = await PublishAsync(factory);

        Seed outside = await PublishAsync(factory);

        using HttpClient client = factory.CreateAuthenticatedClient();

        if (target == "session")
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

            DataRetentionPlan preview = await scope.ServiceProvider.GetRequiredService<IDataRetentionService>()
                .PlanAsync(new DataRetentionRequest(DataRetentionOperation.DeleteSession, selected.SessionId), Token);

            Assert.Contains(preview.Items, static item => item.DataClass == RetentionDataClass.CampaignSummaries
                && item.DerivedRecords > 0);
        }

        string route = target switch
        {
            "campaign" => $"/api/campaigns/{selected.CampaignId:D}",
            "session" => $"/api/data/sessions/{selected.SessionId:D}",
            _ => $"/api/sessions/{selected.SessionId:D}/entries/{selected.EntryId:D}",
        };

        using HttpResponseMessage deleted = await client.DeleteAsync(route, Token);

        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync(Token));

        Assert.Equal(0, await CountAsync(factory, "campaign_rollup_artifacts", selected.CampaignId));

        Assert.Equal(0, await CountAsync(factory, "campaign_contribution_artifacts", selected.CampaignId));

        Assert.Equal(0, await CountAsync(factory, "campaign_rollup_sources", selected.CampaignId));

        Assert.Equal(1, await CountAsync(factory, "campaign_rollup_artifacts", outside.CampaignId));

        Assert.Equal(1, await CountAsync(factory, "campaign_contribution_artifacts", outside.CampaignId));

        if (target != "campaign")
        {
            Assert.Equal(1, await CountAsync(factory, "campaign_rollup_state", selected.CampaignId,
                "CurrentArtifactId IS NULL AND Revision = 1 AND RefoldRequired = 1 AND SourceGeneration > 0"));
        }
    }

    [SkippableFact]
    public async Task Archiving_a_Session_keeps_its_native_contribution_and_published_Campaign_summary()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Seed selected = await PublishAsync(factory);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage archived = await client.DeleteAsync($"/api/sessions/{selected.SessionId:D}", Token);

        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);

        Assert.Equal(1, await CountAsync(factory, "campaign_rollup_artifacts", selected.CampaignId));

        Assert.Equal(1, await CountAsync(factory, "campaign_contribution_artifacts", selected.CampaignId));

        Assert.Equal(1, await CountAsync(factory, "campaign_rollup_state", selected.CampaignId,
            "CurrentArtifactId IS NOT NULL AND RefoldRequired = 0"));
    }

    private static async Task<Seed> PublishAsync(ArcanumWebApplicationFactory factory)
    {
        Guid campaignId = Guid.NewGuid();

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        string name = campaignId.ToString("N");

        db.Campaigns.Add(new Campaign
        {
            Id = campaignId, Name = name, NameLower = name, Path = Path.Combine(factory.TempHome, name),
            Settings = "{}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        db.Sessions.Add(new Session
        {
            Id = sessionId, CampaignId = campaignId, Status = "active", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        db.Entries.Add(new Entry
        {
            Id = entryId, SessionId = sessionId, Role = MessageRole.User,
            Content = "Choose SQLite", ModelUsed = "", Sequence = 1, CreatedAt = DateTimeOffset.UtcNow,
        });

        _ = await db.SaveChangesAsync(Token);

        await db.Database.OpenConnectionAsync(Token);

        using IDisposable bindingWrite = CovenantSqliteConnectionInitializer.Instance.Authorize(
            (SqliteConnection)db.Database.GetDbConnection(), CovenantSqliteAuthorizationKind.SessionBindingWrite);

        _ = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc) VALUES ({sessionId.ToString("D").ToUpperInvariant()}, 2, {campaignId.ToString("D").ToUpperInvariant()}, {DateTimeOffset.UtcNow.ToString("O")});", Token);

        CampaignRollupStore store = new(scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>(), CovenantSqliteConnectionInitializer.Instance);

        Result<CampaignContributionInput?> input = await store.PrepareContributionAsync(sessionId, null, null, Token);

        Assert.True(input.IsSuccess, input.Error.Message);

        Assert.NotNull(input.Value);

        Result<CampaignRollupArtifact> contribution = await store.PublishContributionAsync(input.Value, "SQLite was selected", null, null, Token);

        Assert.True(contribution.IsSuccess, contribution.Error.Message);

        Result<CampaignRollupInput?> fold = await store.PrepareRollupAsync(campaignId, null, Token);

        Assert.True(fold.IsSuccess, fold.Error.Message);

        Assert.NotNull(fold.Value);

        Result<CampaignRollupArtifact> published = await store.PublishRollupAsync(fold.Value, "Campaign decision", null, null, Token);

        Assert.True(published.IsSuccess, published.Error.Message);

        return new Seed(campaignId, sessionId, entryId);
    }

    private static async Task<long> CountAsync(ArcanumWebApplicationFactory factory, string table, Guid campaignId, string? extra = null)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await db.Database.OpenConnectionAsync(Token);

        await using SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE CampaignId = $campaignId" + (extra is null ? ";" : $" AND ({extra});");

        _ = command.Parameters.AddWithValue("$campaignId", campaignId.ToString("D").ToUpperInvariant());

        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    private sealed record Seed(Guid CampaignId, Guid SessionId, Guid EntryId);
}
