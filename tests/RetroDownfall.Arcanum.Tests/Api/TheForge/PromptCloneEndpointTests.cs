using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

[Collection("ApiHost")]
public sealed class PromptCloneEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public PromptCloneEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    private async Task<Guid> CreatePromptAsync(HttpClient client, string name, string version, Guid? campaignId = null)
    {

        CreatePromptRequest request = new(
            name,
            version,
            "Hello {{name}}",
            "Test prompt",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            campaignId);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreatePromptRequest);

        HttpResponseMessage response = await client.PostAsync("/api/prompts", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<PromptDetailDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponsePromptDetailDto);

        return body!.Data!.Id;

    }

    private async Task<HttpResponseMessage> CloneAsync(HttpClient client, Guid id, ClonePromptRequest request)
    {

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.ClonePromptRequest);

        return await client.PostAsync($"/api/prompts/{id}/clone", new StringContent(payload, Encoding.UTF8, "application/json"));

    }

    private async Task<Guid> CreateCampaignAsync(HttpClient client, string suffix)
    {

        string path = Path.Combine(_factory.TempHome, $"prompt-clone-campaign-{suffix}");

        Directory.CreateDirectory(path);

        RegisterCampaignRequest request = new($"Prompt Clone Campaign {suffix}", path, WorkspaceType.Campaign, null);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.RegisterCampaignRequest);

        HttpResponseMessage response = await client.PostAsync("/api/campaigns", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<CampaignDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseCampaignDto);

        return body!.Data!.Id;

    }

    [SkippableFact]
    public async Task Clone_creates_new_prompt()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        Guid sourceId = await CreatePromptAsync(client, $"clone-source-{Guid.NewGuid():N}", "1.0.0");

        HttpResponseMessage response = await CloneAsync(client, sourceId, new ClonePromptRequest("clone-target", "1.0.0"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<PromptDetailDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponsePromptDetailDto);

        Assert.NotNull(body?.Data);

        Assert.Equal("clone-target", body.Data!.Name);

        Assert.Equal("1.0.0", body.Data.Version);

        Assert.NotEqual(sourceId, body.Data.Id);

    }

    [SkippableFact]
    public async Task Clone_400_on_duplicate_version()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string sharedName = $"dup-target-{Guid.NewGuid():N}";

        Guid sourceId = await CreatePromptAsync(client, $"dup-source-{Guid.NewGuid():N}", "1.0.0");

        _ = await CreatePromptAsync(client, sharedName, "1.0.0");

        HttpResponseMessage response = await CloneAsync(client, sourceId, new ClonePromptRequest(sharedName, "1.0.0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task Clone_404_when_source_not_found()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await CloneAsync(client, Guid.NewGuid(), new ClonePromptRequest("whatever", "1.0.0"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task Clone_respects_campaign_scope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string sharedName = $"scoped-{Guid.NewGuid():N}";

        Guid sourceId = await CreatePromptAsync(client, sharedName, "1.0.0");

        // Cloning to the same name+version but without a campaign scope collides with the global-scope source.
        HttpResponseMessage collision = await CloneAsync(client, sourceId, new ClonePromptRequest(sharedName, "1.0.0"));

        Assert.Equal(HttpStatusCode.BadRequest, collision.StatusCode);

        // Cloning the same name+version into a different (nonexistent-but-unscoped) campaign scope does not collide.
        HttpResponseMessage differentVersion = await CloneAsync(client, sourceId, new ClonePromptRequest(sharedName, "2.0.0"));

        Assert.Equal(HttpStatusCode.Created, differentVersion.StatusCode);

    }

    [SkippableFact]
    public async Task Clone_overrides_campaign_when_provided()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        Guid sourceId = await CreatePromptAsync(client, $"override-source-{Guid.NewGuid():N}", "1.0.0");

        Guid overrideCampaignId = await CreateCampaignAsync(client, Guid.NewGuid().ToString("N"));

        HttpResponseMessage response = await CloneAsync(client, sourceId, new ClonePromptRequest("override-target", "1.0.0", overrideCampaignId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<PromptDetailDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponsePromptDetailDto);

        Assert.Equal(overrideCampaignId, body?.Data?.CampaignId);

    }

}
