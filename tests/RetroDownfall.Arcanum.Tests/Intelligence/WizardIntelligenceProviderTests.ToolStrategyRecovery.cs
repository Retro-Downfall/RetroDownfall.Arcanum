using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Strategy_recovery_feeds_a_typed_argument_correction_before_the_next_provider_round(bool streaming)
    {
        using ArcanumTestHomeScope home = new("strategy-invalid-read");
        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        _workspace.WriteFile("present.txt", "verified revised read");
        FakeMcpConnectionManager tools = await WorkspaceOutcomeToolsAsync(manager, _workspace.Root);
        ScriptingChatClient chat = new();

        EnqueueRecoveryTool(chat, streaming, "read_file", "invalid-line", WorkspaceReadArguments("present.txt", 0, 1));
        EnqueueRecoveryTool(chat, streaming, "read_file", "revised-line", WorkspaceReadArguments("present.txt", 1, 1));
        EnqueueRecoveryAnswer(chat, streaming, "verified revised action");

        var outcome = await RunRecoveryTurnAsync(chat, tools, streaming);

        Assert.Equal("verified revised action", outcome.Answer);
        Assert.Equal(3, outcome.Calls.Count);
        string correction = Assert.Single(outcome.Calls[1].SelectMany(message => message.Contents)
            .OfType<TextContent>().Select(item => item.Text),
            text => text.StartsWith("Arcanum recovery guidance", StringComparison.Ordinal));
        Assert.Contains("schema", correction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read_file", correction, StringComparison.Ordinal);
        Assert.DoesNotContain("present.txt", correction, StringComparison.Ordinal);
        Assert.Contains(outcome.Calls[2].SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>(), item => Equals(item.Result, "verified revised read"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Strategy_recovery_intervenes_once_on_same_or_alternating_workspace_evidence(bool streaming, bool alternating)
    {
        using ArcanumTestHomeScope home = new("strategy-repeat");
        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        _workspace.WriteFile("left.txt", "left unchanged");
        _workspace.WriteFile("right.txt", "right unchanged");
        FakeMcpConnectionManager tools = await WorkspaceOutcomeToolsAsync(manager, _workspace.Root);
        ScriptingChatClient chat = new();

        string[] calls = alternating ? ["left.txt", "right.txt", "left.txt", "right.txt"] : ["left.txt", "left.txt", "left.txt"];
        for (int index = 0; index < calls.Length; index++)
        {
            Dictionary<string, object?> args = WorkspaceReadArguments(calls[index]);

            if (index >= 2) args = args.Reverse().ToDictionary();

            EnqueueRecoveryTool(chat, streaming, "read_file", $"payload-id-{index}", args);
        }

        var outcome = await RunRecoveryTurnAsync(chat, tools, streaming);

        Assert.Equal(ErrorCodes.Hub.NoProgressDetected, outcome.Error);
        Assert.Equal(calls.Length, outcome.Calls.Count);
        string intervention = Assert.Single(outcome.Calls[^1].SelectMany(message => message.Contents)
            .OfType<TextContent>().Select(item => item.Text),
            text => text.StartsWith("Arcanum recovery guidance", StringComparison.Ordinal));
        Assert.Contains("list_directory", intervention, StringComparison.Ordinal);
        Assert.Contains("unchanged", intervention, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("left.txt", intervention, StringComparison.Ordinal);
        Assert.DoesNotContain("right.txt", intervention, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Strategy_recovery_uses_an_authorized_alternative_when_one_is_available(bool streaming, bool nativeAlternative)
    {
        using ArcanumTestHomeScope home = new("strategy-alternative");
        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        _workspace.WriteFile("present.txt", "unchanged");
        _workspace.WriteFile("different.txt", "another authorized observation");
        FakeMcpConnectionManager tools = await WorkspaceOutcomeToolsAsync(manager, _workspace.Root);
        ScriptingChatClient chat = new();

        if (!nativeAlternative)
        {
            tools.Tools.RemoveAll(tool => tool.Name == "list_directory");
            tools.Tools.Add(AIFunctionFactory.Create((string relativePath) => "external lookalike", "list_directory", "Fixture external code"));
        }

        EnqueueRecoveryTool(chat, streaming, "read_file", "first-read", WorkspaceReadArguments("present.txt"));
        EnqueueRecoveryTool(chat, streaming, "read_file", "repeat-read", WorkspaceReadArguments("present.txt"));
        EnqueueRecoveryTool(chat, streaming, "list_directory", "different-observation", new() { ["relativePath"] = "." });
        EnqueueRecoveryAnswer(chat, streaming, "verified different evidence");

        var outcome = await RunRecoveryTurnAsync(chat, tools, streaming);

        if (!nativeAlternative)
        {
            Assert.Equal(ErrorCodes.Hub.NoProgressDetected, outcome.Error);
            Assert.Equal(2, outcome.Calls.Count);

            return;
        }

        Assert.Equal("verified different evidence", outcome.Answer);
        Assert.Equal(4, outcome.Calls.Count);
        Assert.Contains(outcome.Calls[^1].SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>(), item => item.CallId == "different-observation"
                && item.Result!.ToString()!.Contains("different.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Calls[^1].SelectMany(message => message.Contents)
            .OfType<TextContent>(), item => item.Text.StartsWith("Arcanum recovery guidance", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Strategy_recovery_guides_a_failed_check_to_fresh_evidence_across_the_round_window(bool streaming)
    {
        using ArcanumTestHomeScope home = new("strategy-check");
        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        _workspace.WriteFile("project.evidence", string.Join('\n', Enumerable.Range(1, 10).Select(line => $"fresh evidence {line}")));
        FakeMcpConnectionManager tools = await WorkspaceOutcomeToolsAsync(manager, _workspace.Root);
        ScriptingChatClient chat = new();
        int checks = 0;
        bool verifiedWorkspace = false;

        chat.BeforeCall = () =>
        {
            verifiedWorkspace |= chat.LastBufferedMessages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Any(content => content.CallId == "evidence-10" && Equals(content.Result, "fresh evidence 10"));
        };

        tools.Tools.Add(new StrategyCheckTool(() =>
        {
            bool verified = ++checks > 1 && verifiedWorkspace;
            return new TrustedStructuredToolResult(TrustedStructuredToolResultKind.WorkspaceCheck,
                verified ? """{"status":"ok","exitCode":0}""" : """{"status":"failed","code":"check_failed","exitCode":1}""")
            {
                FailureEvidence = verified ? null : new(TurnFailurePhase.ToolExecution, TurnEffectState.Completed, TurnRecoveryAction.InspectWorkspace),
            };
        }));

        EnqueueRecoveryTool(chat, streaming, "workspace_check", "failed-check", new() { ["profile"] = "build" });
        for (int line = 1; line <= 10; line++)
        {
            EnqueueRecoveryTool(chat, streaming, "read_file", $"evidence-{line}", WorkspaceReadArguments("project.evidence", line, line));
        }
        EnqueueRecoveryTool(chat, streaming, "workspace_check", "verified-check", new() { ["profile"] = "build" });
        EnqueueRecoveryAnswer(chat, streaming, "check verified the revised state");

        var outcome = await RunRecoveryTurnAsync(chat, tools, streaming);

        Assert.Equal("check verified the revised state", outcome.Answer);
        Assert.Equal(13, outcome.Calls.Count);
        Assert.Equal(2, checks);
        string correction = Assert.Single(outcome.Calls[1].SelectMany(message => message.Contents)
            .OfType<TextContent>().Select(item => item.Text),
            text => text.StartsWith("Arcanum recovery guidance", StringComparison.Ordinal));
        Assert.Contains("workspace_check", correction, StringComparison.Ordinal);
        Assert.Contains("list_directory", correction, StringComparison.Ordinal);
        Assert.Contains(outcome.Calls[^1].SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>(), item => item.CallId == "verified-check" && item.Result!.ToString()!.Contains("\"exitCode\":0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Strategy_recovery_preserves_an_unknown_effect_and_existing_invocation_correlation(bool streaming, bool correlated)
    {
        Guid? invocation = correlated ? Guid.NewGuid() : null;
        int executions = 0;
        FakeMcpConnectionManager tools = new();
        ScriptingChatClient chat = new();

        tools.Tools.Add(new StrategyCheckTool(() =>
        {
            executions++;

            return new TrustedStructuredToolResult(TrustedStructuredToolResultKind.WorkspaceCheck,
                """{"status":"timed_out","code":"timed_out"}""")
            {
                FailureEvidence = new(TurnFailurePhase.ToolExecution, TurnEffectState.Unknown,
                    correlated ? TurnRecoveryAction.ReconcileTool : TurnRecoveryAction.InspectWorkspace)
                { InvocationId = invocation },
            };
        }));

        EnqueueRecoveryTool(chat, streaming, "workspace_check", "uncertain-check", new() { ["profile"] = "build" });
        EnqueueRecoveryTool(chat, streaming, "workspace_check", "must-not-repeat", new() { ["profile"] = "build" });

        var outcome = await RunRecoveryTurnAsync(chat, tools, streaming);

        Assert.Equal("Hub.ToolReconciliationRequired", outcome.Error);
        Assert.Single(outcome.Calls);
        Assert.Equal(1, executions);
        Assert.NotNull(outcome.Failure);
        Assert.Equal(TurnEffectState.Unknown, outcome.Failure.Effect);
        Assert.Equal(invocation, outcome.Failure.InvocationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Strategy_recovery_obeys_provider_context_admission_before_sending_the_guidance(bool streaming)
    {
        using ArcanumTestHomeScope home = new("strategy-budget");
        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        _workspace.WriteFile("present.txt", "verified revised read");
        FakeMcpConnectionManager tools = await WorkspaceOutcomeToolsAsync(manager, _workspace.Root);
        ScriptingChatClient chat = new();

        EnqueueRecoveryTool(chat, streaming, "read_file", "invalid-line", WorkspaceReadArguments("present.txt", 0, 1));
        EnqueueRecoveryAnswer(chat, streaming, "must not send beyond the provider window");

        var outcome = await RunRecoveryTurnAsync(chat, tools, streaming, new RecoveryBudgetEstimator(
            new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance))));

        Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, outcome.Error);
        Assert.Single(outcome.Calls);
    }

    private sealed class RecoveryBudgetEstimator(IModelTokenEstimator inner) : IModelTokenEstimator
    {
        public ResolvedModelTokenizationProfile ResolveProfile(ProviderSettings provider, string model) =>
            inner.ResolveProfile(provider, model);

        public ResolvedModelTokenizationProfile ResolveEffectiveProfile(ProviderSettings provider, string model) =>
            inner.ResolveEffectiveProfile(provider, model);

        public TokenEstimate EstimateText(ProviderSettings provider, string model, string? text) =>
            inner.EstimateText(provider, model, text);

        public ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request)
        {
            ContextTokenBreakdown estimate = inner.EstimateContext(request);

            return request.Messages.SelectMany(message => message.Contents).OfType<TextContent>()
                .Any(text => text.Text.StartsWith("Arcanum recovery guidance", StringComparison.Ordinal))
                ? estimate with { TotalTokens = request.Provider.ContextWindowLimit + 1 }
                : estimate;
        }
    }

    private sealed class StrategyCheckTool(Func<TrustedStructuredToolResult> run) : AIFunction
    {
        public override string Name => "workspace_check";

        public override JsonElement JsonSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(run());
    }

    private static void EnqueueRecoveryTool(ScriptingChatClient chat, bool streaming, string name, string id, Dictionary<string, object?> args)
    {
        if (streaming) chat.EnqueueStreamToolCall(name, id, args);
        else chat.EnqueueToolCall(name, id, args);
    }

    private static void EnqueueRecoveryAnswer(ScriptingChatClient chat, bool streaming, string text)
    {
        if (streaming) chat.EnqueueStreamTokens(text);
        else chat.EnqueueText(text);
    }

    private async Task<(string? Answer, string? Error, List<IReadOnlyList<ChatMessage>> Calls, TurnFailureEvidence? Failure)> RunRecoveryTurnAsync(
        ScriptingChatClient chat, FakeMcpConnectionManager tools, bool streaming, IModelTokenEstimator? estimator = null)
    {
        var settings = DefaultSettings() with { Providers = [DefaultProvider() with { ContextWindowLimit = 65_536 }] };
        var request = WorkspaceOutcomeRequest(_workspace.Root) with { Prompt = "Check the workspace and verify the proposed change." };
        var wizard = CreateWizard(chat, settings, mcp: tools, modelTokenEstimator: estimator);

        if (streaming)
        {
            List<IntelligenceEvent> events = await CollectStreamAsync(wizard, request);
            IntelligenceEvent? error = events.LastOrDefault(item => item.Type == IntelligenceEventType.Error);

            return (events.LastOrDefault(item => item.Type == IntelligenceEventType.Result)?.Message,
                error?.Data, chat.AllStreamingCalls, error?.FailureEvidence);
        }

        var result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);
        return result.IsSuccess
            ? (result.Value.Text, null, chat.AllBufferedCalls, null)
            : (null, result.Error.Code, chat.AllBufferedCalls, result.Error.FailureEvidence);
    }
}
