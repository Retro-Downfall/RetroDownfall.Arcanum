using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnContextGuardsTests
{

    [Fact]
    public void ExpandDeletionToCompleteToolGroups_IncludesToolResultPartner()
    {
        Guid callId = Guid.NewGuid();
        Guid resultId = Guid.NewGuid();
        Guid otherId = Guid.NewGuid();

        List<Entry> ordered =
        [
            new() { Id = callId, Role = MessageRole.Assistant, Content = "[ToolCall: x()]", ToolName = "x" },
            new() { Id = resultId, Role = MessageRole.System, Content = "[ToolResult: ok]" },
            new() { Id = otherId, Role = MessageRole.User, Content = "hi" },
        ];

        HashSet<Guid> expanded = TurnContextGuards.ExpandDeletionToCompleteToolGroups(ordered, [callId]);

        Assert.Contains(callId, expanded);
        Assert.Contains(resultId, expanded);
        Assert.DoesNotContain(otherId, expanded);
    }

    [Fact]
    public void DropOrphanToolHalves_RemovesLoneToolCall()
    {
        List<Entry> ordered =
        [
            new() { Id = Guid.NewGuid(), Role = MessageRole.User, Content = "hi" },
            new() { Id = Guid.NewGuid(), Role = MessageRole.Assistant, Content = "[ToolCall: x()]", ToolName = "x" },
        ];

        List<Entry> cleaned = TurnContextGuards.DropOrphanToolHalves(ordered);

        Assert.Single(cleaned);
        Assert.Equal(MessageRole.User, cleaned[0].Role);
    }

    [Fact]
    public void CheckContextBudget_FailsWhenOverWindow()
    {
        Result result = TurnContextGuards.CheckContextBudget(
            new ContextTokenBreakdown
            {
                Provider = "provider",
                Model = "model",
                Profile = new ResolvedModelTokenizationProfile
                {
                    ProfileId = "test",
                    Type = ModelTokenizationProfileType.UnknownFallback,
                    TokenizerId = "o200k_base",
                    SafetyMarginPercent = 15,
                    PerMessageOverheadTokens = 4,
                    PerToolOverheadTokens = 8,
                    ProviderFramingTokens = 3,
                    StopTokenOverheadTokens = 1,
                    UnknownImageReserveTokens = 2048,
                    Confidence = 0.5,
                },
                Components = [],
                InputTokens = 9_000,
                ReservedTokens = 1_024,
                TotalTokens = 10_024,
                OverallClassification = TokenEstimateClassification.Estimated,
                SafetyMarginTokens = 1_000,
            },
            contextWindowLimit: 8192);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, result.Error.Code);
    }

    [Fact]
    public void TryTrimOldestToolExchanges_RemovesPairs()
    {
        List<MeAiChatMessage> messages =
        [
            new(ChatRole.System, "sys"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "tool_a")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", new string('x', 4000))]),
            new(ChatRole.User, "now"),
        ];

        _ = TurnContextGuards.TryTrimOldestToolExchanges(
            messages,
            static m => m.Sum(msg => 100 + (msg.Text?.Length ?? 0) + msg.Contents.Sum(c => 50 + (c.ToString()?.Length ?? 0))),
            maxTokens: 250);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.User, messages[1].Role);
    }

    /// <summary>
    /// A stateless <c>/v1</c> transcript maps N parallel tool calls to ONE assistant message
    /// followed by N tool messages. Removing a fixed pair splits that turn and leaves orphan tool
    /// results, which every OpenAI-compatible provider rejects.
    /// </summary>
    [Fact]
    public void TryTrimOldestToolExchanges_ParallelToolCalls_RemovesTheWholeExchange()
    {
        List<MeAiChatMessage> messages =
        [
            new(ChatRole.System, "sys"),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("c1", "tool_a"),
                new FunctionCallContent("c2", "tool_b"),
                new FunctionCallContent("c3", "tool_c"),
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", new string('x', 4000))]),
            new(ChatRole.Tool, [new FunctionResultContent("c2", new string('y', 4000))]),
            new(ChatRole.Tool, [new FunctionResultContent("c3", new string('z', 4000))]),
            new(ChatRole.User, "now"),
        ];

        _ = TurnContextGuards.TryTrimOldestToolExchanges(
            messages,
            static m => m.Sum(msg => 100 + (msg.Text?.Length ?? 0) + msg.Contents.Sum(c => 50 + (c.ToString()?.Length ?? 0))),
            maxTokens: 250);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.User, messages[1].Role);
    }

    /// <summary>
    /// The trim loop keeps a running total, subtracting each removed run's own marginal estimate
    /// rather than re-estimating the whole transcript on every iteration. That total is an
    /// approximation of what the transcript now costs, never a measurement of it: the estimator
    /// applies its safety margin as a ceiling over the whole input, and a ceiling does not
    /// distribute over addition, so the removed runs' own margins sum to a little more than the
    /// share they contributed to the transcript's. The running total therefore drifts below what
    /// the surviving list actually estimates at, and a verdict answered from it reports "under
    /// budget" for a transcript that the authoritative breakdown taken immediately afterwards -
    /// the one <c>EnsureContextBudgetWithMaterializations</c> builds right after this call - puts
    /// over. The verdict has to be a measurement of what was left behind.
    /// </summary>
    /// <remarks>
    /// Swept across every budget the trim can exit at, with the production estimator bound exactly
    /// as <c>EnsureContextBudgetWithMaterializations</c> binds it, because only the budgets that
    /// fall inside the drift can tell an accumulated total from a measurement.
    /// </remarks>
    [Fact]
    public void TryTrimOldestToolExchanges_AnswersFromWhatIsLeft_NotFromTheRunningTotal()
    {
        ModelTokenEstimator estimator = new(
            new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        ProviderSettings provider = new()
        {
            Name = "openai-compatible",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.openai.com/v1",
            Models = [new ModelEntry(TrimSweepModel)],
            ContextWindowLimit = 128_000,
        };

        ChatOptions options = new();

        int Count(IReadOnlyList<MeAiChatMessage> transcript) =>
            estimator.EstimateContext(
                new ModelTokenizationRequest(
                    provider,
                    TrimSweepModel,
                    transcript,
                    options,
                    ReservedAnswerTokens: 256,
                    ReservedReasoningTokens: 0))
                .TotalTokens;

        int untrimmable = Count(TrimSweepTranscript(exchanges: 0));
        int whole = Count(TrimSweepTranscript(TrimSweepExchanges));

        Assert.True(
            whole > untrimmable,
            $"the sweep is vacuous unless the tool exchanges cost something: {whole} vs {untrimmable}");

        List<string> disagreements = [];

        for (int budget = untrimmable; budget <= whole; budget++)
        {
            List<MeAiChatMessage> transcript = TrimSweepTranscript(TrimSweepExchanges);

            bool reportedUnderBudget = TurnContextGuards.TryTrimOldestToolExchanges(
                transcript,
                Count,
                budget);

            int actual = Count(transcript);

            if (reportedUnderBudget != actual <= budget)
            {
                disagreements.Add($"budget {budget}: said {reportedUnderBudget}, left {actual}");
            }
        }

        Assert.True(
            disagreements.Count == 0,
            "budgets where the returned verdict disagreed with a fresh estimate of the transcript "
            + $"the trim left behind:\n{string.Join("\n", disagreements)}");
    }

    [Fact]
    public void ResolveContinueThenReplay_AutoUsesIdempotencyHeader()
    {
        DefaultHttpContext withKey = new();
        withKey.Request.Headers[ArcanumApiHeaders.IdempotencyKey] = "k";

        DefaultHttpContext withoutKey = new();

        Assert.True(TurnContextGuards.ResolveContinueThenReplay(withKey, DisconnectPolicy.Auto));
        Assert.False(TurnContextGuards.ResolveContinueThenReplay(withoutKey, DisconnectPolicy.Auto));
        Assert.True(TurnContextGuards.ResolveContinueThenReplay(withoutKey, DisconnectPolicy.ContinueThenReplay));
        Assert.False(TurnContextGuards.ResolveContinueThenReplay(withKey, DisconnectPolicy.CancelAbandoned));
    }

    private const string TrimSweepModel = "an-unlisted-model-estimated-with-a-safety-margin";

    private const int TrimSweepExchanges = 16;

    /// <summary>
    /// A transcript shaped the way a stateless tool loop leaves one: a leading system message the
    /// trim must preserve, <paramref name="exchanges"/> assistant-call/tool-result pairs it may
    /// remove, and the final user turn.
    /// </summary>
    private static List<MeAiChatMessage> TrimSweepTranscript(int exchanges)
    {
        List<MeAiChatMessage> transcript = [new(ChatRole.System, "sys")];

        for (int index = 0; index < exchanges; index++)
        {
            transcript.Add(new MeAiChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent($"call-{index}", "record_progress")]));

            transcript.Add(new MeAiChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent($"call-{index}", $"progress {index} {new string('x', 29)}")]));
        }

        transcript.Add(new MeAiChatMessage(ChatRole.User, "now"));

        return transcript;
    }

}
