using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

[Collection("ApiHost")]
public sealed class SanctumBreachesEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SanctumBreachesEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetBreaches_RedactsPathFieldsToFileNameOnly()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid campaignId = await SeedCampaignAsync();

        await RecordBreachAsync(
            campaignId,
            "read_file_chunk",
            "PathEscape",
            details: new SanctumBreachDetails(
                RequestedPath: "/tmp/campaign-root/../../etc/secret.txt",
                ResolvedPath: "/etc/secret.txt",
                WorkspaceRoot: "/tmp/campaign-root",
                RequestedUrl: null,
                ToolArguments: null,
                LimitValue: null,
                ActualValue: null));

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/campaigns/{campaignId}/sanctum/breaches");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SanctumBreachQueryResult result = await ReadResultAsync(response);

        SanctumBreachDto breach = Assert.Single(result.Items);

        Assert.Equal("secret.txt", breach.ResolvedPath);

        Assert.Equal("campaign-root", breach.WorkspaceRoot);

        Assert.Equal("secret.txt", breach.RequestedPath);

    }

    [SkippableFact]
    public async Task GetBreaches_LimitClampedAndHasMoreReflectsAdditionalRows()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid campaignId = await SeedCampaignAsync();

        for (int i = 0; i < 3; i++)
        {

            await RecordBreachAsync(campaignId, $"tool-{i}", "PathEscape");

        }

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/campaigns/{campaignId}/sanctum/breaches?limit=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SanctumBreachQueryResult result = await ReadResultAsync(response);

        Assert.Equal(2, result.Items.Length);

        Assert.True(result.HasMore);

    }

    [SkippableFact]
    public async Task GetBreaches_FiltersByToolName()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid campaignId = await SeedCampaignAsync();

        await RecordBreachAsync(campaignId, "network_fetch", "NetworkEgress");

        await RecordBreachAsync(campaignId, "read_file_chunk", "PathEscape");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/campaigns/{campaignId}/sanctum/breaches?tool=read_file_chunk");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SanctumBreachQueryResult result = await ReadResultAsync(response);

        SanctumBreachDto breach = Assert.Single(result.Items);

        Assert.Equal("read_file_chunk", breach.ToolName);

    }

    [SkippableFact]
    public async Task GetBreaches_UnknownCampaign_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/campaigns/{Guid.NewGuid()}/sanctum/breaches");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    private static async Task<SanctumBreachQueryResult> ReadResultAsync(HttpResponseMessage response)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SanctumBreachQueryResult>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseSanctumBreachQueryResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        return body.Data!;

    }

    private async Task<Guid> SeedCampaignAsync()
    {

        using IServiceScope scope = _factory.Services.CreateScope();

        ICampaignRepository repository = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        string workspaceRoot = Path.Combine(_factory.TempHome, "sanctum-breach-campaigns", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workspaceRoot);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Campaign campaign = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Campaign-{Guid.NewGuid():N}",
            Path = workspaceRoot,
            Type = WorkspaceType.Campaign,
            Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
            CreatedAt = now,
            UpdatedAt = now,
        };

        Campaign saved = (await repository
            .AddAsync(campaign, CancellationToken.None)).Value;

        return saved.Id;

    }

    private async Task RecordBreachAsync(
        Guid campaignId,
        string toolName,
        string breachType,
        SanctumBreachDetails? details = null)
    {

        using IServiceScope scope = _factory.Services.CreateScope();

        ISanctumBreachRepository repository = scope.ServiceProvider.GetRequiredService<ISanctumBreachRepository>();

        SanctumBreachRecord record = new(
            Id: "ignored",
            CampaignId: campaignId.ToString(),
            OccurredAt: DateTimeOffset.UtcNow,
            ToolName: toolName,
            BreachType: breachType,
            Description: $"{breachType} via {toolName}",
            Details: details);

        await repository.RecordAsync(record, maxBreachCount: 1000, CancellationToken.None);

    }

}
