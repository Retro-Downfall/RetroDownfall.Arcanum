using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

[Collection("ApiHost")]
public sealed class CampaignScopedListingTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public CampaignScopedListingTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    private async Task<CampaignDto> CreateCampaignAsync(HttpClient client, string suffix)
    {

        string path = Path.Combine(_factory.TempHome, $"campaign-scoped-{suffix}");

        Directory.CreateDirectory(path);

        RegisterCampaignRequest request = new($"Campaign {suffix}", path, WorkspaceType.Campaign, null);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.RegisterCampaignRequest);

        HttpResponseMessage response = await client.PostAsync("/api/campaigns", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<CampaignDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseCampaignDto);

        return body!.Data!;

    }

    [SkippableFact]
    public async Task CampaignSpells_includes_builtins_with_shadow_order()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignAsync(client, "spells");

        string spellDir = Path.Combine(campaign.Path, "spells", "campaign-only");

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(Path.Combine(spellDir, "SPELL.md"), "---\nname: campaign-only\ndescription: Campaign spell\n---\n\nBody.");

        HttpResponseMessage response = await client.GetAsync($"/api/campaigns/{campaign.Id}/spells");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SpellSummary[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSpellSummaryArray);

        Assert.NotNull(body?.Data);

        Assert.Contains(body!.Data!, s => s.Name == "campaign-only");

    }

    [SkippableFact]
    public async Task CampaignSpells_404_when_campaign_not_found()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/campaigns/{Guid.NewGuid()}/spells");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task CampaignPrompts_filtered_by_campaign()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignAsync(client, "prompts");

        CreatePromptRequest request = new(
            $"campaign-prompt-{Guid.NewGuid():N}",
            "1.0.0",
            "Hello",
            null,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            campaign.Id);

        string createPayload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreatePromptRequest);

        HttpResponseMessage created = await client.PostAsync("/api/prompts", new StringContent(createPayload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        HttpResponseMessage response = await client.GetAsync($"/api/campaigns/{campaign.Id}/prompts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ListPageResult<PromptSummaryDto>>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto);

        Assert.NotNull(body?.Data);

        Assert.All(body!.Data!.Items, p => Assert.Equal(campaign.Id, p.CampaignId));

        Assert.Single(body.Data.Items);

    }

    [SkippableFact]
    public async Task CampaignSessions_filtered_by_campaign()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignAsync(client, "sessions");

        CreateSessionRequest sessionRequest = new(campaign.Id, "Scoped session");

        string sessionPayload = JsonSerializer.Serialize(sessionRequest, ArcanumJsonContext.Default.CreateSessionRequest);

        HttpResponseMessage createdSession = await client.PostAsync("/api/sessions", new StringContent(sessionPayload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, createdSession.StatusCode);

        HttpResponseMessage response = await client.GetAsync($"/api/campaigns/{campaign.Id}/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SessionQueryResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSessionQueryResult);

        Assert.NotNull(body?.Data);

        Assert.Contains(body!.Data!.Summaries, s => s.CampaignId == campaign.Id);

    }

}
