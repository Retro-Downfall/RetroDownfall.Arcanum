using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

[Collection("ApiHost")]
public sealed class CampaignEndpointTests
{

    [SkippableFact]
    public async Task RegisterCampaign_repository_failure_maps_error_code()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        factory.ServiceOverrides = services =>
        {
            services.RemoveAll<ICampaignRepository>();

            services.AddScoped<ICampaignRepository, MaxReachedCampaignRepository>();
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        string path = Path.Combine(factory.TempHome, "campaign-max-result");

        Directory.CreateDirectory(path);

        RegisterCampaignRequest request = new(
            "Rejected Campaign",
            path,
            WorkspaceType.Campaign,
            null);

        string payload = JsonSerializer.Serialize(
            request,
            ArcanumJsonContext.Default.RegisterCampaignRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/campaigns",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<CampaignDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseCampaignDto);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Campaign.MaxReached, body.Error!.Value.Code);

        Assert.Equal(
            "Repository failure selected by code, not legacy exception text.",
            body.Error.Value.Message);

    }

    [SkippableFact]
    public async Task ImportCampaign_replace_strategy_with_memberless_payload_rejects_without_deleting_prompts()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "replace-guard");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent("""{"strategy":"replace","payload":{}}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<CampaignImportResultDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Campaign.ImportFailed, body.Error!.Value.Code);

        // The rejected bundle must not have destroyed the Campaign's existing prompts: payload
        // validation has to precede the "replace" delete sweep, not follow it.
        Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id));

    }

    [SkippableFact]
    public async Task ImportCampaign_payload_without_spells_or_prompts_imports_nothing_and_succeeds()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "sparse-bundle");

        string payload = "{\"strategy\":\"merge\",\"payload\":{\"campaign\":" + CampaignJson(campaign) + "}}";

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<CampaignImportResultDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.Equal(0, body.Data!.SpellsImported);

        Assert.Equal(0, body.Data.PromptsImported);

    }

    [SkippableFact]
    public async Task ImportCampaign_spell_with_unparsable_embedded_json_reports_import_failure()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "bad-spell-json");

        string payload = "{\"strategy\":\"merge\",\"payload\":{\"campaign\":"
            + CampaignJson(campaign)
            + ",\"spells\":[{\"name\":\"broken\",\"spellJson\":\"not-json\",\"fullContent\":\"x\",\"scripts\":[]}],\"prompts\":[]}}";

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<CampaignImportResultDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Campaign.ImportFailed, body.Error!.Value.Code);

        // The body itself parsed fine; only the spell's embedded metadata string did not, and the
        // message has to name the spell rather than blame the whole request body.
        Assert.Contains("broken", body.Error.Value.Message, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task GetCampaignPrompts_truncated_page_reports_hasMore_and_nextOffset()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid campaignId = Guid.NewGuid();

        // The 10,000-row ceiling is real, so a Campaign with more prompts than that gets a truncated
        // page. A hard-coded hasMore:false tells the caller it has everything.
        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {

                services.RemoveAll<ICampaignRepository>();

                services.AddScoped<ICampaignRepository>(_ => new SingleCampaignRepository(campaignId));

                services.RemoveAll<IPromptRepository>();

                services.AddScoped<IPromptRepository>(_ => new TruncatedPromptRepository(campaignId));

            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/campaigns/{campaignId}/prompts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<ListPageResult<PromptSummaryDto>>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto);

        Assert.NotNull(body?.Data);

        Assert.True(body!.Data!.HasMore);

        Assert.Equal(10_000, body.Data.NextOffset);

    }

    private sealed class SingleCampaignRepository(Guid campaignId) : ICampaignRepository
    {

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(id == campaignId
                ? new Campaign
                {
                    Id = campaignId,
                    Name = "Truncated",
                    Path = Path.GetTempPath(),
                    Type = WorkspaceType.Campaign,
                }
                : null);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

    }

    private sealed class TruncatedPromptRepository(Guid campaignId) : IPromptRepository
    {

        public Task<ListPageResult<Prompt>> ListAsync(
            Guid? scopeCampaignId,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Prompt>(
                [
                    new Prompt
                    {
                        Id = Guid.NewGuid(),
                        CampaignId = campaignId,
                        Name = "truncated",
                        Version = "1.0.0",
                        Template = "body",
                        Tags = "[]",
                    },
                ],
                HasMore: true,
                NextOffset: 10_000));

        public Task<Prompt?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Prompt?> GetByNameAndVersionAsync(
            string name,
            string version,
            Guid? scopeCampaignId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Prompt>> ListVersionsAsync(
            string name,
            Guid? scopeCampaignId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Prompt> AddAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Prompt> UpdateAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private static string CampaignJson(CampaignDto campaign) =>
        JsonSerializer.Serialize(campaign, ArcanumJsonContext.Default.CampaignDto);

    private static async Task<CampaignDto> CreateCampaignWithOnePromptAsync(
        ArcanumWebApplicationFactory factory,
        HttpClient client,
        string suffix)
    {

        string path = Path.Combine(factory.TempHome, $"campaign-import-{suffix}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        RegisterCampaignRequest register = new($"Import {suffix}", path, WorkspaceType.Campaign, null);

        HttpResponseMessage created = await client.PostAsync(
            "/api/campaigns",
            new StringContent(
                JsonSerializer.Serialize(register, ArcanumJsonContext.Default.RegisterCampaignRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        ApiResponse<CampaignDto>? campaignBody = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignDto);

        CampaignDto campaign = campaignBody!.Data!;

        CreatePromptRequest prompt = new(
            $"import-guard-{Guid.NewGuid():N}",
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

        HttpResponseMessage promptCreated = await client.PostAsync(
            "/api/prompts",
            new StringContent(
                JsonSerializer.Serialize(prompt, ArcanumJsonContext.Default.CreatePromptRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.Created, promptCreated.StatusCode);

        return campaign;

    }

    private static async Task<IReadOnlyList<PromptSummaryDto>> ListCampaignPromptsAsync(HttpClient client, Guid campaignId)
    {

        HttpResponseMessage response = await client.GetAsync($"/api/campaigns/{campaignId}/prompts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<ListPageResult<PromptSummaryDto>>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto);

        return body!.Data!.Items;

    }

    private sealed class MaxReachedCampaignRepository : ICampaignRepository
    {

        public Task<Campaign?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByPathAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByNameAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<Campaign>.Failure(
                    new Error(
                        ErrorCodes.Campaign.MaxReached,
                        "Repository failure selected by code, not legacy exception text.")));

        public Task<Campaign> UpdateAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

    }

}
