using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [SkippableFact]
    public async Task Apprentice_root_filter_joins_apprentice_identity_to_exact_quest_linkage()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                return settings;
            },
            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid quest = Guid.Empty;

        for (int index = 0; index < 2; index++)
        {
            GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Linked root", "Inspect exact linkage",
                new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

            using HttpResponseMessage issue = await client.PostAsync("/api/guild/quests",
                Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

            Assert.Equal(HttpStatusCode.Created, issue.StatusCode);

            using JsonDocument accepted = JsonDocument.Parse(await issue.Content.ReadAsStringAsync());

            if (index == 0) quest = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        }

        Guid apprentice;

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await using var command = await GrimoireSqlCommandFactory.CreateAsync(db,
                "SELECT ApprenticeId FROM guild_local_executions WHERE QuestId=$quest", CancellationToken.None);

            command.Parameters.AddWithValue("$quest", quest.ToString("D").ToUpperInvariant());

            apprentice = Guid.Parse((string)(await command.ExecuteScalarAsync())!);
        }

        using HttpResponseMessage filtered = await client.GetAsync($"/api/apprentices?rootId={apprentice:D}");

        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);

        using JsonDocument page = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());

        JsonElement row = Assert.Single(page.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());

        Assert.Equal(apprentice, row.GetProperty("id").GetGuid());

        Assert.Equal(quest, row.GetProperty("guildQuestId").GetGuid());

        using HttpResponseMessage detail = await client.GetAsync($"/api/apprentices/{apprentice:D}");

        using JsonDocument detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());

        Assert.Equal(quest, detailJson.RootElement.GetProperty("data").GetProperty("guildQuestId").GetGuid());

        using HttpResponseMessage wrongKind = await client.GetAsync($"/api/apprentices?rootId={quest:D}");

        using JsonDocument wrongJson = JsonDocument.Parse(await wrongKind.Content.ReadAsStringAsync());

        Assert.Empty(wrongJson.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
    }
    [SkippableFact]
    public async Task Apprentice_parent_and_root_filters_preserve_complete_timestamp_ties()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                return settings;
            },
            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest rootRequest = new(Guid.NewGuid(), null, "Root", "Delegate work",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [GuildDelegationKind.Local], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(rootRequest, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument rootJson = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid rootId = rootJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        async Task<Guid> Child(Guid parent)
        {
            using HttpResponseMessage found = await client.GetAsync($"/api/guild/quests/{parent:D}");

            using JsonDocument current = JsonDocument.Parse(await found.Content.ReadAsStringAsync());

            GuildIssueChildRequest request = new(Guid.NewGuid(), current.RootElement.GetProperty("data").GetProperty("revision").GetInt64(),
                null, "Child", "Inspect lineage", null);

            using HttpResponseMessage result = await client.PostAsync($"/api/guild/quests/{parent:D}/children",
                Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueChildRequest)));

            Assert.True(result.StatusCode == HttpStatusCode.Created, await result.Content.ReadAsStringAsync());

            using JsonDocument child = JsonDocument.Parse(await result.Content.ReadAsStringAsync());

            return child.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        }

        Guid firstChild = await Child(rootId);

        Guid secondChild = await Child(rootId);

        _ = await Child(firstChild);

        Guid rootApprentice;

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await using var command = await GrimoireSqlCommandFactory.CreateAsync(db,
                "SELECT ApprenticeId FROM guild_local_executions WHERE QuestId=$quest", default);

            command.Parameters.AddWithValue("$quest", rootId.ToString("D").ToUpperInvariant());

            rootApprentice = Guid.Parse((string)(await command.ExecuteScalarAsync())!);

            command.CommandText = "UPDATE Apprentices SET UpdatedAt='2026-01-01T00:00:00.0000000Z'";

            command.Parameters.Clear();

            _ = await command.ExecuteNonQueryAsync();
        }

        using HttpResponseMessage parentResponse = await client.GetAsync($"/api/apprentices?parentId={rootApprentice:D}&limit=1");

        Assert.True(parentResponse.StatusCode == HttpStatusCode.OK, await parentResponse.Content.ReadAsStringAsync());

        using JsonDocument parentPage = JsonDocument.Parse(await parentResponse.Content.ReadAsStringAsync());

        Guid[] children = parentPage.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("guildQuestId").GetGuid()).ToArray();

        Assert.Equal(2, children.Length);

        Assert.Contains(firstChild, children);

        Assert.Contains(secondChild, children);

        Assert.False(parentPage.RootElement.GetProperty("data").GetProperty("hasMore").GetBoolean());

        using HttpResponseMessage rootResponse = await client.GetAsync($"/api/apprentices?rootId={rootApprentice:D}&limit=1");

        using JsonDocument rootPage = JsonDocument.Parse(await rootResponse.Content.ReadAsStringAsync());

        Assert.Equal(4, rootPage.RootElement.GetProperty("data").GetProperty("items").GetArrayLength());

        Assert.False(rootPage.RootElement.GetProperty("data").GetProperty("hasMore").GetBoolean());
    }

}
