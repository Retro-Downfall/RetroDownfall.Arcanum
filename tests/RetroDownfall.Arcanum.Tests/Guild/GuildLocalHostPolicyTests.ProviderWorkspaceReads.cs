using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Provider_reads_lists_and_searches_only_its_physical_workspace_with_financial_controls_off(bool peerOwned, bool replaceReadAfterDisclosure)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: peerOwned);

        string[] names = ["read_file", "list_directory", "search_workspace"];

        Guid workspace = await f.AddWorkspaceAsync();

        Directory.CreateDirectory(Path.Combine(f.WorkspaceRoot, "allowed"));

        Directory.CreateDirectory(Path.Combine(f.WorkspaceRoot, "allowed-other"));

        const string source = "line 1\r\nneedle [source] `literal` ü ☃\r\nline 3";

        string readPath = Path.Combine(f.WorkspaceRoot, "allowed", "雪.txt");

        await File.WriteAllTextAsync(readPath, source);

        await File.WriteAllTextAsync(Path.Combine(f.WorkspaceRoot, "allowed-other", "00-secret.txt"), "needle OUTSIDE-QUEST-CANARY");

        ArcanumSettings settings = f.Settings.CurrentValue;

        settings.DefaultModel = "accepted-model";

        settings.Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1", GuildUsageReconciliation = GuildUsageReconciliationSource.None, Models = ["accepted-model"] }];

        settings.Cost.Budget.Enabled = false;

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new InboundScheduler());

        GuildCallerContext scheduler = await f.OperatorAsync();

        GuildQuestStored root;

        if (peerOwned)
        {
            ReloadInbound(f, item =>
            {
                item.CampaignId = workspace;

                item.WorkspaceId = workspace;

                item.Grant!.Workspaces = [new() { WorkspaceId = workspace, RelativeRoot = "allowed", Access = GuildWorkspaceAccess.Read }];

                item.Grant.Tools = [.. names.Select(name => new GuildToolGrantSettings
                { SourceKind = GuildToolSourceKind.InternalMcp, SourceRegistrationId = "arcanum", Name = name })];
            });

            GuildInboundPeer peer = Assert.IsType<GuildInboundPeer>(f.InboundPeers!.Resolve(Fixture.PeerCredential));

            GuildInboundHost host = new(f.Policy, f.Store, coordinator);

            var issued = await host.IssueAsync(peer, "workspace-read-message", "workspace-read-task", null, "Read the permitted workspace", default);

            Assert.True(issued.IsSuccess, issued.Error.Message);

            var caller = await f.Policy.CreatePeerCallerAsync(peer, Guid.NewGuid(), default);

            Assert.True(caller.IsSuccess, caller.Error.Message);

            var stored = await f.Store.GetAsync(caller.Value, issued.Value.Quest.Id, default);

            Assert.True(stored.IsSuccess, stored.Error.Message);

            root = stored.Value;

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_inbound_tasks"));
        }
        else
        {
            var issued = await f.IssueAsync(scheduler, f.Request with { WorkspaceId = workspace, Grant = f.Request.Grant with
                { Workspaces = [new(workspace, "allowed", GuildWorkspaceAccess.Read)],
                    Tools = [.. names.Select(name => new GuildToolGrant(GuildToolSourceKind.InternalMcp, "arcanum", name))] } });

            Assert.True(issued.IsSuccess, issued.Error.Message);

            root = issued.Value;
        }

        // The host scheduler rebinds accepted work, including peer-owned Quests, to an authenticated Run.
        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, scheduler, root);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        using WorkspaceReadTransport transport = new(
            ("read_file", """{"relativePath":"allowed/雪.txt","startLine":2,"endLine":2}"""),
            ("list_directory", """{"relativePath":"allowed","recursive":true}"""),
            ("search_workspace", """{"root":"allowed","pattern":"needle","mode":"literal","caseSensitive":true}"""),
            ("read_file", """{"relativePath":"allowed-other/00-secret.txt","startLine":1,"endLine":1}"""));

        if (replaceReadAfterDisclosure)
            transport.BeforeResponse = async ordinal =>
            {
                if (ordinal != 2) return;

                File.Move(readPath, readPath + ".old");

                await File.WriteAllTextAsync(readPath, "replacement bytes");
            };

        ChatClientFactory clients = new(transport, f.Settings);

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(new BudgetReservationService(db, f.Settings), settings, clients, coordinator);

            var result = await runtime.Runtime.ExecuteTurnAsync(execution, input.Value,
                new("Use the permitted workspace tools.", DisableAllTools: false), default);

            Assert.NotEmpty(transport.Bodies);

            using JsonDocument first = JsonDocument.Parse(transport.Bodies[0]);

            Assert.True(first.RootElement.TryGetProperty("tools", out JsonElement tools), "The authorized provider round did not advertise workspace tools.");

            Assert.Equal(names.Order(StringComparer.Ordinal), tools.EnumerateArray()
                .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).Order(StringComparer.Ordinal));

            if (replaceReadAfterDisclosure)
            {
                Assert.True(result.IsFailure);

                Assert.Equal(2, transport.Bodies.Count);

                using JsonDocument firstRead = ReadWorkspaceToolResult(Assert.Single(WorkspaceToolResults(transport.Bodies[1])));

                Assert.Equal("needle [source] `literal` ü ☃", firstRead.RootElement.GetProperty("content").GetString());

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=1"));

                return;
            }

            Assert.True(result.IsSuccess, $"Provider requests: {transport.Bodies.Count}. {result.Error.Code}: {result.Error.Message}");

            Assert.Equal("used physical workspace results", result.Value.Text);

            Assert.Equal(5, transport.Bodies.Count);

            string[] results = WorkspaceToolResults(transport.Bodies[^1]);

            Assert.Equal(4, results.Length);

            using JsonDocument read = ReadWorkspaceToolResult(results[0]);

            Assert.Equal("needle [source] `literal` ü ☃", read.RootElement.GetProperty("content").GetString());

            using JsonDocument list = ReadWorkspaceToolResult(results[1]);

            Assert.Equal("allowed/雪.txt", Assert.Single(list.RootElement.GetProperty("paths").EnumerateArray()).GetString());

            using JsonDocument search = ReadWorkspaceToolResult(results[2]);

            JsonElement match = Assert.Single(search.RootElement.GetProperty("matches").EnumerateArray());

            Assert.Equal("allowed/雪.txt", match.GetProperty("path").GetString());

            Assert.Equal("needle [source] `literal` ü ☃", match.GetProperty("preview").GetString());

            Assert.Contains("Guild.Forbidden", results[3]);

            Assert.All(results, text => Assert.DoesNotContain("OUTSIDE-QUEST-CANARY", text));

            Assert.Equal(0, transport.MetadataRequests);

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

            Assert.Equal(5L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=2 AND UnknownUsd=1 AND UnknownTokens=1"));

            Assert.Equal(5L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations WHERE InputTokens=0 AND OutputTokens=0 AND CAST(ActualCostUsd AS NUMERIC)=0"));
        }
        finally { await wizard.DisposeAsync(); }
    }

    private static string[] WorkspaceToolResults(string body)
    {
        using JsonDocument payload = JsonDocument.Parse(body);

        return [.. payload.RootElement.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "tool")
            .Select(message => message.GetProperty("content").GetString()!)];
    }

    private static JsonDocument ReadWorkspaceToolResult(string result)
    {
        Assert.Contains("UNTRUSTED DATA", result);

        int first = result.IndexOf('{');

        int last = result.LastIndexOf('}');

        Assert.True(first >= 0 && last > first, result);

        return JsonDocument.Parse(result.AsMemory(first, last - first + 1));
    }

    private sealed class WorkspaceReadTransport(params (string Name, string Arguments)[] calls) : HttpMessageHandler, IHttpClientFactory
    {
        internal List<string> Bodies { get; } = [];

        internal int MetadataRequests { get; private set; }

        internal Func<int, Task>? BeforeResponse { get; set; }

        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post)
            {
                MetadataRequests++;

                throw new InvalidOperationException("Unexpected provider metadata request.");
            }

            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));

            int ordinal = Bodies.Count;

            if (BeforeResponse is { } before) await before(ordinal);

            string message = ordinal <= calls.Length
                ? $$$"""{"role":"assistant","content":null,"tool_calls":[{"id":"call-{{{ordinal}}}","type":"function","function":{"name":"{{{calls[ordinal - 1].Name}}}","arguments":{{{JsonSerializer.Serialize(calls[ordinal - 1].Arguments, ArcanumCoreJsonContext.Default.String)}}}}}]}"""
                : """{"role":"assistant","content":"used physical workspace results"}""";

            string finish = ordinal <= calls.Length ? "tool_calls" : "stop";

            string json = $$$"""{"id":"response-{{{ordinal}}}","object":"chat.completion","created":1700000000,"model":"accepted-model","choices":[{"index":0,"message":{{{message}}},"finish_reason":"{{{finish}}}"}]}""";

            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
