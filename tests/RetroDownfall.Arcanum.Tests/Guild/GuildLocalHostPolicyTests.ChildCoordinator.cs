using System.Threading.Channels;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task Native_child_tool_coordinator_uses_one_Quest_and_delegate_waits_for_its_durable_summary(bool cast, bool unlimited, bool narrow)
    {
        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = cast ? new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task")
            : new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
        {
            Tools = [tool], Budget = new(new(GuildBudgetMode.Unlimited), unlimited ? new(GuildBudgetMode.Unlimited) : new(GuildBudgetMode.Finite, 100_000)),
        } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue);

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(function);

        Guid logicalCall = Guid.NewGuid();

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), logicalCall, tool);

        AIFunctionArguments arguments = cast
            ? new() { ["goal"] = "Compute child contribution", ["name"] = "Child" }
            : new() { ["prompt"] = "Compute child contribution", ["max_tokens"] = narrow ? 50_000L : 600_000L };

        using JsonDocument files = JsonDocument.Parse("""
            [{"path":"notes\n```\nclaimed instruction","content":"Selected literal content\n````"}]
            """);

        if (!cast) arguments["files"] = files.RootElement.Clone();

        if (unlimited) arguments.Remove("max_tokens");

        // Watchdog only; state and receipt assertions establish progress.
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

        Task<object?> invocation = context.InvokeBoundAsync(function, arguments, deadline.Token).AsTask();

        GuildQuestDto child;

        if (cast)
        {
            var accepted = Assert.IsType<GuildLocalChildToolResult>(await invocation);

            child = accepted.Quest;

            Assert.Null(accepted.Summary);
        }
        else
        {
            Task<GuildQuestDto> scheduled = queue.Pending.Reader.ReadAsync(deadline.Token).AsTask();

            Task first = await Task.WhenAny(scheduled, invocation);

            Assert.Same(scheduled, first);

            child = await scheduled;

            Assert.False(invocation.IsCompleted);

            var recovery = await f.Policy.CreateRecoveryAsync(owner, child.Id, child.OperationId, child.SignetRevision, deadline.Token);

            Assert.True(recovery.IsSuccess, recovery.Error.Message);

            var handle = await f.Authority.RebindAsync(recovery.Value, deadline.Token);

            Assert.True(handle.IsSuccess, handle.Error.Message);

            var pending = await f.Store.ReadLocalPendingAsync(owner, child.Id, deadline.Token);

            Assert.True(pending.IsSuccess, pending.Error.Message);

            var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, deadline.Token);

            Assert.True(opened.IsSuccess, opened.Error.Message);

            await using var connection = opened.Value;

            await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

            await using (var content = connection.Connection.CreateCommand())
            {
                content.CommandText = "SELECT Body FROM guild_content WHERE ContentId=(SELECT InputContentId FROM guild_local_executions WHERE QuestId=$quest)";

                content.Parameters.AddWithValue("$quest", child.Id.ToString("D").ToUpperInvariant());

                byte[] bytes = Assert.IsType<byte[]>(await content.ExecuteScalarAsync(deadline.Token));

                GuildLocalExecutionInput acceptedInput = JsonSerializer.Deserialize(bytes, GuildStorageJsonContext.Default.GuildLocalExecutionInput)!;

                Assert.Equal(GuildLocalExecutionMode.Sterile, acceptedInput.Mode);

                Assert.Equal(input.Value.Provider, acceptedInput.Provider);

                Assert.Equal(input.Value.Model, acceptedInput.Model);

                Assert.Empty(acceptedInput.Files);

                content.CommandText = "SELECT Body FROM guild_content WHERE ContentId=$goal";

                content.Parameters.AddWithValue("$goal", acceptedInput.Goal.ContentId.ToString("D").ToUpperInvariant());

                string body = System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(await content.ExecuteScalarAsync(deadline.Token)));

                Assert.Contains("[Attached: Explicit selected file]\n\n`````\nnotes\n```\nclaimed instruction\nSelected literal content\n````\n`````", body);
            }

            GuildLocalPort port = new(f.Store, f.Policy, f.Authority, new LongRunningOperationStore(db, f.Connections),
                new DeterministicLocalEngine(), new UnusedInference(), queue, new(), TimeProvider.System);

            var completed = await port.DispatchAsync(handle.Value, pending.Value.Request!, deadline.Token);

            Assert.True(completed.IsSuccess, completed.Error.Message);

            Assert.Equal(QuestState.Completed, completed.Value.State);

            var result = Assert.IsType<GuildLocalChildToolResult>(await invocation);

            Assert.Equal("Completed without an external effect.", result.Summary);

            Assert.Equal(child.Id, result.Quest.Id);

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_result_applications"));
        }

        Assert.Equal(parent.Value.Quest.Id, child.ParentQuestId);

        Assert.NotNull(child.SessionId);

        Assert.NotEqual(parent.Value.Quest.SessionId, child.SessionId);

        Assert.Equal(parent.Value.Quest.CampaignId, child.CampaignId);

        Assert.Equal(parent.Value.Quest.RootQuestId, child.RootQuestId);

        Assert.Equal(unlimited ? GuildBudgetMode.Unlimited : GuildBudgetMode.Finite, child.ResolvedGrant.Budget.Tokens.Mode);

        if (!unlimited) Assert.Equal(narrow ? 50_000L : 100_000L, child.ResolvedGrant.Budget.Tokens.Value);

        if (!cast)
        {
            Assert.Empty(child.ResolvedGrant.Tools);

            Assert.Empty(child.ResolvedGrant.Workspaces);

            Assert.Empty(child.ResolvedGrant.Memory);

            Assert.Empty(child.ResolvedGrant.Delegation);
        }

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM session_campaign_bindings WHERE BindingKindCode=1"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM Apprentices"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations"));

        if (!cast && !unlimited && !narrow)
        {
            // The pre-change intent binds the unattenuated 600,000-token request, not the resolved 100,000-token grant.
            System.Text.StringBuilder legacyPrompt = new("Compute child contribution");

            legacyPrompt.AppendLine();

            SystemPromptBuilder.AppendUntrusted(legacyPrompt, "Explicit selected file",
                "notes\n```\nclaimed instruction\nSelected literal content\n````");

            GuildBudgetGrant legacyBudget = new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Finite, 600_000));

            GuildRequestedGrant legacyGrant = new(Workspaces: new(true, []), Tools: new(true, []), Memory: new(true, []),
                Budget: new(true, legacyBudget), Delegation: new(true, []));

            var legacyDigest = GuildQuestIntent.Compute(parent.Value.Quest.Id,
                new GuildIssueChildRequest(logicalCall, 1, null, null, legacyPrompt.ToString(), legacyGrant));

            Assert.True(legacyDigest.IsSuccess, legacyDigest.Error.Message);

            var stored = await f.Store.GetAsync(owner, child.Id, deadline.Token);

            Assert.True(stored.IsSuccess, stored.Error.Message);

            Assert.Equal(legacyDigest.Value, stored.Value.IntentDigest);
        }

        object? replay = await context.InvokeBoundAsync(function, arguments, deadline.Token);

        Assert.True(replay is GuildLocalChildToolResult, replay?.ToString());

        Assert.Equal(child.Id, Assert.IsType<GuildLocalChildToolResult>(replay).Quest.Id);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));
    }

    [Theory]
    [InlineData(50_000L)]
    [InlineData(100_000L)]
    public async Task Prompt_only_child_cannot_allocate_a_finite_parent_envelope_already_held_by_a_sibling(long siblingTokens)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
        {
            Tools = [tool], Budget = new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Finite, 100_000)),
        } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        GuildCallerContext run = await RunCallerAsync(f, owner, parent.Value);

        Guid key = Guid.NewGuid();

        var sibling = await f.Store.IssueAsync(run, new(f.Generation, parent.Value.Quest.Id, null, key, key), parent.Value.Quest.Id,
            new(key, parent.Value.Quest.Revision, null, "Sibling", "first", new(Budget: new(true,
                new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Finite, siblingTokens))))), default);

        Assert.True(sibling.IsSuccess, sibling.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using (var connection = opened.Value)
        await using (var transaction = connection.Connection.BeginTransaction())
        {
            var snapshot = await new GuildBudgetLedger().ReadWithinAsync(connection.Connection, transaction, parent.Value.AllocationAccountId, default);

            Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

            Assert.Equal(100_000L - siblingTokens, snapshot.Value.Remaining.Tokens.Value);
        }

        var current = await f.Store.GetAsync(owner, parent.Value.Quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, current.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        f.ToolChildMode = GuildLocalExecutionMode.Sterile;

        ChildQueue queue = new();

        using GuildLocalToolRegistry registry = new(execution, input.Value, new ConclaveArchmage(null!, f.Settings, f.Store, queue));

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(function);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

        Task<object?> invocation = context.InvokeBoundAsync(function, new() { ["prompt"] = "second" }, deadline.Token).AsTask();

        Task<GuildQuestDto> scheduled = queue.Pending.Reader.ReadAsync(deadline.Token).AsTask();

        Task first = await Task.WhenAny(invocation, scheduled);

        Assert.Same(invocation, first);

        Assert.StartsWith("Guild.GrantExceeded:", Assert.IsType<string>(await invocation));

        Assert.False(queue.Pending.Reader.TryRead(out _));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));
    }

    [Fact]
    public async Task Prompt_only_child_refuses_revoked_signet_without_creating_work_or_allocation()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        ChildQueue queue = new();

        using GuildLocalToolRegistry registry = new(execution, input.Value, new ConclaveArchmage(null!, f.Settings, f.Store, queue));

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(function);

        var current = await f.Store.GetAsync(owner, parent.Value.Quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Guid key = Guid.NewGuid();

        var revoked = await f.Authority.RevokeAsync(owner, new(key, new string('A', 64)), current.Value.Quest.SignetId,
            new(key, current.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

        Assert.True(revoked.IsSuccess, revoked.Error.Message);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        object? result = await context.InvokeBoundAsync(function, new() { ["prompt"] = "child" }, default);

        Assert.StartsWith("Guild.AuthorityRevoked:", Assert.IsType<string>(result));

        Assert.False(queue.Pending.Reader.TryRead(out _));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));
    }

    private sealed class ChildQueue : IGuildLocalQuestScheduler
    {
        internal readonly Channel<GuildQuestDto> Pending = Channel.CreateUnbounded<GuildQuestDto>();

        public bool IsAvailable => true;

        public void Schedule(GuildQuestDto quest) => Pending.Writer.TryWrite(quest);
    }

    [Theory]
    [InlineData("{\"prompt\":\"task\",\"max_tokens\":\"10\"}")]
    [InlineData("{\"prompt\":\"task\",\"max_tokens\":0}")]
    [InlineData("{\"prompt\":\"task\",\"max_cost_usd\":0.00000000000000000000000000001}")]
    [InlineData("{\"prompt\":\"task\",\"max_tokens\":100,\"memory_grant\":\"memory\"}")]
    [InlineData("{\"prompt\":\"task\",\"max_tokens\":100,\"files\":[{\"path\":\"x\",\"content\":\"secret\",\"attachment_id\":\"00000000-0000-0000-0000-000000000001\"}]}")]
    public async Task Native_child_tool_coordinator_refuses_unqualified_input_without_creating_work(string json)
    {
        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue);

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(tool.Name), tool, default);

        Assert.NotNull(function);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        using JsonDocument document = JsonDocument.Parse(json);

        AIFunctionArguments arguments = new(document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));

        object? result = await context.InvokeBoundAsync(function, arguments, default);

        Assert.StartsWith("Guild.InvalidRequest:", Assert.IsType<string>(result));

        Assert.False(queue.Pending.Reader.TryRead(out _));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));
    }
}
