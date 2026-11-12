using System.Net;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [SkippableFact]
    public async Task Operator_tree_includes_accepted_root_and_rejects_unknown_owner()
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

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Tree root", "Inspect tree",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using HttpResponseMessage ordinaryRead = await client.GetAsync($"/api/guild/quests/{questId:D}");

        Assert.Equal(HttpStatusCode.OK, ordinaryRead.StatusCode);

        using JsonDocument ordinary = JsonDocument.Parse(await ordinaryRead.Content.ReadAsStringAsync());

        Assert.Equal(JsonValueKind.Null, ordinary.RootElement.GetProperty("data").GetProperty("receipt").ValueKind);

        using HttpResponseMessage tree = await client.GetAsync($"/api/guild/quests/{questId:D}/tree");

        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);

        using JsonDocument page = JsonDocument.Parse(await tree.Content.ReadAsStringAsync());

        Assert.Equal(questId, Assert.Single(page.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        using HttpResponseMessage missing = await client.GetAsync($"/api/guild/quests/{Guid.NewGuid():D}/tree");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [SkippableFact]
    public async Task Operator_can_read_spend_for_an_accepted_quest()
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

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Spend root", "Inspect spend",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using HttpResponseMessage response = await client.GetAsync($"/api/guild/quests/{questId:D}/spend");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(questId, body.RootElement.GetProperty("data").GetProperty("questId").GetGuid());

        Assert.Equal(0, body.RootElement.GetProperty("data").GetProperty("knownTokens").GetInt64());
    }


    [SkippableFact]
    public async Task Operator_child_issue_replays_before_parent_revision_check()
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

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Parent", "Delegate",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [GuildDelegationKind.Local], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid parent = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        long revision = accepted.RootElement.GetProperty("data").GetProperty("revision").GetInt64();

        GuildIssueChildRequest child = new(Guid.NewGuid(), revision, null, "Child", "Do work", null);

        string payload = JsonSerializer.Serialize(child, GuildJsonContext.Default.GuildIssueChildRequest);

        using HttpResponseMessage first = await client.PostAsync($"/api/guild/quests/{parent:D}/children", Json(payload));

        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());

        using JsonDocument created = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        Guid id = created.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using HttpResponseMessage replay = await client.PostAsync($"/api/guild/quests/{parent:D}/children", Json(payload));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using JsonDocument repeated = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

        Assert.Equal(id, repeated.RootElement.GetProperty("data").GetProperty("id").GetGuid());

        Assert.True(repeated.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("replayed").GetBoolean());
    }


    [SkippableFact]
    public async Task Operator_quest_list_pages_capture_fixed_membership()
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

        async Task<Guid> Issue(string name)
        {
            GuildIssueRootRequest request = new(Guid.NewGuid(), null, name, "Inspect list",
                new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

            using HttpResponseMessage response = await client.PostAsync("/api/guild/quests",
                Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            return body.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        }

        Guid firstId = await Issue("First");

        Guid secondId = await Issue("Second");

        using HttpResponseMessage first = await client.GetAsync("/api/guild/quests?pageSize=1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using JsonDocument firstPage = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        JsonElement data = firstPage.RootElement.GetProperty("data");

        Assert.Equal(firstId, Assert.Single(data.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        string cursor = data.GetProperty("nextCursor").GetString()!;

        long snapshot = data.GetProperty("snapshotSequence").GetInt64();

        _ = await Issue("Later");

        using HttpResponseMessage second = await client.GetAsync($"/api/guild/quests?pageSize=1&cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using JsonDocument secondPage = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        JsonElement secondData = secondPage.RootElement.GetProperty("data");

        Assert.Equal(secondId, Assert.Single(secondData.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        Assert.Equal(snapshot, secondData.GetProperty("snapshotSequence").GetInt64());

        Assert.Equal(JsonValueKind.Null, secondData.GetProperty("nextCursor").ValueKind);
    }


    [SkippableFact]
    public async Task Operator_signet_revocation_is_replayable_and_cannot_accept_owner_deletion_reason()
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

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Revocation", "Revoke authority",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        JsonElement quest = accepted.RootElement.GetProperty("data");

        Guid signetId = quest.GetProperty("signetId").GetGuid();

        long revision = quest.GetProperty("signetRevision").GetInt64();

        GuildRevokeSignetRequest revoke = new(Guid.NewGuid(), revision, GuildCancellationReason.OperatorRequested);

        string payload = JsonSerializer.Serialize(revoke, GuildJsonContext.Default.GuildRevokeSignetRequest);

        using HttpResponseMessage first = await client.PostAsync($"/api/guild/signets/{signetId:D}/revoke", Json(payload));

        Assert.True(first.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync());

        using JsonDocument revoked = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        Assert.True(revoked.RootElement.GetProperty("data").GetProperty("revoked").GetBoolean());

        using HttpResponseMessage replay = await client.PostAsync($"/api/guild/signets/{signetId:D}/revoke", Json(payload));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using JsonDocument repeated = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

        Assert.True(repeated.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("replayed").GetBoolean());

        GuildRevokeSignetRequest forbiddenReason = new(Guid.NewGuid(), revision, GuildCancellationReason.OwnerDeleted);

        using HttpResponseMessage denied = await client.PostAsync($"/api/guild/signets/{signetId:D}/revoke",
            Json(JsonSerializer.Serialize(forbiddenReason, GuildJsonContext.Default.GuildRevokeSignetRequest)));

        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
    }

}
