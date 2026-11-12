using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [SkippableFact]
    public async Task Operator_roster_refresh_discovers_global_cards_and_replays_without_reading_changed_authored_bytes()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => { settings.Features.Guild = true; settings.Features.Apprentices = true; return settings; },

            ServiceOverrides = HoldExecution,
        };

        string directory = Path.Combine(Path.GetDirectoryName(ArcanumPaths.GlobalSpellsDirectory)!, "agents");

        Directory.CreateDirectory(directory);

        string source = Path.Combine(directory, "reviewer.md");

        await File.WriteAllTextAsync(source, "---\nname: NativeReviewer\ndescription: Reviews authored tasks\nskills: [review]\ntools: []\n---\nUntrusted reviewer guidance.\n", new UTF8Encoding(false));

        using HttpClient client = factory.CreateAuthenticatedClient();

        string body = "{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\"}";

        using HttpResponseMessage refreshed = await client.PostAsync("/api/guild/agents/refresh", Json(body));

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        using JsonDocument receipt = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());

        JsonElement accepted = receipt.RootElement.GetProperty("data");

        Assert.True(accepted.GetProperty("completed").GetBoolean());

        Assert.False(accepted.GetProperty("receipt").GetProperty("replayed").GetBoolean());

        using HttpResponseMessage listed = await client.GetAsync("/api/guild/agents?source=declaredGlobal&skill=review&pageSize=1");

        Assert.True(listed.StatusCode == HttpStatusCode.OK, await listed.Content.ReadAsStringAsync());

        using JsonDocument cards = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());

        JsonElement card = Assert.Single(cards.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());

        Assert.Equal("NativeReviewer", card.GetProperty("name").GetString());

        Assert.True(card.GetProperty("source").GetProperty("available").GetBoolean());

        Guid agent = card.GetProperty("agentId").GetGuid();

        long revision = card.GetProperty("revision").GetInt64();

        using HttpResponseMessage exact = await client.GetAsync($"/api/guild/agents/{agent:D}?cardRevision={revision}");

        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);

        await File.WriteAllTextAsync(source, "invalid changed source", new UTF8Encoding(false));

        using HttpResponseMessage replayed = await client.PostAsync("/api/guild/agents/refresh", Json(body));

        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);

        using JsonDocument replay = JsonDocument.Parse(await replayed.Content.ReadAsStringAsync());

        JsonElement replayReceipt = replay.RootElement.GetProperty("data").GetProperty("receipt");

        Assert.True(replayReceipt.GetProperty("replayed").GetBoolean());

        Assert.Equal(accepted.GetProperty("receipt").GetProperty("id").GetGuid(), replayReceipt.GetProperty("id").GetGuid());

        using HttpResponseMessage unchanged = await client.GetAsync($"/api/guild/agents/{agent:D}?cardRevision={revision}");

        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);

        using JsonDocument stillAvailable = JsonDocument.Parse(await unchanged.Content.ReadAsStringAsync());

        Assert.True(stillAvailable.RootElement.GetProperty("data").GetProperty("source").GetProperty("available").GetBoolean());
    }

    [SkippableFact]
    public async Task Operator_roster_refresh_pages_workspace_sources_and_refuses_changed_source_sets()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => { settings.Features.Guild = true; settings.Features.Apprentices = true; return settings; },

            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        string root = Path.Combine(factory.TempHome, "native-roster-workspace");

        Directory.CreateDirectory(root);

        async Task Register(string path, string name)
        {
            Directory.CreateDirectory(path);

            using HttpResponseMessage registered = await client.PostAsync("/api/workspaces", Json(JsonSerializer.Serialize(
                new RetroDownfall.Arcanum.Core.Workspaces.CreateWorkspaceRequest(name, path, RetroDownfall.Arcanum.Core.Workspaces.WorkspaceType.Campaign),
                RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.CreateWorkspaceRequest)));

            Assert.True(registered.StatusCode == HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        }

        await Register(root, "Native Roster Workspace");

        string directory = Path.Combine(root, ".arcanum", "agents");

        Directory.CreateDirectory(directory);

        for (int index = 0; index < 101; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, $"reviewer-{index:D3}.md"),
                $"---\nname: WorkspaceReviewer{index:D3}\ndescription: Scoped review task\ntools: []\n---\nUntrusted guidance.\n", new UTF8Encoding(false));
        }

        string? cursor = null;

        HashSet<Guid> agents = [];

        bool completed = false;

        // A test hang guard: product source counts and refresh turns have no total cap.
        for (int turn = 0; turn < 20 && !completed; turn++)
        {
            string body = "{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\",\"cursor\":"
                + (cursor is null ? "null" : "\"" + cursor + "\"") + "}";

            using HttpResponseMessage response = await client.PostAsync("/api/guild/agents/refresh", Json(body));

            string jsonText = await response.Content.ReadAsStringAsync();

            Assert.True(response.StatusCode == HttpStatusCode.OK, jsonText);

            using JsonDocument json = JsonDocument.Parse(jsonText);

            JsonElement data = json.RootElement.GetProperty("data");

            JsonElement diagnostics = data.GetProperty("diagnostics");

            Assert.InRange(diagnostics.GetProperty("items").GetArrayLength(), 0, 100);

            foreach (JsonElement item in diagnostics.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("agentId").ValueKind == JsonValueKind.String) agents.Add(item.GetProperty("agentId").GetGuid());
            }

            cursor = diagnostics.GetProperty("nextCursor").GetString();

            completed = data.GetProperty("completed").GetBoolean();

            Assert.Equal(completed, cursor is null);
        }

        Assert.True(completed);

        Assert.Equal(101, agents.Count);

        using HttpResponseMessage listed = await client.GetAsync("/api/guild/agents?source=declaredWorkspace&pageSize=100");

        Assert.True(listed.StatusCode == HttpStatusCode.OK, await listed.Content.ReadAsStringAsync());

        using JsonDocument first = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());

        JsonElement firstPage = first.RootElement.GetProperty("data");

        Assert.Equal(100, firstPage.GetProperty("items").GetArrayLength());

        Assert.All(firstPage.GetProperty("items").EnumerateArray(), static card => Assert.True(card.GetProperty("source").GetProperty("available").GetBoolean()));

        string listCursor = firstPage.GetProperty("nextCursor").GetString()!;

        using HttpResponseMessage second = await client.GetAsync("/api/guild/agents?source=declaredWorkspace&pageSize=100&cursor=" + listCursor);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using JsonDocument remainder = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        Assert.Single(remainder.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());

        using HttpResponseMessage mismatched = await client.GetAsync("/api/guild/agents?source=declaredGlobal&pageSize=100&cursor=" + listCursor);

        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);

        using HttpResponseMessage nextScan = await client.PostAsync("/api/guild/agents/refresh", Json("{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\"}"));

        Assert.Equal(HttpStatusCode.OK, nextScan.StatusCode);

        using JsonDocument scan = JsonDocument.Parse(await nextScan.Content.ReadAsStringAsync());

        string nextCursor = scan.RootElement.GetProperty("data").GetProperty("diagnostics").GetProperty("nextCursor").GetString()!;

        await Register(Path.Combine(factory.TempHome, "another-native-workspace"), "Changed Roster source set");

        using HttpResponseMessage changed = await client.PostAsync("/api/guild/agents/refresh", Json("{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\",\"cursor\":\"" + nextCursor + "\"}"));

        Assert.Equal(HttpStatusCode.Gone, changed.StatusCode);
    }

    [SkippableTheory]
    [InlineData("workspaces")]
    [InlineData("campaigns")]
    public async Task Registration_records_metadata_without_workspace_effects_while_Guild_is_enabled(string resource)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => { settings.Features.Guild = true; settings.Features.Apprentices = true; return settings; },

            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        string root = Path.Combine(factory.TempHome, "registration-" + resource);

        Directory.CreateDirectory(root);

        string body = resource == "workspaces"
            ? JsonSerializer.Serialize(new RetroDownfall.Arcanum.Core.Workspaces.CreateWorkspaceRequest("Metadata only", root, RetroDownfall.Arcanum.Core.Workspaces.WorkspaceType.Campaign),
                RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.CreateWorkspaceRequest)
            : JsonSerializer.Serialize(new RetroDownfall.Arcanum.Core.Tower.RegisterCampaignRequest("Metadata only", root, RetroDownfall.Arcanum.Core.Workspaces.WorkspaceType.Campaign, null),
                RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.RegisterCampaignRequest);

        using HttpResponseMessage response = await client.PostAsync("/api/" + resource, Json(body));

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [SkippableFact]
    public async Task Operator_roster_remote_refresh_uses_configured_discovery_and_accepted_replay_does_not_refetch()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        using NativeRosterRemoteHandler handler = new();

        RetroDownfall.Arcanum.Tests.Support.FakeDnsResolver dns = new();

        dns.Add("native-roster.example", IPAddress.Parse("93.184.216.34"));

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                settings.Features.A2AClient = true;

                settings.Integrations.A2A.AllowedRemoteAgents = [NativeRosterRemote];

                return settings;
            },

            ServiceOverrides = services =>
            {
                HoldExecution(services);

                services.RemoveAll<IHttpClientFactory>();

                services.AddSingleton<IHttpClientFactory>(new NativeRosterHttpFactory(handler));

                services.RemoveAll<RetroDownfall.Arcanum.Core.Security.IDnsResolver>();

                services.AddSingleton<RetroDownfall.Arcanum.Core.Security.IDnsResolver>(dns);
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage initial = await client.PostAsync("/api/guild/agents/refresh", Json("{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\"}"));

        Assert.True(initial.StatusCode == HttpStatusCode.OK, await initial.Content.ReadAsStringAsync());

        using JsonDocument first = JsonDocument.Parse(await initial.Content.ReadAsStringAsync());

        JsonElement batch = first.RootElement.GetProperty("data");

        Assert.False(batch.GetProperty("completed").GetBoolean());

        string cursor = batch.GetProperty("diagnostics").GetProperty("nextCursor").GetString()!;

        string body = "{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\",\"cursor\":\"" + cursor + "\"}";

        using HttpResponseMessage remote = await client.PostAsync("/api/guild/agents/refresh", Json(body));

        Assert.True(remote.StatusCode == HttpStatusCode.OK, await remote.Content.ReadAsStringAsync());

        using JsonDocument refreshed = JsonDocument.Parse(await remote.Content.ReadAsStringAsync());

        JsonElement receipt = refreshed.RootElement.GetProperty("data");

        Assert.True(receipt.GetProperty("completed").GetBoolean());

        JsonElement diagnostic = Assert.Single(receipt.GetProperty("diagnostics").GetProperty("items").EnumerateArray());

        Assert.True(diagnostic.GetProperty("available").GetBoolean());

        Assert.Equal("remote", diagnostic.GetProperty("sourceKind").GetString());

        Assert.True(handler.Requests > 0);

        int requestsBeforeReplay = handler.Requests;

        using HttpResponseMessage replayed = await client.PostAsync("/api/guild/agents/refresh", Json(body));

        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);

        using JsonDocument replay = JsonDocument.Parse(await replayed.Content.ReadAsStringAsync());

        Assert.True(replay.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("replayed").GetBoolean());

        Assert.Equal(0, handler.Requests - requestsBeforeReplay);

        using HttpResponseMessage list = await client.GetAsync("/api/guild/agents?source=remote");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        using JsonDocument listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());

        Assert.Equal("Native remote", Assert.Single(listed.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()).GetProperty("name").GetString());
    }

    private const string NativeRosterRemote = "https://native-roster.example/";

    private sealed class NativeRosterHttpFactory(NativeRosterRemoteHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NativeRosterRemoteHandler : HttpMessageHandler
    {
        internal int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);

            Assert.Equal("native-roster.example", request.RequestUri!.Host);

            Assert.Equal(HttpMethod.Get, request.Method);

            global::A2A.AgentCard card = new()
            {
                Name = "Native remote",

                Description = "Configured remote source",

                Version = "1",

                SupportedInterfaces = [new() { Url = NativeRosterRemote + "a2a", ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],

                Capabilities = new() { Streaming = true },

                Skills = [],

                DefaultInputModes = ["text/plain"],

                DefaultOutputModes = ["text/plain"],
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(card,
                    (System.Text.Json.Serialization.Metadata.JsonTypeInfo<global::A2A.AgentCard>)global::A2A.A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(global::A2A.AgentCard)))),
            });
        }
    }

}
