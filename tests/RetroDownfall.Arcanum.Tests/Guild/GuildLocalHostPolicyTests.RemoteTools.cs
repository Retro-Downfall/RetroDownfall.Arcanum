using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("dispatch_sending")]
    [InlineData("continue_sending")]
    public async Task Qualified_dispatch_sending_requires_remote_delegation_and_installed_adapter_instead_of_local_delegation(string toolName)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(f);

        f.Settings.CurrentValue.Features.Conclave = true;

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", toolName);

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool], Delegation = [GuildDelegationKind.Remote] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        using RemoteCardHandler http = new();

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue, RemoteResolver(f, http));

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(function);

        Assert.Equal(0, http.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Qualified_dispatch_sending_refuses_finite_grants_before_card_discovery_or_child_issuance(bool usd)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(f);

        f.Settings.CurrentValue.Features.Conclave = true;

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "dispatch_sending");

        GuildBudgetGrant budget = usd ? new(new(GuildBudgetMode.Finite, 1m), new(GuildBudgetMode.Unlimited))
            : new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Finite, 100_000));

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [tool], Delegation = [GuildDelegationKind.Remote], Budget = budget } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        using RemoteCardHandler http = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue(), RemoteResolver(f, http));

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var denied = await registry.AuthorizeAsync(tool, _ => true, default);

        Assert.Equal(ErrorCodes.Guild.BudgetCapabilityUnavailable, denied.Error.Code);

        Assert.Equal(0, http.Requests);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));
    }
    [Theory]
    [InlineData("blocking")]
    [InlineData("continuable")]
    [InlineData("callback")]
    public async Task Qualified_remote_tool_freezes_modes_and_replays_one_child_for_one_logical_call(string mode)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(f);

        f.Settings.CurrentValue.Features.Conclave = true;

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "dispatch_sending");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [tool], Delegation = [GuildDelegationKind.Remote] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        using RemoteCardHandler http = new();

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue, RemoteResolver(f, http));

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(function);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        AIFunctionArguments arguments = new() { ["goal"] = "Remote goal", ["name"] = "Remote child",
            ["agent_url"] = RemoteDiscovery, ["continuable"] = mode == "continuable", ["callback"] = mode == "callback",
            ["skill_id"] = "chosen-skill", ["accepted_output_modes"] = new[] { "text/plain", "application/json" } };

        GuildQuestDto? accepted = null;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(5));

            Task<object?> invoked = context.InvokeBoundAsync(function, arguments, stop.Token).AsTask();

            Task<GuildQuestDto> pending = queue.Pending.Reader.ReadAsync(stop.Token).AsTask();

            Assert.Same(pending, await Task.WhenAny(pending, invoked));

            GuildQuestDto child = await pending;

            Assert.False(invoked.IsCompleted);

            if (accepted is null) accepted = child;
            else Assert.Equal(accepted.Id, child.Id);

            stop.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await invoked);
        }

        Assert.Equal(1, http.Requests);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_remote_executions"));

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using var transaction = connection.Connection.BeginTransaction();

        var frozen = await GuildRemoteExecutionStore.ReadInputWithinAsync(connection.Connection, transaction, accepted!.Id, default);

        Assert.True(frozen.IsSuccess, frozen.Error.Message);

        Assert.Equal(mode == "callback" ? A2ADispatchMode.Callback : mode == "continuable" ? A2ADispatchMode.Continuable : A2ADispatchMode.Blocking,
            frozen.Value.Mode);

        Assert.Equal("chosen-skill", frozen.Value.SkillId);

        Assert.Equal(["text/plain", "application/json"], frozen.Value.AcceptedOutputModes.ToArray());
    }

    [Fact]
    public async Task Qualified_continue_sending_uses_the_existing_immediate_child_and_one_answer_receipt()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(f);

        f.Settings.CurrentValue.Features.Conclave = true;

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant dispatch = new(GuildToolSourceKind.InternalMcp, "arcanum", "dispatch_sending");

        GuildToolGrant continuation = new(GuildToolSourceKind.InternalMcp, "arcanum", "continue_sending");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [dispatch, continuation], Delegation = [GuildDelegationKind.Remote] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        using RemoteCardHandler http = new();

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue, RemoteResolver(f, http));

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var start = await context.AdvertiseAsync(new InertChildTool(dispatch.Name), dispatch, default);

        var answer = await context.AdvertiseAsync(new InertChildTool(continuation.Name), continuation, default);

        Assert.NotNull(start);

        Assert.NotNull(answer);

        GuildQuestDto child;

        using (registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), dispatch))
        {
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(5));

            Task<object?> invoked = context.InvokeBoundAsync(start, new() { ["goal"] = "Remote goal", ["agent_url"] = RemoteDiscovery,
                ["continuable"] = true }, stop.Token).AsTask();

            Task<GuildQuestDto> pending = queue.Pending.Reader.ReadAsync(stop.Token).AsTask();

            Assert.Same(pending, await Task.WhenAny(pending, invoked));

            child = await pending;

            stop.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await invoked);
        }

        GuildCallerContext childCaller = await RunCallerAsync(f, owner, (await f.Store.GetAsync(owner, child.Id, default)).Value);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        var leased = await operations.TryAcquireLeaseAsync(child.OperationId, "remote-tool-wait", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.True(leased.Acquired);

        GuildQuestDriver driver = new(childCaller.Execution!, "remote-tool-wait", leased.Operation.Revision, childCaller);

        Guid offered = Guid.NewGuid();

        var waited = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), offered, default);

        Assert.True(waited.IsSuccess, waited.Error.Message);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), continuation);

        AIFunctionArguments arguments = new() { ["task_id"] = child.Id.ToString("D"), ["agent_url"] = RemoteDiscovery,
            ["message"] = "Selected answer", ["continuable"] = true, ["skill_id"] = "answer-skill",
            ["accepted_output_modes"] = new[] { "text/plain" } };

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(5));

            Task<object?> invoked = context.InvokeBoundAsync(answer, arguments, stop.Token).AsTask();

            Task<GuildQuestDto> pending = queue.Pending.Reader.ReadAsync(stop.Token).AsTask();

            Assert.Same(pending, await Task.WhenAny(pending, invoked));

            Assert.Equal(child.Id, (await pending).Id);

            stop.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await invoked);
        }

        Assert.Equal(1, http.Requests);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_continuations"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_continuations WHERE AuthorityCode=2 AND ProducerQuestId IS NOT NULL"));
    }
    [Fact]
    public async Task Qualified_continue_sending_refuses_a_task_outside_the_current_immediate_parent()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(f);

        f.Settings.CurrentValue.Features.Conclave = true;

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "continue_sending");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [tool], Delegation = [GuildDelegationKind.Remote] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        GuildCallerContext unrelatedOwner = await f.OperatorAsync();

        var unrelated = await f.IssueAsync(unrelatedOwner, f.Request with { IdempotencyKey = Guid.NewGuid() });

        Assert.True(unrelated.IsSuccess, unrelated.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        using RemoteCardHandler http = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue(), RemoteResolver(f, http));

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(function);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        var denied = await context.InvokeBoundAsync(function, new() { ["task_id"] = unrelated.Value.Quest.Id.ToString("D"),
            ["agent_url"] = RemoteDiscovery, ["message"] = "Unrelated answer" }, default);

        Assert.Contains(ErrorCodes.Guild.Forbidden, Assert.IsType<string>(denied));

        Assert.Equal(0, http.Requests);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_continuations"));
    }
}
