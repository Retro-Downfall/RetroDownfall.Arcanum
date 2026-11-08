using System.Net;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("before-child")]
    [InlineData("after-child")]
    [InlineData("absent")]
    public async Task Qualified_provider_tool_loop_uses_closed_registry_and_one_bill_per_actual_round(string? readinessLoss)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task");

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext run = await RunCallerAsync(f, owner, root.Value);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var leased = await operations.TryAcquireLeaseAsync(root.Value.Quest.OperationId, "provider-tool", now, now.AddMinutes(5));

        Assert.True(leased.Acquired);

        GuildQuestDriver driver = new(run.Execution!, "provider-tool", leased.Operation.Revision, run);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), new string('C', 64), root.Value.Quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

        var input = await f.Store.ReadLocalInputAsync(driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        ArcanumSettings settings = new()
        {
            Features = new() { Guild = true }, DefaultModel = "accepted-model",
            Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://example.test/v1", Models = ["accepted-model"] }],
        };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        using ChildToolTransport transport = new();

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings));

        ProviderChildDispatcher children = new(f);

        GuildQuestStore effectStore = readinessLoss == "absent"
            ? new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy, new GuildBudgetLedger(), TimeProvider.System)
            : f.Store;

        if (readinessLoss == "before-child") transport.BeforeResponse = ordinal => ordinal == 1
            ? readiness.DisposeAsync().AsTask() : Task.CompletedTask;

        if (readinessLoss == "after-child") children.AfterCommit = () => readiness.DisposeAsync().AsTask();

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients, children);

            var result = await runtime.Runtime.ExecuteTurnAsync(new(effectStore, driver, dispatch.Value), input.Value,
                new("Solve the admitted task using the permitted child tool.", DisableAllTools: false), default);

            if (readinessLoss == "absent")
            {
                Assert.True(result.IsFailure);

                Assert.Empty(transport.Bodies);

                Assert.Equal(0, children.Calls);

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

                return;
            }

            if (readinessLoss is not null)
            {
                Assert.True(readiness.RequireExecutionReady().IsFailure);

                Assert.True(result.IsFailure);

                Assert.Single(transport.Bodies);

                Assert.Equal(readinessLoss == "before-child" ? 0 : 1, children.Calls);

                Assert.Equal(readinessLoss == "before-child" ? 1L : 2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

                // Known physical usage settles after readiness is lost; no second paid round begins.
                Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

                return;
            }

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal("final parent answer", result.Value.Text);

            Assert.Equal(2, transport.Bodies.Count);

            Assert.Equal(1, children.Calls);

            Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

            Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM InferenceRuns"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM Entries"));

            using JsonDocument first = JsonDocument.Parse(transport.Bodies[0]);

            Assert.Equal("delegate_task", first.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());

            Assert.Single(first.RootElement.GetProperty("tools").EnumerateArray());

            Assert.Contains("UNTRUSTED DATA", transport.Bodies[1], StringComparison.Ordinal);
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    private sealed class ProviderChildDispatcher(Fixture fixture) : IGuildLocalChildToolDispatcher
    {
        internal int Calls;

        internal Func<Task>? AfterCommit;

        public async Task<Result<GuildLocalChildToolResult>> DispatchAsync(GuildLocalChildToolCall call, CancellationToken ct)
        {
            Calls++;

            fixture.ToolChildMode = GuildLocalExecutionMode.Sterile;

            Guid key = call.Admission.LogicalCallId;

            GuildIssueChildRequest request = new(key, call.ParentQuest.Revision, null, "Child",
                call.Arguments.GetProperty("prompt").GetString()!, new());

            var accepted = await fixture.Store.IssueToolChildAsync(call.Admission,
                new(fixture.Generation, call.ParentQuest.Id, null, call.Admission.IssuingStepId, key), request, ct);

            if (accepted.IsSuccess && AfterCommit is not null) await AfterCommit();

            return accepted.IsFailure ? accepted.Error : new GuildLocalChildToolResult(accepted.Value.Quest, "child result");
        }
    }

    private sealed class ChildToolTransport : HttpMessageHandler, IHttpClientFactory
    {
        internal List<string> Bodies { get; } = [];

        internal Func<int, Task>? BeforeResponse;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));

            if (BeforeResponse is not null) await BeforeResponse(Bodies.Count);

            string message = Bodies.Count == 1
                ? """{"role":"assistant","content":null,"tool_calls":[{"id":"call-child","type":"function","function":{"name":"delegate_task","arguments":"{\"prompt\":\"child task\",\"max_cost_usd\":1}"}}]}"""
                : """{"role":"assistant","content":"final parent answer"}""";

            string json = $$$"""{"id":"response-{{{Bodies.Count}}}","object":"chat.completion","created":1700000000,"model":"accepted-model","choices":[{"index":0,"message":{{{message}}},"finish_reason":"{{{(Bodies.Count == 1 ? "tool_calls" : "stop")}}}"}],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}""";

            return new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
