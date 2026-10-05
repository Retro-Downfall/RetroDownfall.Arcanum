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

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

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
    public async Task ImportCampaign_replace_with_one_prompt_lacking_template_rejects_without_deleting_prompts()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "replace-shape");

        string payload = ReplaceBundle(
            campaign,
            """{"name":"complete","version":"1.0.0","template":"Hello","tags":[]}""",
            """{"name":"incomplete","version":"1.0.0","tags":[]}""");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        await AssertImportRefusedAsync(response);

        // The first prompt of the bundle is valid, so the existing prompt must also survive the half
        // of the bundle that would have been written before the invalid entry was reached.
        Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id));
    }

    [SkippableFact]
    public async Task ImportCampaign_replace_with_a_duplicate_name_and_version_in_the_bundle_rejects_without_deleting_prompts()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "replace-duplicate");

        PromptSummaryDto existing = Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id));

        string payload = ReplaceBundle(
            campaign,
            """{"name":"twin","version":"1.0.0","template":"One","tags":[]}""",
            """{"name":" twin ","version":"1.0.0","template":"Two","tags":[]}""");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        await AssertImportRefusedAsync(response);

        Assert.Equal(existing.Id, Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id)).Id);
    }

    [SkippableTheory]
    [InlineData("replce")]
    [InlineData("overwrite")]
    [InlineData("merge-all")]
    public async Task ImportCampaign_unknown_strategy_is_400(string strategy)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "unknown-strategy");

        string payload = ReplaceBundle(
            campaign,
            """{"name":"fresh","version":"1.0.0","template":"Hello","tags":[]}""")
            .Replace("\"replace\"", $"\"{strategy}\"", StringComparison.Ordinal);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        await AssertImportRefusedAsync(response);

        // An unrecognised strategy used to behave as a silent merge and import the bundle's prompts.
        Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id));
    }

    [SkippableFact]
    public async Task ImportCampaign_replace_strategy_is_case_insensitive_and_swaps_the_prompt_set()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "replace-swap");

        PromptSummaryDto existing = Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id));

        // One incoming prompt reuses the existing prompt's name and version: the unique index only admits it
        // because the delete and the adds share one transaction.
        string payload = ReplaceBundle(
            campaign,
            $$"""{"name":"{{existing.Name}}","version":"{{existing.Version}}","template":"Replaced","tags":[]}""",
            """{"name":"added","version":"2.0.0","template":"Added","tags":[]}""")
            .Replace("\"replace\"", "\"REPLACE\"", StringComparison.Ordinal);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<CampaignImportResultDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto);

        Assert.Equal(2, body!.Data!.PromptsImported);

        Assert.Empty(body.Data.Warnings);

        IReadOnlyList<PromptSummaryDto> after = await ListCampaignPromptsAsync(client, campaign.Id);

        Assert.Equal(2, after.Count);

        Assert.DoesNotContain(after, p => p.Id == existing.Id);

        Assert.Contains(after, p => p.Name == existing.Name && p.Version == existing.Version);

        Assert.Contains(after, p => p.Name == "added");
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

    /// <summary>
    /// A campaign root is usually a repository the operator cloned, so what it holds at
    /// <c>.arcanum/campaign.json</c> is not trusted to be a small regular file.
    /// </summary>
    /// <remarks>
    /// Each shape is a valid bundle that the unbounded, link-following read used to import, so a refusal
    /// here is the reader's and not the parser's: a link to a file outside the campaign, a link in the
    /// place of the <c>.arcanum</c> directory, and a regular file past the size cap.
    /// </remarks>
    [SkippableTheory]
    [InlineData("symlinked-file")]
    [InlineData("symlinked-directory")]
    [InlineData("oversized-file")]
    public async Task Import_from_disk_refuses_a_symlinked_or_oversized_campaign_json(string shape)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(
            OperatingSystem.IsWindows() && shape.StartsWith("symlinked", StringComparison.Ordinal),
            "Creating a symbolic link needs a privilege the Windows lane does not hold.");

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, shape);

        string bundle = "{\"campaign\":" + CampaignJson(campaign) + ",\"spells\":[],\"prompts\":[]}";

        string arcanumDirectory = Path.Combine(campaign.Path, ".arcanum");

        string outside = Path.Combine(factory.TempHome, $"outside-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outside);

        switch (shape)
        {
            case "symlinked-file":

                Directory.CreateDirectory(arcanumDirectory);

                await File.WriteAllTextAsync(Path.Combine(outside, "campaign.json"), bundle);

                File.CreateSymbolicLink(
                    Path.Combine(arcanumDirectory, "campaign.json"),
                    Path.Combine(outside, "campaign.json"));

                break;

            case "symlinked-directory":

                await File.WriteAllTextAsync(Path.Combine(outside, "campaign.json"), bundle);

                // Registration may already have created the directory; the link takes its place.
                if (Directory.Exists(arcanumDirectory))
                {
                    Directory.Delete(arcanumDirectory, recursive: true);
                }

                Directory.CreateSymbolicLink(arcanumDirectory, outside);

                break;

            default:

                Directory.CreateDirectory(arcanumDirectory);

                // A valid bundle padded with trailing whitespace to one byte past the cap, so the only
                // thing wrong with it is its size.
                await using (FileStream stream = File.Create(Path.Combine(arcanumDirectory, "campaign.json")))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(bundle));

                    stream.SetLength(16L * 1024L * 1024L + 1L);

                    stream.Position = bundle.Length;

                    byte[] spaces = Enumerable.Repeat((byte)' ', 64 * 1024).ToArray();

                    while (stream.Position < stream.Length)
                    {
                        int count = (int)Math.Min(spaces.Length, stream.Length - stream.Position);

                        await stream.WriteAsync(spaces.AsMemory(0, count));
                    }
                }

                break;
        }

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<CampaignImportResultDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Campaign.ImportFailed, body.Error!.Value.Code);
    }

    [SkippableFact]
    public async Task Import_from_disk_still_reads_a_regular_campaign_json_inside_the_campaign()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "disk-bundle");

        string arcanumDirectory = Path.Combine(campaign.Path, ".arcanum");

        Directory.CreateDirectory(arcanumDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(arcanumDirectory, "campaign.json"),
            "{\"campaign\":" + CampaignJson(campaign) + ",\"spells\":[],\"prompts\":[]}");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    /// <summary>
    /// <c>GET /api/campaigns</c> takes <c>limit</c> and <c>offset</c> as well as <c>type</c>, and the API
    /// reference now says so; this pins the behaviour it documents.
    /// </summary>
    [SkippableFact]
    public async Task GetCampaigns_pages_with_limit_and_offset_and_clamps_both()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        for (int i = 0; i < 3; i++)
        {
            _ = await CreateCampaignWithOnePromptAsync(factory, client, $"paged-{i}");
        }

        ListPageResult<CampaignDto> first = await ListCampaignsAsync(client, "limit=1&offset=0");

        CampaignDto only = Assert.Single(first.Items);

        Assert.True(first.HasMore);

        Assert.Equal(1, first.NextOffset);

        ListPageResult<CampaignDto> second = await ListCampaignsAsync(client, "limit=1&offset=1");

        Assert.NotEqual(only.Id, Assert.Single(second.Items).Id);

        // A negative offset is treated as 0, and a limit below 1 is clamped up to 1.
        Assert.Equal(only.Id, Assert.Single((await ListCampaignsAsync(client, "limit=1&offset=-5")).Items).Id);

        Assert.Single((await ListCampaignsAsync(client, "limit=0")).Items);

        Assert.Empty((await ListCampaignsAsync(client, "limit=10000&offset=100000")).Items);
    }

    private static async Task<ListPageResult<CampaignDto>> ListCampaignsAsync(HttpClient client, string query)
    {
        HttpResponseMessage response = await client.GetAsync($"/api/campaigns?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<ListPageResult<CampaignDto>>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto);

        return body!.Data!;
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

        public Task<Result<Prompt>> AddAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<Prompt>> UpdateAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<int>> ReplaceCampaignPromptsAsync(
            Guid campaignId,
            IReadOnlyList<Prompt> prompts,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static string ReplaceBundle(CampaignDto campaign, params string[] promptJson) =>
        "{\"strategy\":\"replace\",\"payload\":{\"campaign\":"
        + CampaignJson(campaign)
        + ",\"spells\":[],\"prompts\":["
        + string.Join(",", promptJson)
        + "]}}";

    private static async Task AssertImportRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<CampaignImportResultDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Campaign.ImportFailed, body.Error!.Value.Code);
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
