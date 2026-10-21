using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(ToolPolicy.AllTools)]
    [InlineData(ToolPolicy.ReadOnlyTools)]
    public async Task MemoryRecall_enabled_attended_operator_receives_native_tool_with_mcp_disabled(ToolPolicy policy)
    {
        var settings = DefaultSettings();

        settings.Features.AgentMemoryRecall = true;

        ScriptingChatClient chat = new();

        chat.EnqueueText("answer");

        var wizard = CreateWizard(chat, settings, memoryRecallReader: new RecallReader());

        var result = await wizard.ExecutePromptAsync(BaseRequest() with
        {
            Prompt = "query",
            SkipSpellRouting = true,
            DisableMcpTools = true,
            ToolPolicy = policy,
        }, InvocationContexts.AttendedGlobalOnlySession(), default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Contains(chat.LastChatOptions!.Tools!, static tool => tool.Name == "recall_memory");
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unattended")]
    [InlineData("background")]
    [InlineData("no-context")]
    [InlineData("no-tools")]
    public async Task MemoryRecall_unavailable_invocations_do_not_receive_native_tool(string mode)
    {
        var settings = DefaultSettings();

        settings.Features.AgentMemoryRecall = mode != "disabled";

        ScriptingChatClient chat = new();

        chat.EnqueueText("answer");

        var wizard = CreateWizard(chat, settings, memoryRecallReader: new RecallReader());

        var invocation = mode == "background" ? ArcanumInvocationContext.None
            : mode == "no-context" ? InvocationContexts.NoContextSession() : InvocationContexts.AttendedGlobalOnlySession();

        var result = await wizard.ExecutePromptAsync(BaseRequest() with
        {
            Prompt = "query",
            SkipSpellRouting = true,
            DisableMcpTools = true,
            UnattendedMode = mode == "unattended",
            ToolPolicy = mode == "no-tools" ? ToolPolicy.NoTools : ToolPolicy.AllTools,
        }, invocation, default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.DoesNotContain(chat.LastChatOptions!.Tools ?? [], static tool => tool.Name == "recall_memory");
    }

    [Fact]
    public async Task MemoryRecall_query_is_not_copied_to_durable_audit_when_general_argument_redaction_is_off()
    {
        var settings = DefaultSettings();

        settings.Features.AgentMemoryRecall = true;

        settings.Host.AuditLog.RedactToolArguments = false;

        ScriptingChatClient chat = new();

        chat.EnqueueToolCall("recall_memory", arguments: new Dictionary<string, object?>
        {
            ["query"] = "private recall phrase",
            ["scope"] = "Lexicon",
        });

        chat.EnqueueText("answer");

        var audit = new InferenceAuditContext { RequestType = "recall-test" };

        var result = await CreateWizard(chat, settings, memoryRecallReader: new RecallReader()).ExecutePromptAsync(
            BaseRequest() with { Prompt = "query", SkipSpellRouting = true, DisableMcpTools = true },
            InvocationContexts.AttendedGlobalOnlySession(), default, audit);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Contains("recall_memory", audit.ToolNames);

        Assert.DoesNotContain(audit.ToolArgumentsJson, static value => value.Contains("private recall phrase", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task MemoryRecall_native_tool_honors_active_spell_attunement(bool declared, bool expected)
    {
        await CreateSpellWithDeclaredToolsAsync("recall-spell", declared ? ["recall_memory"] : ["get_local_system_time"]);

        var settings = DefaultSettings();

        settings.Features.AgentMemoryRecall = true;

        ScriptingChatClient chat = new();

        chat.EnqueueText("answer");

        var result = await CreateWizard(chat, settings, memoryRecallReader: new RecallReader()).ExecutePromptAsync(
            BaseRequest() with { Prompt = "query", WorkingDirectory = _workspace.Root, OverrideSpellName = "recall-spell", DisableMcpTools = true },
            InvocationContexts.AttendedGlobalOnlySession(), default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(expected, (chat.LastChatOptions!.Tools ?? []).Any(static tool => tool.Name == "recall_memory"));
    }

    [Fact]
    public async Task MemoryRecall_native_tool_honors_configured_forbidden_arts()
    {
        var settings = DefaultSettings();

        settings.Features.AgentMemoryRecall = true;

        settings.Security.Ward.ForbiddenArts = ["recall_memory"];

        ScriptingChatClient chat = new();

        chat.EnqueueText("answer");

        var result = await CreateWizard(chat, settings, memoryRecallReader: new RecallReader()).ExecutePromptAsync(
            BaseRequest() with { Prompt = "query", SkipSpellRouting = true, DisableMcpTools = true, ToolPolicy = ToolPolicy.NoForbiddenArts },
            InvocationContexts.AttendedGlobalOnlySession(), default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.DoesNotContain(chat.LastChatOptions!.Tools ?? [], static tool => tool.Name == "recall_memory");
    }

    private sealed class RecallReader : IMemoryRecallCandidateReader
    {
        public Task<Result<MemoryRecallCandidates>> ReadAsync(MemoryRecallRequest request,
            CanonicalCampaignContext canonical, MemoryScope memoryScope, CancellationToken cancellationToken) =>
            Task.FromResult(Result<MemoryRecallCandidates>.Success(new([
                new(MemoryRecallScope.Lexicon, "one", "query answer"),
                new(MemoryRecallScope.Lexicon, "two", "query second answer"),
            ])));
    }
}
