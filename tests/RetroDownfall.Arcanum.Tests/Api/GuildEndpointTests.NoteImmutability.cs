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
    public async Task Operator_note_update_and_delete_routes_are_unavailable_and_the_original_remains_replayable()
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

        using JsonDocument cohortData = JsonDocument.Parse(await created.Content.ReadAsStringAsync());

        Guid cohort = cohortData.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        AppendSharedNoteRequest note = new(Guid.NewGuid(), 1, GuildNoteKind.Observation, null, "Immutable operator note", null);

        string payload = JsonSerializer.Serialize(note, GuildJsonContext.Default.AppendSharedNoteRequest);

        string collection = $"/api/guild/cohorts/{cohort:D}/notes";

        using HttpResponseMessage first = await client.PostAsync(collection, Json(payload));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using JsonDocument saved = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        Guid id = saved.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        foreach (string path in new[] { collection, $"{collection}/{id:D}" })
        {
            foreach (HttpMethod method in new[] { HttpMethod.Delete, HttpMethod.Patch, HttpMethod.Put })
            {
                using HttpRequestMessage mutation = new(method, path) { Content = Json(payload) };

                using HttpResponseMessage refused = await client.SendAsync(mutation);

                Assert.Contains(refused.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
            }
        }

        using HttpResponseMessage read = await client.GetAsync(collection);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using JsonDocument page = JsonDocument.Parse(await read.Content.ReadAsStringAsync());

        JsonElement item = Assert.Single(page.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());

        Assert.Equal(id, item.GetProperty("id").GetGuid());

        Assert.Contains("Immutable operator note", item.GetProperty("body").GetString());

        using HttpResponseMessage replay = await client.PostAsync(collection, Json(payload));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using JsonDocument repeated = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

        Assert.Equal(id, repeated.RootElement.GetProperty("data").GetProperty("id").GetGuid());

        Assert.True(repeated.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("replayed").GetBoolean());
    }
}
