using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ToolLoopEvidence_repeated_round_names_its_advertised_tool_and_safe_next_step(bool streaming, bool unregistered)
    {
        const string tool = "review_progress";
        ScriptingChatClient chat = new();
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(AIFunctionFactory.Create((string privateValue) => "private-result-canary", tool, "test progress"));
        string requestedTool = unregistered ? "private-argument-canary\nforged_tool" : tool;
        Dictionary<string, object?> arguments = new() { ["privateValue"] = "private-argument-canary" };
        for (int i = 0; i < 2; i++)
        {
            if (streaming) chat.EnqueueStreamToolCall(requestedTool, $"repeat-{i}", arguments);
            else chat.EnqueueToolCall(requestedTool, $"repeat-{i}", arguments);
        }
        TestCapturingLogger<WizardIntelligenceProvider> log = new();
        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp, logger: log);
        string message;
        if (streaming)
        {
            List<IntelligenceEvent> events = await CollectStreamAsync(wizard, BaseRequest() with { Prompt = "check progress", SkipSpellRouting = true });
            IntelligenceEvent error = Assert.Single(events, e => e.Type == IntelligenceEventType.Error);
            Assert.Equal(ErrorCodes.Hub.NoProgressDetected, error.Data);
            message = error.Message ?? string.Empty;
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(BaseRequest() with { Prompt = "check progress", SkipSpellRouting = true }, InvocationContexts.AttendedSession(), CancellationToken.None);
            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCodes.Hub.NoProgressDetected, result.Error.Code);
            message = result.Error.Message;
        }
        Assert.Equal(2, streaming ? chat.StreamingCallCount : chat.BufferedCallCount);
        TestLogEntry warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no progress", StringComparison.Ordinal));
        Assert.DoesNotContain("private-argument-canary", message + warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-result-canary", message + warning.Message, StringComparison.Ordinal);
        Assert.Contains(unregistered ? "unregistered" : tool, message, StringComparison.Ordinal);
        Assert.Contains(unregistered ? "unregistered" : tool, warning.Message, StringComparison.Ordinal);
        Assert.Contains("request", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ToolLoopEvidence_trimmed_round_may_be_reacquired_with_real_context_admission(bool streaming, bool repeatedCycle)
    {
        const string tool = "review_evidence";
        ScriptingChatClient chat = new();
        List<string[]> observedResultIds = [];
        List<int> observedNotices = [];
        chat.BeforeCall = () =>
        {
            observedResultIds.Add(chat.LastBufferedMessages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.CallId).ToArray());
            observedNotices.Add(chat.LastBufferedMessages.SelectMany(m => m.Contents).OfType<TextContent>().Count(c => c.Text.Contains("Tool evidence trimmed", StringComparison.Ordinal)));
        };
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(AIFunctionFactory.Create((int evidence) => $"evidence-{evidence}:" + new string(evidence == 0 ? 'a' : 'b', 6500), tool, "returns bounded evidence"));
        (int, string)[] sequence = repeatedCycle
            ? [(0, "first-a"), (1, "then-b"), (0, "reread-a"), (1, "reread-b"), (0, "cycle-a")]
            : [(0, "first-a"), (1, "then-b"), (0, "reread-a")];
        foreach ((int evidence, string id) in sequence)
        {
            Dictionary<string, object?> args = new() { ["evidence"] = evidence };
            if (streaming) chat.EnqueueStreamToolCall(tool, id, args);
            else chat.EnqueueToolCall(tool, id, args);
        }

        if (streaming) chat.EnqueueStreamTokens("reacquired evidence complete");
        else chat.EnqueueText("reacquired evidence complete");
        ArcanumSettings settings = DefaultSettings() with { Providers = [DefaultProvider() with { ContextWindowLimit = 10000 }] };
        EvidenceContextEstimator estimator = new(new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)));
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, mcp: mcp, modelTokenEstimator: estimator);
        bool success;
        string terminal;
        if (streaming)
        {
            List<IntelligenceEvent> events = await CollectStreamAsync(wizard, BaseRequest() with { Prompt = "review the evidence", SkipSpellRouting = true, MaxOutputTokens = 64 });
            success = events.Any(e => e.Type == IntelligenceEventType.Result);
            terminal = string.Join(";", events.Where(e => e.Type == IntelligenceEventType.Error).Select(e => e.Data + ":" + e.Message));
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(BaseRequest() with { Prompt = "review the evidence", SkipSpellRouting = true, MaxOutputTokens = 64 }, InvocationContexts.AttendedSession(), CancellationToken.None);
            success = result.IsSuccess;
            terminal = result.IsFailure ? result.Error.Code + ":" + result.Error.Message : string.Empty;
        }

        List<IReadOnlyList<MeAiChatMessage>> calls = streaming ? chat.AllStreamingCalls : chat.AllBufferedCalls;
        Assert.True(calls.Count >= 3, $"Need actual three provider payloads before terminal assertion: {terminal}; calls={calls.Count}");
        Assert.True(observedResultIds[1].Contains("first-a", StringComparer.Ordinal), "A must be admitted before later eviction: " + string.Join(";", estimator.Observations));
        Assert.False(observedResultIds[2].Contains("first-a", StringComparer.Ordinal), "A must actually be evicted: " + string.Join(";", estimator.Observations));
        Assert.Contains("then-b", observedResultIds[2]);
        Assert.Equal(1, observedNotices[2]);
        Assert.All(observedNotices, count => Assert.InRange(count, 0, 1));
        Assert.Contains(estimator.Observations, value => value.StartsWith("over:", StringComparison.Ordinal));
        Assert.Equal(!repeatedCycle, success);
        if (repeatedCycle) Assert.Contains(ErrorCodes.Hub.NoProgressDetected, terminal, StringComparison.Ordinal);
        Assert.Equal(repeatedCycle ? 5 : 4, calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolLoopEvidence_multi_tool_diagnostics_are_bounded_and_report_omitted_names(bool streaming)
    {
        ScriptingChatClient chat = new();
        FakeMcpConnectionManager mcp = new();
        string[] names = Enumerable.Range(0, 10).Select(i => i == 0 ? "review_\n\u2028" + new string('x', 100) : $"review_tool_{i}").ToArray();
        foreach (string name in names) mcp.Tools.Add(AIFunctionFactory.Create(() => "stable", name, "test tool"));
        for (int round = 0; round < 2; round++)
        {
            List<AIContent> calls = names.Select((name, i) => (AIContent)new FunctionCallContent($"round-{round}-{i}", name)).ToList();
            if (streaming) chat.EnqueueStreamUpdates(new ChatResponseUpdate(ChatRole.Assistant, calls));
            else chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(ChatRole.Assistant, calls)));
        }
        TestCapturingLogger<WizardIntelligenceProvider> log = new();
        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp, logger: log);
        string message;
        if (streaming)
        {
            List<IntelligenceEvent> events = await CollectStreamAsync(wizard, BaseRequest() with { Prompt = "review tools", SkipSpellRouting = true });
            IntelligenceEvent error = Assert.Single(events, e => e.Type == IntelligenceEventType.Error);
            Assert.Equal(ErrorCodes.Hub.NoProgressDetected, error.Data);
            message = error.Message;
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(BaseRequest() with { Prompt = "review tools", SkipSpellRouting = true }, InvocationContexts.AttendedSession(), CancellationToken.None);
            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCodes.Hub.NoProgressDetected, result.Error.Code);
            message = result.Error.Message;
        }
        TestLogEntry warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no progress", StringComparison.Ordinal));
        Assert.Equal(2, streaming ? chat.StreamingCallCount : chat.BufferedCallCount);
        Assert.Contains("+2 more tools", message, StringComparison.Ordinal);
        Assert.Contains("review_??", message, StringComparison.Ordinal);
        Assert.Contains("calls: 10", message, StringComparison.Ordinal);
        Assert.DoesNotContain("review_tool_8", message + warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("review_tool_9", message + warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(names[0], message + warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', message + warning.Message);
        Assert.DoesNotContain('\u2028', message + warning.Message);
        Assert.True(message.Length < 1024);
        Assert.Null(warning.Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolLoopEvidence_notice_that_cannot_fit_refuses_before_the_next_provider_send(bool streaming)
    {
        const string tool = "review_evidence";
        ScriptingChatClient chat = new();
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.Add(AIFunctionFactory.Create(() => new string('a', 6500), tool, "returns bounded evidence"));
        if (streaming) { chat.EnqueueStreamToolCall(tool, "first-a"); chat.EnqueueStreamTokens("must not be sent"); }
        else { chat.EnqueueToolCall(tool, "first-a"); chat.EnqueueText("must not be sent"); }
        ArcanumSettings settings = DefaultSettings() with { Providers = [DefaultProvider() with { ContextWindowLimit = 1350 }] };
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, mcp: mcp);
        if (streaming)
        {
            List<IntelligenceEvent> events = await CollectStreamAsync(wizard, BaseRequest() with { Prompt = "review the evidence", SkipSpellRouting = true, MaxOutputTokens = 64 });
            IntelligenceEvent error = Assert.Single(events, e => e.Type == IntelligenceEventType.Error);
            Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, error.Data);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(BaseRequest() with { Prompt = "review the evidence", SkipSpellRouting = true, MaxOutputTokens = 64 }, InvocationContexts.AttendedSession(), CancellationToken.None);
            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, result.Error.Code);
        }
        Assert.Equal(1, streaming ? chat.StreamingCallCount : chat.BufferedCallCount);
    }

    private sealed class EvidenceContextEstimator(IModelTokenEstimator inner) : IModelTokenEstimator
    {
        public List<string> Observations { get; } = [];
        public ResolvedModelTokenizationProfile ResolveProfile(ProviderSettings provider, string model) => inner.ResolveProfile(provider, model);
        public ResolvedModelTokenizationProfile ResolveEffectiveProfile(ProviderSettings provider, string model) => inner.ResolveEffectiveProfile(provider, model);
        public TokenEstimate EstimateText(ProviderSettings provider, string model, string? text) => inner.EstimateText(provider, model, text);
        public ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request)
        {
            ContextTokenBreakdown result = inner.EstimateContext(request);
            string ids = string.Join(",", request.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.CallId));
            Observations.Add($"{(result.TotalTokens > request.Provider.ContextWindowLimit ? "over" : "fit")}:{result.TotalTokens}/{request.Provider.ContextWindowLimit}:reserve={result.ReservedTokens}:results={ids}");
            return result;
        }
    }
}
