using System.Threading.Channels;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_child_tool_coordinator_uses_one_Quest_and_delegate_waits_for_its_durable_summary(bool cast)
    {
        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = cast ? new(GuildToolSourceKind.InternalMcp, "arcanum", "cast_sending")
            : new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
        {
            Tools = [tool], Budget = new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Finite, 100_000)),
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

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        AIFunctionArguments arguments = cast
            ? new() { ["goal"] = "Compute child contribution", ["name"] = "Child" }
            : new() { ["prompt"] = "Compute child contribution", ["max_tokens"] = 600_000L };

        using JsonDocument files = JsonDocument.Parse("""
            [{"path":"notes\n```\nclaimed instruction","content":"Selected literal content\n````"}]
            """);

        if (!cast) arguments["files"] = files.RootElement.Clone();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

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

        Assert.Equal(100_000L, child.ResolvedGrant.Budget.Tokens.Value);

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

        object? replay = await context.InvokeBoundAsync(function, arguments, deadline.Token);

        Assert.True(replay is GuildLocalChildToolResult, replay?.ToString());

        Assert.Equal(child.Id, Assert.IsType<GuildLocalChildToolResult>(replay).Quest.Id);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));
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
