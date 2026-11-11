using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("create_cohort")]
    [InlineData("admit_cohort_member")]
    [InlineData("close_cohort")]
    [InlineData("remove_cohort_member")]
    public void Cohort_tools_survive_the_frozen_provider_wire_roundtrip(string name)
    {
        GuildProviderPayload payload = new([new ChatMessage(ChatRole.User, "Collect member results")], [new InertChildTool(name)]);

        Assert.Equal(name, Assert.Single(payload.CreateFunctions()).Name);
    }

    [Fact]
    public void Cohort_message_schema_includes_explicit_exclusive_address()
    {
        JsonElement schema = ArcanumInternalToolServer.BuildTaskToolSchema("send_agent_message");

        Assert.True(schema.GetProperty("properties").TryGetProperty("cohortId", out _));

        Assert.DoesNotContain(schema.GetProperty("required").EnumerateArray(), item => item.GetString() == "questId");

        Assert.True(schema.TryGetProperty("allOf", out _));
    }

    [Theory]
    [InlineData("create_cohort")]
    [InlineData("admit_cohort_member")]
    [InlineData("close_cohort")]
    [InlineData("remove_cohort_member")]
    public void Cohort_tool_schema_excludes_caller_identity_and_reserves_exact_name(string name)
    {
        JsonElement schema = ArcanumInternalToolServer.BuildTaskToolSchema(name);

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

        Assert.False(schema.GetProperty("properties").TryGetProperty("issuerQuestId", out _));

        Assert.False(schema.GetProperty("properties").TryGetProperty("memberAgentId", out _));

        Assert.Contains(name, ArcanumInternalToolServer.RegisteredToolNames);

        Assert.Contains(GuildLocalToolRegistry.SupportedTools, tool => tool.Name == name);
    }

    [Fact]
    public async Task Cohort_tools_commit_run_bound_create_admit_remove_close_and_exact_replay()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = RetroDownfall.Arcanum.Core.Configuration.AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant[] tools = CohortTools();

        GuildToolGrant message = new(GuildToolSourceKind.InternalMcp, "arcanum", "send_agent_message");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [.. tools, message], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store, new(), f.Policy, TimeProvider.System);

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue, cohorts: cohorts);

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        async Task<GuildLocalChildToolResult> Invoke(string name, AIFunctionArguments arguments, bool replay = false)
        {
            GuildToolGrant tool = tools.Single(value => value.Name == name);

            var bound = await context.AdvertiseAsync(new InertChildTool(name), tool, default);

            Assert.NotNull(bound);

            using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

            object? raw = await context.InvokeBoundAsync(bound, arguments, default);

            Assert.False(raw is string, raw?.ToString());

            GuildLocalChildToolResult accepted = Assert.IsType<GuildLocalChildToolResult>(raw);

            if (replay)
            {
                object? repeated = await context.InvokeBoundAsync(bound, arguments, default);

                Assert.False(repeated is string, repeated?.ToString());

                Assert.Equal(accepted.Projection!.Value.GetProperty("id").GetGuid(),
                    Assert.IsType<GuildLocalChildToolResult>(repeated).Projection!.Value.GetProperty("id").GetGuid());
            }

            return accepted;
        }

        var created = await Invoke("create_cohort", new() { ["name"] = "Panel", ["policy"] = "all", ["resultType"] = "text/plain" }, true);

        Guid cohortId = created.Projection!.Value.GetProperty("id").GetGuid();

        Assert.Equal(parent.Value.Quest.Id, created.Quest.Id);

        Assert.Equal(parent.Value.Quest.Id, created.Projection.Value.GetProperty("issuerQuestId").GetGuid());

        var first = await Invoke("admit_cohort_member", new() { ["cohortId"] = cohortId.ToString(), ["expectedRevision"] = 1L, ["goal"] = "First vote" }, true);

        Assert.Equal(parent.Value.Quest.Id, first.Quest.ParentQuestId);

        var afterFirst = await cohorts.GetAsync(execution.Driver.Caller!, cohortId, default);

        var second = await Invoke("admit_cohort_member", new() { ["cohortId"] = cohortId.ToString(),
            ["expectedRevision"] = afterFirst.Value.Revision, ["goal"] = "Second vote" });

        var primary = await f.Store.GetAsync(owner, first.Quest.Id, default);

        Assert.True(primary.IsSuccess, primary.Error.Message);

        var memberExecution = await ToolExecutionAsync(f, owner, primary.Value);

        var memberInput = await f.Store.ReadLocalInputAsync(memberExecution.Driver, default);

        using GuildLocalToolRegistry memberRegistry = new(memberExecution, memberInput.Value, coordinator);

        var memberContext = f.Store.CreateToolContext(memberExecution, memberRegistry, _ => true);

        var messageFunction = await memberContext.AdvertiseAsync(new InertChildTool(message.Name), message, default);

        Assert.NotNull(messageFunction);

        using (memberRegistry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), message))
        {
            object? sent = await memberContext.InvokeBoundAsync(messageFunction, new() { ["cohortId"] = cohortId.ToString(),
                ["kind"] = "message", ["contentType"] = "text/plain", ["body"] = "Peer progress" }, default);

            Assert.False(sent is string, sent?.ToString());

            Assert.IsType<GuildLocalChildToolResult>(sent);

            GuildChildToolAdmission sentAdmission = memberRegistry.CaptureInvocation(message)!.Admission!;

            using IDisposable invalidMessageFrame = memberRegistry.BeginInvocation(Guid.Empty, Guid.NewGuid(), message);

            var nestedRefusal = await f.Store.PublishCohortTaskMessageAsync(sentAdmission, cohortId, sentAdmission.LogicalCallId,
                "text/plain", "Peer progress", default);

            Assert.True(nestedRefusal.IsFailure);
        }

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_cohort_audience"));

        var afterSecond = await cohorts.GetAsync(execution.Driver.Caller!, cohortId, default);

        await Invoke("remove_cohort_member", new() { ["cohortId"] = cohortId.ToString(),
            ["memberId"] = second.Projection!.Value.GetProperty("id").GetGuid().ToString(), ["expectedRevision"] = afterSecond.Value.Revision });

        var afterRemoval = await cohorts.GetAsync(execution.Driver.Caller!, cohortId, default);

        var closed = await Invoke("close_cohort", new() { ["cohortId"] = cohortId.ToString(), ["expectedRevision"] = afterRemoval.Value.Revision }, true);

        Assert.Equal("closed", closed.Projection!.Value.GetProperty("state").GetString());

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohorts"));

        Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_members"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_members WHERE StateCode=1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cohort_tools_require_composed_store_and_current_cohort_delegation(bool composed)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await f.OperatorAsync();

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [.. CohortTools()] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        GuildCohortStore? cohorts = composed
            ? new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store, new(), f.Policy, TimeProvider.System) : null;

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue(), cohorts: cohorts);

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        foreach (GuildToolGrant tool in CohortTools())
        {
            Assert.Null(await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default));
        }

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohorts"));
    }

    private static GuildToolGrant[] CohortTools() =>
        [new(GuildToolSourceKind.InternalMcp, "arcanum", "create_cohort"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "admit_cohort_member"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "close_cohort"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "remove_cohort_member")];

    [Theory]
    [InlineData("closed-frame")]
    [InlineData("nested-invalid-frame")]
    [InlineData("feature-off")]
    [InlineData("changed-request")]
    [InlineData("projection-substitution")]
    public async Task Cohort_tool_transactions_and_output_recheck_exact_retained_host_admission(string boundary)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var owner = await f.OperatorAsync();

        GuildToolGrant tool = CohortTools()[0];

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [tool], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store, new(), f.Policy, TimeProvider.System);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue(), cohorts: cohorts);

        CohortCaptureDispatcher dispatcher = new(coordinator) { TamperProjection = boundary == "projection-substitution" };

        using GuildLocalToolRegistry registry = new(execution, input.Value, dispatcher);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var bound = await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(bound);

        Guid key = Guid.NewGuid();

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), key, tool);

        object? result = await context.InvokeBoundAsync(bound, new() { ["name"] = "Panel", ["policy"] = "all", ["resultType"] = "text/plain" }, default);

        if (boundary == "projection-substitution")
        {
            Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, Assert.IsType<string>(result));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohorts"));

            return;
        }

        Assert.IsType<GuildLocalChildToolResult>(result);

        GuildChildToolAdmission admission = Assert.IsType<GuildChildToolAdmission>(dispatcher.LastAdmission);

        if (boundary == "closed-frame") frame.Dispose();

        using IDisposable? invalidFrame = boundary == "nested-invalid-frame"
            ? registry.BeginInvocation(Guid.Empty, Guid.NewGuid(), tool) : null;

        if (boundary == "feature-off") f.Settings.CurrentValue.Features.Guild = false;

        CreateCohortRequest request = new(boundary == "changed-request" ? Guid.NewGuid() : key,
            parent.Value.Quest.Id, "Panel", new(GuildConvergenceKind.All, null), new("text/plain"), null);

        var refused = await cohorts.CreateAsync(execution.Driver.Caller!, request, default, admission);

        Assert.True(refused.IsFailure);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohorts"));
    }

    private sealed class CohortCaptureDispatcher(IGuildLocalChildToolDispatcher inner) : IGuildLocalChildToolDispatcher
    {
        internal GuildChildToolAdmission? LastAdmission;

        internal bool TamperProjection;

        public bool SupportsCohorts => inner.SupportsCohorts;

        public async Task<Result<GuildLocalChildToolResult>> DispatchAsync(GuildLocalChildToolCall call, CancellationToken ct)
        {
            LastAdmission = call.Admission;

            var result = await inner.DispatchAsync(call, ct);

            if (result.IsFailure || !TamperProjection) return result;

            using JsonDocument forged = JsonDocument.Parse("{\"id\":\"00000000-0000-0000-0000-000000000001\"}");

            return result.Value with { Projection = forged.RootElement.Clone() };
        }
    }

    [Fact]
    public async Task Cohort_remote_admission_uses_its_own_exact_tool_grant_for_discovery_and_issue()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(f);

        f.Settings.CurrentValue.Features.Conclave = true;

        var owner = await f.OperatorAsync();

        GuildToolGrant create = CohortTools()[0];

        GuildToolGrant admit = CohortTools()[1];

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [create, admit], Delegation = [GuildDelegationKind.Cohort, GuildDelegationKind.Remote] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store, new(), f.Policy, TimeProvider.System);

        using RemoteCardHandler http = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue(), RemoteResolver(f, http), cohorts: cohorts);

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var createFunction = await context.AdvertiseAsync(new InertChildTool(create.Name), create, default);

        Assert.NotNull(createFunction);

        GuildLocalChildToolResult created;

        using (registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), create))
            created = Assert.IsType<GuildLocalChildToolResult>(await context.InvokeBoundAsync(createFunction,
                new() { ["name"] = "Remote panel", ["policy"] = "first", ["resultType"] = "text/plain" }, default));

        var admitFunction = await context.AdvertiseAsync(new InertChildTool(admit.Name), admit, default);

        Assert.NotNull(admitFunction);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), admit);

        object? result = await context.InvokeBoundAsync(admitFunction, new() { ["cohortId"] = created.Projection!.Value.GetProperty("id").GetGuid().ToString(),
            ["expectedRevision"] = 1L, ["goal"] = "Remote vote", ["agent"] = RemoteDiscovery }, default);

        Assert.Equal(1, http.Requests);

        Assert.False(result is string, result?.ToString());

        Assert.Equal(parent.Value.Quest.Id, Assert.IsType<GuildLocalChildToolResult>(result).Quest.ParentQuestId);
    }
}
