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
    public async Task Operator_lease_pages_pin_membership_and_mutations_replay_original_receipts()
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

        string root = Path.Combine(factory.TempHome, "leases");

        Directory.CreateDirectory(root);

        using HttpResponseMessage registered = await client.PostAsync("/api/workspaces", Json(
            JsonSerializer.Serialize(new RetroDownfall.Arcanum.Core.Workspaces.CreateWorkspaceRequest("Lease workspace", root,
                RetroDownfall.Arcanum.Core.Workspaces.WorkspaceType.Campaign), RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.CreateWorkspaceRequest)));

        Assert.True(registered.StatusCode == HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());

        using JsonDocument workspace = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());

        Guid workspaceId = Guid.Parse(workspace.RootElement.GetProperty("data").GetProperty("id").GetString()!);

        async Task<JsonElement> Acquire(string path)
        {
            AcquireWorkspaceLeaseRequest request = new(Guid.NewGuid(), workspaceId, [new(path, GuildPathScopeKind.File)]);

            string body = JsonSerializer.Serialize(request, GuildJsonContext.Default.AcquireWorkspaceLeaseRequest);

            using HttpResponseMessage first = await client.PostAsync("/api/guild/workspace-leases", Json(body));

            Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());

            using JsonDocument data = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

            using HttpResponseMessage replay = await client.PostAsync("/api/guild/workspace-leases", Json(body));

            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

            using JsonDocument repeated = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

            Assert.Equal(data.RootElement.GetProperty("data").GetProperty("id").GetGuid(),
                repeated.RootElement.GetProperty("data").GetProperty("id").GetGuid());

            Assert.True(repeated.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("replayed").GetBoolean());

            return data.RootElement.GetProperty("data").Clone();
        }

        JsonElement firstLease = await Acquire("one.txt");

        JsonElement secondLease = await Acquire("two.txt");

        using HttpResponseMessage firstPage = await client.GetAsync("/api/guild/workspace-leases?pageSize=1");

        Assert.True(firstPage.StatusCode == HttpStatusCode.OK, await firstPage.Content.ReadAsStringAsync());

        using JsonDocument firstJson = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());

        JsonElement page = firstJson.RootElement.GetProperty("data");

        Assert.Equal(firstLease.GetProperty("id").GetGuid(), Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        string cursor = page.GetProperty("nextCursor").GetString()!;

        _ = await Acquire("three.txt");

        using HttpResponseMessage secondPage = await client.GetAsync($"/api/guild/workspace-leases?pageSize=100&cursor={Uri.EscapeDataString(cursor)}");

        Assert.True(secondPage.StatusCode == HttpStatusCode.OK, await secondPage.Content.ReadAsStringAsync());

        using JsonDocument secondJson = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());

        JsonElement continued = secondJson.RootElement.GetProperty("data");

        Assert.Equal(secondLease.GetProperty("id").GetGuid(), Assert.Single(continued.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        Assert.Equal(page.GetProperty("snapshotSequence").GetInt64(), continued.GetProperty("snapshotSequence").GetInt64());

        Assert.Equal(JsonValueKind.Null, continued.GetProperty("nextCursor").ValueKind);

        using HttpResponseMessage crossQuery = await client.GetAsync($"/api/guild/workspace-leases?workspaceId={workspaceId:D}&cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.BadRequest, crossQuery.StatusCode);

        Guid leaseId = firstLease.GetProperty("id").GetGuid();

        RenewWorkspaceLeaseRequest renewal = new(Guid.NewGuid(), firstLease.GetProperty("revision").GetInt64(), firstLease.GetProperty("epoch").GetInt64());

        string renewalBody = JsonSerializer.Serialize(renewal, GuildJsonContext.Default.RenewWorkspaceLeaseRequest);

        using HttpResponseMessage renewed = await client.PostAsync($"/api/guild/workspace-leases/{leaseId:D}/renew", Json(renewalBody));

        Assert.True(renewed.StatusCode == HttpStatusCode.OK, await renewed.Content.ReadAsStringAsync());

        using JsonDocument renewalJson = JsonDocument.Parse(await renewed.Content.ReadAsStringAsync());

        ReleaseWorkspaceLeaseRequest release = new(Guid.NewGuid(), renewalJson.RootElement.GetProperty("data").GetProperty("revision").GetInt64(), renewal.Epoch);

        string releaseBody = JsonSerializer.Serialize(release, GuildJsonContext.Default.ReleaseWorkspaceLeaseRequest);

        using HttpResponseMessage released = await client.PostAsync($"/api/guild/workspace-leases/{leaseId:D}/release", Json(releaseBody));

        Assert.True(released.StatusCode == HttpStatusCode.OK, await released.Content.ReadAsStringAsync());

        using HttpResponseMessage renewalReplay = await client.PostAsync($"/api/guild/workspace-leases/{leaseId:D}/renew", Json(renewalBody));

        Assert.Equal(HttpStatusCode.OK, renewalReplay.StatusCode);

        using JsonDocument replayJson = JsonDocument.Parse(await renewalReplay.Content.ReadAsStringAsync());

        Assert.Equal("held", replayJson.RootElement.GetProperty("data").GetProperty("state").GetString());

        Assert.Equal(renewalJson.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("id").GetGuid(),
            replayJson.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("id").GetGuid());

        Assert.True(replayJson.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("replayed").GetBoolean());

        AcquireWorkspaceLeaseRequest oversized = new(Guid.NewGuid(), workspaceId,
            [new(new string('a', 65536), GuildPathScopeKind.File)]);

        using HttpResponseMessage tooLarge = await client.PostAsync("/api/guild/workspace-leases",
            Json(JsonSerializer.Serialize(oversized, GuildJsonContext.Default.AcquireWorkspaceLeaseRequest)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
    }
}
