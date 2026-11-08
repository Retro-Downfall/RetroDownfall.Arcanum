using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Qualified_child_tool_commits_one_real_child_with_exact_source_and_mode(bool cast)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = cast ? new(GuildToolSourceKind.InternalMcp, "arcanum", "cast_sending")
            : new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task");

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, root.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        ToolDispatcher dispatcher = new(f);

        using GuildLocalToolRegistry registry = new(execution, input.Value, dispatcher);

        GuildToolInvocationContext context = f.Store.CreateToolContext(execution, registry, _ => true);

        InertChildTool template = new(tool.Name);

        AIFunction? bound = await context.AdvertiseAsync(template, tool, default);

        Assert.NotNull(bound);

        Guid step = Guid.NewGuid();

        Guid call = Guid.NewGuid();

        using IDisposable invocation = registry.BeginInvocation(step, call, tool);

        object? result = await context.InvokeBoundAsync(bound, new AIFunctionArguments { ["goal"] = "Compute child contribution" }, default);

        GuildLocalChildToolResult accepted = Assert.IsType<GuildLocalChildToolResult>(result);

        Assert.Equal(root.Value.Quest.Id, accepted.Quest.ParentQuestId);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind='subagent'"));

        object? replay = await context.InvokeBoundAsync(bound, new AIFunctionArguments { ["goal"] = "Compute child contribution" }, default);

        Assert.Equal(accepted.Quest.Id, Assert.IsType<GuildLocalChildToolResult>(replay).Quest.Id);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(0, template.Effects);
    }

    [Fact]
    public async Task Qualified_child_tool_retained_callback_refuses_input_wait_with_current_driver()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task");

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, root.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        ToolDispatcher dispatcher = new(f);

        using GuildLocalToolRegistry registry = new(execution, input.Value, dispatcher);

        GuildToolInvocationContext context = f.Store.CreateToolContext(execution, registry, _ => true);

        InertChildTool template = new(tool.Name);

        AIFunction? bound = await context.AdvertiseAsync(template, tool, default);

        Assert.NotNull(bound);

        var waiting = await f.Store.WaitForInputAsync(execution.Driver, Guid.NewGuid(), Guid.NewGuid(), default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        Assert.Equal(QuestState.InputRequired, waiting.Value.Quest.State);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        var operation = await operations.GetAsync(root.Value.Quest.OperationId);

        Assert.True(execution.TryRefreshDriver(execution.Driver with { OperationRevision = operation!.Revision }));

        using IDisposable invocation = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        object? result = await context.InvokeBoundAsync(bound, new AIFunctionArguments { ["goal"] = "Wait must forbid this child" }, default);

        Assert.IsNotType<GuildLocalChildToolResult>(result);

        Assert.Equal(0, dispatcher.Calls);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(0, template.Effects);
    }

    [Theory]
    [InlineData("missing-scope", 0)]
    [InlineData("closed-scope", 0)]
    [InlineData("invalid-inner-scope", 0)]
    [InlineData("feature-off", 0)]
    [InlineData("eligibility", 0)]
    [InlineData("closed-registry", 0)]
    [InlineData("opaque-json", 0)]
    [InlineData("deep-json", 0)]
    [InlineData("readiness-lost", 0)]
    [InlineData("uncommitted-receipt", 1)]
    [InlineData("wrong-mode", 1)]
    [InlineData("forged-admission", 1)]
    [InlineData("disable-at-commit", 1)]
    [InlineData("readiness-at-commit", 1)]
    public async Task Qualified_child_tool_rechecks_retained_host_boundaries(string boundary, int expectedCalls)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task");

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, root.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        ToolDispatcher dispatcher = new(f)
        {
            SkipIssue = boundary == "uncommitted-receipt",
            WrongMode = boundary == "wrong-mode",
            ForgeAdmission = boundary == "forged-admission",
            BeforeIssue = boundary == "disable-at-commit" ? () => f.Settings.CurrentValue.Features.Guild = false
                : boundary == "readiness-at-commit" ? () => f.Readiness.Available = false : null,
        };

        bool eligible = true;

        using GuildLocalToolRegistry registry = new(execution, input.Value, dispatcher);

        GuildToolInvocationContext context = f.Store.CreateToolContext(execution, registry, _ => eligible);

        InertChildTool template = new(tool.Name);

        AIFunction? bound = await context.AdvertiseAsync(template, tool, default);

        Assert.NotNull(bound);

        using IDisposable? invocation = boundary == "missing-scope" ? null : registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        if (boundary == "closed-scope") invocation!.Dispose();

        using IDisposable? invalid = boundary == "invalid-inner-scope" ? registry.BeginInvocation(Guid.Empty, Guid.NewGuid(), tool) : null;

        if (boundary == "feature-off") f.Settings.CurrentValue.Features.Guild = false;

        if (boundary == "eligibility") eligible = false;

        if (boundary == "closed-registry") registry.Dispose();

        if (boundary == "readiness-lost") f.Readiness.Available = false;

        AIFunctionArguments arguments = new() { ["goal"] = "Refuse retained callback" };

        if (boundary == "opaque-json") arguments["opaque"] = new object();

        if (boundary == "deep-json")
        {
            object? nested = "leaf";

            for (int depth = 0; depth < 70; depth++) nested = new object?[] { nested };

            arguments["nested"] = nested;
        }

        object? result = await context.InvokeBoundAsync(bound, arguments, default);

        Assert.IsNotType<GuildLocalChildToolResult>(result);

        Assert.Equal(expectedCalls, dispatcher.Calls);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));

        Assert.Equal(0, template.Effects);
    }

    private static async Task<GuildLocalInferenceExecution> ToolExecutionAsync(Fixture f, GuildCallerContext owner, GuildQuestStored root)
    {
        GuildCallerContext run = await RunCallerAsync(f, owner, root);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var leased = await operations.TryAcquireLeaseAsync(root.Quest.OperationId, "qualified-tool", now, now.AddMinutes(5));

        Assert.True(leased.Acquired);

        GuildQuestDriver driver = new(run.Execution!, "qualified-tool", leased.Operation.Revision, run);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), new string('B', 64), run.Execution!.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

        return new(f.Store, driver, dispatch.Value);
    }

    private sealed class ToolDispatcher(Fixture fixture) : IGuildLocalChildToolDispatcher
    {
        internal int Calls;

        internal bool SkipIssue;

        internal bool WrongMode;

        internal bool ForgeAdmission;

        internal Action? BeforeIssue;

        public async Task<Result<GuildLocalChildToolResult>> DispatchAsync(GuildLocalChildToolCall call, CancellationToken ct)
        {
            Calls++;

            if (SkipIssue) return new GuildLocalChildToolResult(call.ParentQuest);

            BeforeIssue?.Invoke();

            fixture.ToolChildMode = WrongMode ? GuildLocalExecutionMode.Apprentice : call.Kind == GuildLocalChildToolKind.DelegateTask ? GuildLocalExecutionMode.Sterile : GuildLocalExecutionMode.Apprentice;

            Guid key = call.Admission.LogicalCallId;

            GuildIssueChildRequest request = new(key, call.ParentQuest.Revision, null, "Child", call.Arguments.GetProperty("goal").GetString()!, new());

            GuildChildToolAdmission admission = ForgeAdmission
                ? new(call.Admission.Registry, call.Admission.Frame, call.Admission.Arguments, call.Admission.ArgumentsDigest, call.Admission.Eligibility)
                : call.Admission;

            var accepted = await fixture.Store.IssueToolChildAsync(admission,
                new(fixture.Generation, call.ParentQuest.Id, null, call.Admission.IssuingStepId, key), request, ct);

            return accepted.IsFailure ? accepted.Error : new GuildLocalChildToolResult(accepted.Value.Quest);
        }
    }

    private sealed class InertChildTool(string name) : AIFunction
    {
        internal int Effects;

        public override string Name => name;

        public override string Description => "Host schema template";

        public override JsonElement JsonSchema => JsonDocument.Parse("""{"type":"object","properties":{"goal":{"type":"string"}},"required":["goal"]}""").RootElement.Clone();

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken ct)
        {
            Effects++;

            return ValueTask.FromResult<object?>("Unqualified original ran");
        }
    }
}
