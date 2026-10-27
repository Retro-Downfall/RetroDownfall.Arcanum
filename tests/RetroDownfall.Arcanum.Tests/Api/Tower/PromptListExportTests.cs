using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class PromptListExportTests
{
    [SkippableTheory]
    [InlineData(false, "q")]
    [InlineData(false, "tag")]
    [InlineData(true, "q")]
    [InlineData(true, "tag")]
    public async Task Filtered_lists_page_matches_beyond_the_old_ten_thousand_row_prefix(bool campaignRoute, string filter)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await RegisterAsync(factory, client);

        await SeedAsync(factory, campaignRoute ? campaign.Id : null);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            IPromptRepository repository = scope.ServiceProvider.GetRequiredService<IPromptRepository>();

            Result<Prompt> decoy = await repository.AddAsync(new Prompt
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignRoute ? null : campaign.Id,
                Name = "aaa-wrong-scope",
                Version = "1",
                Description = "É%_",
                Tags = "[\"É%_\"]",
                Template = "wrong scope",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            Assert.True(decoy.IsSuccess);
        }

        string route = campaignRoute ? $"/api/campaigns/{campaign.Id}/prompts" : "/api/prompts";

        string term = Uri.EscapeDataString(filter == "q" ? " é%_ " : "é%_");

        using HttpResponseMessage response = await client.GetAsync($"{route}?{filter}={term}&limit=1&offset=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<ListPageResult<PromptSummaryDto>>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto);

        Assert.NotNull(body?.Data);

        ListPageResult<PromptSummaryDto> page = body!.Data!;

        Assert.Equal("zz-tail-01", Assert.Single(page.Items).Name);

        Assert.True(page.HasMore);

        Assert.Equal(2, page.NextOffset);

        using HttpResponseMessage lastResponse = await client.GetAsync($"{route}?{filter}={term}&limit=1&offset={page.NextOffset}");

        Assert.Equal(HttpStatusCode.OK, lastResponse.StatusCode);

        ListPageResult<PromptSummaryDto> last = JsonSerializer.Deserialize(
            await lastResponse.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto)!.Data!;

        Assert.Equal("zz-tail-02", Assert.Single(last.Items).Name);

        Assert.False(last.HasMore);

        Assert.Null(last.NextOffset);
    }

    [SkippableFact]
    public async Task Campaign_export_includes_every_prompt_and_the_final_template_after_the_old_limit()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await RegisterAsync(factory, client);

        await SeedAsync(factory, campaign.Id);

        using HttpResponseMessage response = await client.PostAsync($"/api/campaigns/{campaign.Id}/export",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        CampaignExportDto bundle = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseCampaignExportDto)!.Data!;

        Assert.Equal(10003, bundle.Prompts.Count);

        PromptExportDto tail = Assert.Single(bundle.Prompts, static prompt => prompt.Name == "zz-tail-02");

        Assert.Equal("template-10002", tail.Template);

        Assert.Equal(["É%_"], tail.Tags);
    }

    private static async Task<CampaignDto> RegisterAsync(ArcanumWebApplicationFactory factory, HttpClient client)
    {
        string path = Path.Combine(factory.TempHome, "prompt-library");

        Directory.CreateDirectory(path);

        RegisterCampaignRequest request = new("Prompt library", path, WorkspaceType.Campaign, null);

        using HttpResponseMessage response = await client.PostAsync("/api/campaigns",
            new StringContent(JsonSerializer.Serialize(request, ArcanumJsonContext.Default.RegisterCampaignRequest), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseCampaignDto)!.Data!;
    }

    private static async Task SeedAsync(ArcanumWebApplicationFactory factory, Guid? campaign)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        // One statement owns one native read/write snapshot; no per-prompt API or provider calls.
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(db, """
            WITH RECURSIVE numbers(i) AS (VALUES(0) UNION ALL SELECT i + 1 FROM numbers WHERE i < 10002)
            INSERT INTO Prompts(Id, CampaignId, Name, Version, Description, Tags, Template, CreatedAt, UpdatedAt)
            SELECT printf('%08X-0000-4000-8000-000000000001', i), $campaign,
                CASE WHEN i < 10000 THEN printf('aaa-%05d', i) ELSE printf('zz-tail-%02d', i - 10000) END,
                '1', CASE WHEN i < 10000 THEN NULL ELSE $description END,
                CASE WHEN i < 10000 THEN '[]' ELSE $tags END,
                printf('template-%d', i), $instant, $instant
            FROM numbers;
            """, CancellationToken.None);

        command.Parameters.AddWithValue("$campaign", campaign is { } id ? GrimoireEntitySql.Format(id) : DBNull.Value);

        command.Parameters.AddWithValue("$description", "Literal É%_ suffix");

        command.Parameters.AddWithValue("$tags", "[\"É%_\"]");

        command.Parameters.AddWithValue("$instant", UtcInstantText.Format(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)));

        Assert.Equal(10003, await command.ExecuteNonQueryAsync());
    }
}
