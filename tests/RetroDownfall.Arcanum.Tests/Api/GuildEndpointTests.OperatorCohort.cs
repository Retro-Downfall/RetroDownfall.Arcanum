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
    public async Task Operator_create_cohort_replays_exact_receipt_and_requires_live_issuer_authority()
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

        GuildIssueRootRequest root = new(Guid.NewGuid(), null, "Issuer", "Coordinate work",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)),
                [GuildDelegationKind.Local, GuildDelegationKind.Cohort], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(root, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid issuer = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        CreateCohortRequest request = new(Guid.NewGuid(), issuer, "Reviewers", new(GuildConvergenceKind.All, null), new("text/plain"), null);

        string payload = JsonSerializer.Serialize(request, GuildJsonContext.Default.CreateCohortRequest);

        using HttpResponseMessage first = await client.PostAsync("/api/guild/cohorts", Json(payload));

        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());

        using JsonDocument created = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        Guid cohort = created.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using HttpResponseMessage replay = await client.PostAsync("/api/guild/cohorts", Json(payload));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using JsonDocument repeated = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

        Assert.Equal(cohort, repeated.RootElement.GetProperty("data").GetProperty("id").GetGuid());

        Assert.True(repeated.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("replayed").GetBoolean());

        CreateCohortRequest missing = request with { IdempotencyKey = Guid.NewGuid(), IssuerQuestId = Guid.NewGuid() };

        using HttpResponseMessage denied = await client.PostAsync("/api/guild/cohorts",
            Json(JsonSerializer.Serialize(missing, GuildJsonContext.Default.CreateCohortRequest)));

        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [SkippableFact]
    public async Task Operator_cohort_list_pins_membership_across_pages()
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

        GuildIssueRootRequest root = new(Guid.NewGuid(), null, "Issuer", "Coordinate work",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)),
                [GuildDelegationKind.Local, GuildDelegationKind.Cohort], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(root, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid issuer = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        async Task<Guid> Create(string name)
        {
            CreateCohortRequest request = new(Guid.NewGuid(), issuer, name, new(GuildConvergenceKind.All, null), new("text/plain"), null);

            using HttpResponseMessage response = await client.PostAsync("/api/guild/cohorts", Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.CreateCohortRequest)));

            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            return body.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        }

        Guid firstId = await Create("First");

        Guid secondId = await Create("Second");

        using HttpResponseMessage first = await client.GetAsync("/api/guild/cohorts?pageSize=1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using JsonDocument firstPage = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        JsonElement data = firstPage.RootElement.GetProperty("data");

        Assert.Equal(firstId, Assert.Single(data.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        string cursor = data.GetProperty("nextCursor").GetString()!;

        long snapshot = data.GetProperty("snapshotSequence").GetInt64();

        _ = await Create("Later");

        using HttpResponseMessage second = await client.GetAsync($"/api/guild/cohorts?pageSize=1&cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using JsonDocument secondPage = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        JsonElement secondData = secondPage.RootElement.GetProperty("data");

        Assert.Equal(secondId, Assert.Single(secondData.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        Assert.Equal(snapshot, secondData.GetProperty("snapshotSequence").GetInt64());

        Assert.Equal(JsonValueKind.Null, secondData.GetProperty("nextCursor").ValueKind);
    }


    [SkippableFact]
    public async Task Operator_shared_note_preserves_authority_body_and_replay_receipt()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();
        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => { settings.Features.Guild = true; settings.Features.Apprentices = true; return settings; },
            ServiceOverrides = HoldExecution,
        };
        using HttpClient client = factory.CreateAuthenticatedClient();
        GuildIssueRootRequest root = new(Guid.NewGuid(), null, "Issuer", "Coordinate notes",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [GuildDelegationKind.Local, GuildDelegationKind.Cohort], null));
        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests", Json(JsonSerializer.Serialize(root, GuildJsonContext.Default.GuildIssueRootRequest)));
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        Guid issuer = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        CreateCohortRequest request = new(Guid.NewGuid(), issuer, "Notes", new(GuildConvergenceKind.All, null), new("text/plain"), null);
        using HttpResponseMessage created = await client.PostAsync("/api/guild/cohorts", Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.CreateCohortRequest)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid cohort = body.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        AppendSharedNoteRequest note = new(Guid.NewGuid(), 1, GuildNoteKind.Observation, null, "Operator note", null);
        string payload = JsonSerializer.Serialize(note, GuildJsonContext.Default.AppendSharedNoteRequest);
        using HttpResponseMessage first = await client.PostAsync($"/api/guild/cohorts/{cohort:D}/notes", Json(payload));
        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        using JsonDocument saved = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Guid id = saved.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        using HttpResponseMessage replay = await client.PostAsync($"/api/guild/cohorts/{cohort:D}/notes", Json(payload));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using JsonDocument repeated = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(id, repeated.RootElement.GetProperty("data").GetProperty("id").GetGuid());
        Assert.True(repeated.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("replayed").GetBoolean());
        using HttpResponseMessage read = await client.GetAsync($"/api/guild/cohorts/{cohort:D}/notes");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using JsonDocument page = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        JsonElement item = Assert.Single(page.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Equal("operator", item.GetProperty("authority").GetString());
        Assert.Contains("Operator note", item.GetProperty("body").GetString());
    }


    [SkippableFact]
    public async Task Operator_admits_one_member_and_replays_the_same_association()
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

        GuildIssueRootRequest root = new(Guid.NewGuid(), null, "Issuer", "Coordinate work",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)),
                [GuildDelegationKind.Local, GuildDelegationKind.Cohort], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(root, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid issuer = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        CreateCohortRequest create = new(Guid.NewGuid(), issuer, "Team", new(GuildConvergenceKind.All, null), new("text/plain"), null);

        using HttpResponseMessage created = await client.PostAsync("/api/guild/cohorts", Json(JsonSerializer.Serialize(create, GuildJsonContext.Default.CreateCohortRequest)));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using JsonDocument cohortBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());

        Guid cohort = cohortBody.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        AdmitCohortMemberRequest request = new(Guid.NewGuid(), 1, new(null, "Member", "Contribute", null));

        string payload = JsonSerializer.Serialize(request, GuildJsonContext.Default.AdmitCohortMemberRequest);

        using HttpResponseMessage first = await client.PostAsync($"/api/guild/cohorts/{cohort:D}/members", Json(payload));

        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());

        using JsonDocument admitted = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        Guid member = admitted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using HttpResponseMessage replay = await client.PostAsync($"/api/guild/cohorts/{cohort:D}/members", Json(payload));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using JsonDocument repeated = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

        Assert.Equal(member, repeated.RootElement.GetProperty("data").GetProperty("id").GetGuid());

        Assert.True(repeated.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("replayed").GetBoolean());

        using HttpResponseMessage list = await client.GetAsync($"/api/guild/cohorts/{cohort:D}/members");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        using JsonDocument page = JsonDocument.Parse(await list.Content.ReadAsStringAsync());

        Assert.Equal(member, Assert.Single(page.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

}
