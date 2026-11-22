using System.Text.Json;
using System.Text.Json.Nodes;
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
    [Fact]
    public async Task Native_root_issue_accepts_a_grant_with_only_nonfinancial_authority()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await f.OperatorAsync();

        JsonNode wire = JsonSerializer.SerializeToNode(f.Request, GuildJsonContext.Default.GuildIssueRootRequest)!;

        Assert.True(wire["grant"]!.AsObject().Remove("budget"));

        GuildIssueRootRequest request = Assert.IsType<GuildIssueRootRequest>(wire.Deserialize(GuildJsonContext.Default.GuildIssueRootRequest));

        var accepted = await f.IssueAsync(owner, request);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        var replay = await f.IssueAsync(owner, request);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(accepted.Value.Quest.Id, replay.Value.Quest.Id);

        Assert.True(replay.Value.Quest.Receipt!.Replayed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_prompt_only_delegation_reaches_real_coordinator_and_returns_child_receipt(bool historicalFiniteGrant)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task");

        GuildGrant grant = f.Request.Grant with { Tools = [tool] };

        if (historicalFiniteGrant)
        {
            grant = grant with { Budget = new(new(GuildBudgetMode.Finite, 0), new(GuildBudgetMode.Finite, 0)) };
        }

        var root = await f.IssueAsync(owner, f.Request with { Grant = grant });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, root.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        f.ToolChildMode = GuildLocalExecutionMode.Sterile;

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        ArcanumSettings settings = new()
        {
            Features = new() { Guild = true }, DefaultModel = "accepted-model",
            Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://example.test/v1", Models = ["accepted-model"] }],
        };

        BudgetReservationService daily = new(db);

        using ChildToolTransport transport = new() { PromptOnly = true };

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings));

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue);

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients, coordinator);

            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

            var invocation = runtime.Runtime.ExecuteTurnAsync(execution, input.Value,
                new("Delegate the task.", DisableAllTools: false), deadline.Token);

            Task<GuildQuestDto> scheduled = queue.Pending.Reader.ReadAsync(deadline.Token).AsTask();

            Assert.Same(scheduled, await Task.WhenAny(scheduled, invocation));

            GuildQuestDto child = await scheduled;

            Assert.Equal(GuildBudgetMode.Unlimited, child.ResolvedGrant.Budget.Usd.Mode);

            Assert.Equal(GuildBudgetMode.Unlimited, child.ResolvedGrant.Budget.Tokens.Mode);

            Assert.Empty(child.ResolvedGrant.Tools);

            Assert.Empty(child.ResolvedGrant.Workspaces);

            Assert.Empty(child.ResolvedGrant.Memory);

            Assert.Empty(child.ResolvedGrant.Delegation);

            var recovery = await f.Policy.CreateRecoveryAsync(owner, child.Id, child.OperationId, child.SignetRevision, deadline.Token);

            Assert.True(recovery.IsSuccess, recovery.Error.Message);

            var handle = await f.Authority.RebindAsync(recovery.Value, deadline.Token);

            Assert.True(handle.IsSuccess, handle.Error.Message);

            var pending = await f.Store.ReadLocalPendingAsync(owner, child.Id, deadline.Token);

            Assert.True(pending.IsSuccess, pending.Error.Message);

            GuildLocalPort port = new(f.Store, f.Policy, f.Authority, new LongRunningOperationStore(db, f.Connections),
                new DeterministicLocalEngine(), new UnusedInference(), queue, new(), TimeProvider.System);

            var completed = await port.DispatchAsync(handle.Value, pending.Value.Request!, deadline.Token);

            Assert.True(completed.IsSuccess, completed.Error.Message);

            var result = await invocation;

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal("final parent answer", result.Value.Text);

            Assert.Equal(2, transport.Bodies.Count);

            using JsonDocument dispatched = JsonDocument.Parse(transport.Bodies[0]);

            JsonElement function = Assert.Single(dispatched.RootElement.GetProperty("tools").EnumerateArray()).GetProperty("function");

            Assert.Equal("delegate_task", function.GetProperty("name").GetString());

            JsonElement advertisedParameters = function.GetProperty("parameters").GetProperty("properties");

            Assert.False(advertisedParameters.TryGetProperty("max_tokens", out _));

            Assert.False(advertisedParameters.TryGetProperty("max_cost_usd", out _));

            Assert.Contains("Completed without an external effect.", transport.Bodies[1], StringComparison.Ordinal);

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_result_applications"));

            await using var accounting = connection.Connection.BeginTransaction();

            var settled = await new GuildBudgetLedger().ReadWithinAsync(connection.Connection, accounting,
                root.Value.AllocationAccountId, deadline.Token);

            Assert.True(settled.IsSuccess, settled.Error.Message);

            Assert.False(settled.Value.PhysicalBlocked);

            Assert.False(settled.Value.UsdBlocked);

            Assert.False(settled.Value.TokensBlocked);
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }
}
