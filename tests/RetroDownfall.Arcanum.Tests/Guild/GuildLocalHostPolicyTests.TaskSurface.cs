using Microsoft.Extensions.AI;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public void Task_surface_has_one_creation_route_and_scoped_management_routes()
    {
        string[] names = GuildLocalToolRegistry.SupportedTools.Select(tool => tool.Name).ToArray();

        Assert.Contains("create_agent_task", names);

        Assert.Contains("send_agent_message", names);

        Assert.Contains("get_agent_task", names);

        Assert.Contains("cancel_agent_task", names);

        Assert.Contains("list_agents", names);

        Assert.DoesNotContain("cast_sending", names);

        Assert.DoesNotContain("dispatch_sending", names);

        Assert.DoesNotContain("continue_sending", names);
    }

    [Fact]
    public void Guild_task_registry_includes_the_shared_memory_search_adapter()
    {
        Assert.Contains(GuildLocalToolRegistry.SupportedTools, tool => tool.Name == "search_memory");
    }

    [Fact]
    public async Task Create_task_admits_local_child_without_waiting_when_remote_is_disabled()
    {
        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue);

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(function);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        object? result = await context.InvokeBoundAsync(function, new() { ["goal"] = "Compute child contribution" }, deadline.Token);

        Assert.NotNull(result);

        Assert.IsNotType<string>(result);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.True(queue.Pending.Reader.TryRead(out GuildQuestDto? child));

        Assert.Equal(parent.Value.Quest.Id, child!.ParentQuestId);

        Assert.Null(child.ResultReceiptId);
    }
    [Theory]
    [InlineData("get_agent_task")]
    [InlineData("cancel_agent_task")]
    [InlineData("list_agents")]
    [InlineData("send_agent_message")]
    public async Task Task_management_tools_are_available_without_delegation_grants(string name)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", name);

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool], Delegation = [] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue());

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        Assert.NotNull(await context.AdvertiseAsync(new InertChildTool(name), tool, default));
    }

    [Fact]
    public async Task Task_tools_read_message_list_cancel_and_replay_only_the_current_subtree()
    {
        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant[] tools = [.. GuildLocalToolRegistry.SupportedTools.Where(item => item.SourceKind == GuildToolSourceKind.InternalMcp)];

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [.. tools] } });

        var foreign = await f.IssueAsync(owner, f.Request with { IdempotencyKey = Guid.NewGuid() });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        Assert.True(foreign.IsSuccess, foreign.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue());

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        async Task<object?> Invoke(string name, AIFunctionArguments args, Guid? logical = null)
        {
            GuildToolGrant tool = tools.Single(item => item.Name == name);

            var function = await context.AdvertiseAsync(new InertChildTool(name), tool, default);

            Assert.NotNull(function);

            using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), logical ?? Guid.NewGuid(), tool);

            return await context.InvokeBoundAsync(function, args, default);
        }

        var created = Assert.IsType<GuildLocalChildToolResult>(await Invoke("create_agent_task", new() { ["goal"] = "Child" }));

        Assert.Equal(created.Quest.Id, created.Projection!.Value.GetProperty("id").GetGuid());

        var read = Assert.IsType<GuildLocalChildToolResult>(await Invoke("get_agent_task", new() { ["questId"] = created.Quest.Id.ToString() }));

        Assert.Equal(created.Quest.Id, read.Projection!.Value.GetProperty("id").GetGuid());

        string denied = Assert.IsType<string>(await Invoke("get_agent_task", new() { ["questId"] = foreign.Value.Quest.Id.ToString() }));

        string missing = Assert.IsType<string>(await Invoke("get_agent_task", new() { ["questId"] = Guid.NewGuid().ToString() }));

        Assert.Equal(missing, denied);

        Guid messageId = Guid.NewGuid();

        AIFunctionArguments message = new() { ["questId"] = created.Quest.Id.ToString(), ["kind"] = "message",
            ["contentType"] = "text/plain", ["body"] = "Progress input", ["idempotencyKey"] = messageId.ToString() };

        object? sending = await Invoke("send_agent_message", message);

        Assert.False(sending is string, sending?.ToString());

        var sent = Assert.IsType<GuildLocalChildToolResult>(sending);

        var replay = Assert.IsType<GuildLocalChildToolResult>(await Invoke("send_agent_message", message));

        Assert.Equal(sent.Projection!.Value.GetProperty("messageId").GetGuid(), replay.Projection!.Value.GetProperty("messageId").GetGuid());

        var agents = Assert.IsType<GuildLocalChildToolResult>(await Invoke("list_agents", new() { ["pageSize"] = 1 }));

        Assert.Single(agents.Projection!.Value.GetProperty("items").EnumerateArray());

        Assert.Equal("livePerPage", agents.Projection.Value.GetProperty("stateConsistency").GetString());

        Guid cancellation = Guid.NewGuid();

        AIFunctionArguments cancel = new() { ["questId"] = created.Quest.Id.ToString(),
            ["expectedRevision"] = created.Quest.Revision, ["idempotencyKey"] = cancellation.ToString() };

        var canceled = Assert.IsType<GuildLocalChildToolResult>(await Invoke("cancel_agent_task", cancel));

        var cancelReplay = Assert.IsType<GuildLocalChildToolResult>(await Invoke("cancel_agent_task", cancel));

        Assert.True(canceled.Projection!.Value.GetProperty("cancellationRequested").GetBoolean());

        Assert.True(cancelReplay.Projection!.Value.GetProperty("receipt").GetProperty("replayed").GetBoolean());
    }

    [Theory]
    [InlineData("eligibility")]
    [InlineData("revoked")]
    [InlineData("closed-frame")]
    [InlineData("foreign-target")]
    public async Task Task_cancellation_cannot_adopt_a_prior_receipt_after_its_model_authority_is_lost(string boundary)
    {
        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant create = new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task");

        GuildToolGrant cancel = new(GuildToolSourceKind.InternalMcp, "arcanum", "cancel_agent_task");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [create, cancel] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var foreign = await f.IssueAsync(owner, f.Request with { IdempotencyKey = Guid.NewGuid() });

        Assert.True(foreign.IsSuccess, foreign.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue());

        CapturingTaskDispatcher dispatcher = new(coordinator);

        bool eligible = true;

        using GuildLocalToolRegistry registry = new(execution, input.Value, dispatcher);

        var context = f.Store.CreateToolContext(execution, registry, _ => eligible);

        var createFunction = await context.AdvertiseAsync(new InertChildTool(create.Name), create, default);

        Assert.NotNull(createFunction);

        GuildLocalChildToolResult child;

        using (registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), create))
            child = Assert.IsType<GuildLocalChildToolResult>(await context.InvokeBoundAsync(createFunction, new() { ["goal"] = "Child" }, default));

        var cancelFunction = await context.AdvertiseAsync(new InertChildTool(cancel.Name), cancel, default);

        Assert.NotNull(cancelFunction);

        using IDisposable invocation = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), cancel);

        GuildCancelQuestRequest request = new(Guid.NewGuid(), child.Quest.Revision, GuildCancellationReason.OperatorRequested);

        AIFunctionArguments arguments = new() { ["questId"] = child.Quest.Id.ToString(),
            ["expectedRevision"] = request.ExpectedRevision, ["idempotencyKey"] = request.IdempotencyKey.ToString() };

        var accepted = Assert.IsType<GuildLocalChildToolResult>(await context.InvokeBoundAsync(cancelFunction, arguments, default));

        GuildChildToolAdmission admission = Assert.IsType<GuildChildToolAdmission>(dispatcher.LastAdmission);

        var replay = Assert.IsType<GuildLocalChildToolResult>(await context.InvokeBoundAsync(cancelFunction, arguments, default));

        Assert.True(replay.Projection!.Value.GetProperty("receipt").GetProperty("replayed").GetBoolean());

        Guid target = child.Quest.Id;

        if (boundary == "foreign-target")
        {
            target = foreign.Value.Quest.Id;

            request = new(Guid.NewGuid(), foreign.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

            var native = await f.Store.RequestCancellationAsync(owner,
                new(request.IdempotencyKey, GuildQuestIntent.Compute(target, request).Value), target, request, default);

            Assert.True(native.IsSuccess, native.Error.Message);
        }
        else if (boundary == "eligibility") eligible = false;
        else if (boundary == "closed-frame") invocation.Dispose();
        else if (boundary == "revoked")
        {
            var current = await f.Store.GetAsync(owner, parent.Value.Quest.Id, default);

            Assert.True(current.IsSuccess, current.Error.Message);

            Guid key = Guid.NewGuid();

            var revoked = await f.Authority.RevokeAsync(owner, new(key, new string('A', 64)), current.Value.Quest.SignetId,
                new(key, current.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

            Assert.True(revoked.IsSuccess, revoked.Error.Message);
        }

        long receipts = await f.ScalarAsync("SELECT count(*) FROM guild_quest_cancellations");

        var refused = await f.Store.RequestTaskCancellationAsync(admission,
            new(request.IdempotencyKey, GuildQuestIntent.Compute(target, request).Value), target, request, default);

        Assert.True(refused.IsFailure);

        Assert.Equal(receipts, await f.ScalarAsync("SELECT count(*) FROM guild_quest_cancellations"));

        Assert.True(accepted.Projection!.Value.GetProperty("cancellationRequested").GetBoolean());
    }

    private sealed class CapturingTaskDispatcher(IGuildLocalChildToolDispatcher inner) : IGuildLocalChildToolDispatcher
    {
        internal GuildChildToolAdmission? LastAdmission { get; private set; }

        public Task<Result<GuildLocalChildToolResult>> DispatchAsync(GuildLocalChildToolCall call, CancellationToken ct)
        {
            LastAdmission = call.Admission;

            return inner.DispatchAsync(call, ct);
        }
    }

    [Fact]
    public async Task Create_task_remote_destination_returns_committed_projection_without_waiting_for_peer_work()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(f);

        f.Settings.CurrentValue.Features.Conclave = true;

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [tool], Delegation = [GuildDelegationKind.Remote] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        using RemoteCardHandler http = new();

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue, RemoteResolver(f, http));

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(function);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        var created = Assert.IsType<GuildLocalChildToolResult>(await context.InvokeBoundAsync(function,
            new() { ["goal"] = "Remote work", ["agent"] = RemoteDiscovery }, deadline.Token));

        Assert.Equal(parent.Value.Quest.Id, created.Quest.ParentQuestId);

        Assert.Null(created.Quest.ResultReceiptId);

        Assert.True(queue.Pending.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Bound_task_creation_preserves_explicit_local_and_remote_capability_matrix(bool local, bool remote)
    {
        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        ConfigureRemote(f);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [tool], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Remote] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        f.Settings.CurrentValue.Features.Conclave = local;

        f.Settings.CurrentValue.Features.A2AClient = remote;

        if (!local) f.Settings.CurrentValue.Providers = [];

        using RemoteCardHandler http = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue(), RemoteResolver(f, http));

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => f.Settings.CurrentValue.Features.Guild);

        var template = new InertChildTool(tool.Name);

        var advertised = await context.AdvertiseAsync(template, tool, default);

        Assert.Equal(local || remote, advertised is not null);

        var function = advertised ?? context.Bind(template, tool);

        int accepted = 0;

        foreach (bool selectedRemote in new[] { false, true })
        {
            using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

            AIFunctionArguments arguments = new() { ["goal"] = "Bound capability matrix" };

            if (selectedRemote) arguments["agent"] = RemoteDiscovery;

            object? result = await context.InvokeBoundAsync(function, arguments, default);

            if (selectedRemote ? remote : local)
            {
                Assert.False(result is string, result?.ToString());

                var child = Assert.IsType<GuildLocalChildToolResult>(result);

                Assert.Equal(parent.Value.Quest.Id, child.Quest.ParentQuestId);

                Assert.Equal(child.Quest.Id, child.Projection!.Value.GetProperty("id").GetGuid());

                Assert.Equal(child.Quest.OperationId, child.Projection.Value.GetProperty("operationId").GetGuid());

                Assert.Null(child.Quest.ResultReceiptId);

                accepted++;
            }
            else
            {
                string refusal = Assert.IsType<string>(result);

                Assert.True(refusal.StartsWith(ErrorCodes.Guild.Unavailable + ":", StringComparison.Ordinal)
                    || refusal.StartsWith(ErrorCodes.Guild.ToolBoundaryUnavailable + ":", StringComparison.Ordinal), refusal);
            }
        }

        Assert.Equal(1L + accepted, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(remote ? 1 : 0, http.Requests);
    }

}
