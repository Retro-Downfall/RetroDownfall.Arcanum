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
    [InlineData("create_agent_task")]
    [InlineData("send_agent_message")]
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

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task");

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

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(function);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        var denied = await context.InvokeBoundAsync(function, new() { ["goal"] = "Remote", ["agent"] = RemoteDiscovery }, default);

        Assert.Contains(ErrorCodes.Guild.BudgetCapabilityUnavailable, Assert.IsType<string>(denied));

        Assert.Equal(0, http.Requests);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));
    }

    [Fact]
    public async Task Qualified_remote_creation_replays_one_asynchronous_child_for_one_logical_call()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(f);

        f.Settings.CurrentValue.Features.Conclave = true;

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task");

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

        AIFunctionArguments arguments = new() { ["goal"] = "Remote goal", ["name"] = "Remote child", ["agent"] = RemoteDiscovery };

        var accepted = Assert.IsType<GuildLocalChildToolResult>(await context.InvokeBoundAsync(function, arguments, default));

        var repeated = Assert.IsType<GuildLocalChildToolResult>(await context.InvokeBoundAsync(function, arguments, default));

        Assert.Equal(accepted.Quest.Id, repeated.Quest.Id);

        Assert.Null(accepted.Quest.ResultReceiptId);

        Assert.Equal(1, http.Requests);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_remote_executions"));

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using var transaction = connection.Connection.BeginTransaction();

        var frozen = await GuildRemoteExecutionStore.ReadInputWithinAsync(connection.Connection, transaction, accepted.Quest.Id, default);

        Assert.True(frozen.IsSuccess, frozen.Error.Message);

        Assert.Equal(A2ADispatchMode.Continuable, frozen.Value.Mode);
    }

    [Fact]
    public async Task Qualified_continue_sending_refuses_a_task_outside_the_current_immediate_parent()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(f);

        f.Settings.CurrentValue.Features.Conclave = true;

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "send_agent_message");

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

        var denied = await context.InvokeBoundAsync(function, new() { ["questId"] = unrelated.Value.Quest.Id.ToString("D"),
            ["kind"] = "continuation", ["contentType"] = "text/plain", ["body"] = "Unrelated answer", ["expectedRevision"] = 1L }, default);

        Assert.Contains(ErrorCodes.Guild.NotFound, Assert.IsType<string>(denied));

        Assert.Equal(0, http.Requests);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_continuations"));
    }

    private static async Task JoinRemoteToolTestWorkAsync(CancellationTokenSource lifetime, Task invocation, Task queuedChild)
    {
        lifetime.Cancel();

        try
        {
            await Task.WhenAll(invocation, queuedChild);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }
}
