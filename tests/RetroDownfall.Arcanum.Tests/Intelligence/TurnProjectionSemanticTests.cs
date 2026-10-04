using System.Text.Json;

using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnProjectionSemanticTests
{
    [Fact]
    public void IntelligenceEventProjection_ContextCompressionAndHumanPrompt_MapToTypedFrames()
    {
        IntelligenceEvent compressed = Assert.Single(
            IntelligenceEventProjection.Map(
                new ContextCompressed(Correlation(1), "Context compressed")));
        Assert.Equal(IntelligenceEventType.Status, compressed.Type);
        Assert.Equal("Context compressed", compressed.Message);

        IntelligenceEvent human = Assert.Single(
            IntelligenceEventProjection.Map(
                new HumanInputRequested(Correlation(2), "call-human", "Choose a path")));
        Assert.Equal(IntelligenceEventType.ToolCall, human.Type);
        Assert.Equal("ask_human", human.Message);
        Assert.Equal("Choose a path", human.Data);
        Assert.Equal(
            new IntelligenceToolCallEvent("call-human", "ask_human", "Choose a path"),
            human.ToolCall);

        ContextTokenBreakdown breakdown = Breakdown();
        IntelligenceEvent context = Assert.Single(
            IntelligenceEventProjection.Map(
                new ContextAccounted(Correlation(3), breakdown)));
        Assert.Equal(IntelligenceEventType.Context, context.Type);
        Assert.Equal(breakdown, context.ContextBreakdown);
    }

    [Fact]
    public void IntelligenceEventProjection_RunAbandoned_UsesFallbackOrProvidedError()
    {
        IntelligenceEvent fallback = Assert.Single(
            IntelligenceEventProjection.Map(
                new RunAbandoned(
                    Correlation(1),
                    Error: null,
                    TurnTerminationReason.Cancelled,
                    Usage: null,
                    Warnings: [],
                    Interrupted: true,
                    PartialText: "partial")));
        Assert.Equal(IntelligenceEventType.Error, fallback.Type);
        Assert.Equal("Turn abandoned.", fallback.Message);
        Assert.Equal(ErrorCodes.Hub.Error, fallback.Data);

        Error expected = new(ErrorCodes.Guardrails.Blocked, "provider failed");
        IntelligenceEvent provided = Assert.Single(
            IntelligenceEventProjection.Map(
                new RunAbandoned(
                    Correlation(2),
                    expected,
                    TurnTerminationReason.ProviderFailure,
                    Usage: null,
                    Warnings: [],
                    Interrupted: false,
                    PartialText: null)));
        Assert.Equal(expected.Message, provided.Message);
        Assert.Equal(expected.Code, provided.Data);
    }

    [Fact]
    public void IntelligenceEventProjection_CompletedWithReasoningAndNoUsage_EmitsReasoningBeforeResult()
    {
        ReasoningContentSegment reasoning = new("concise summary", ReasoningOutputMode.Summary);
        RunCompleted completed = new(
            Correlation(1),
            FinalText: "answer",
            Usage: null,
            ToolCalls: null,
            FinishReason: "stop",
            Warnings: ["structured warning"],
            SessionId: null,
            StructuredOutputWarning: true)
        {
            Reasoning = [reasoning],
        };

        IntelligenceEvent[] frames = IntelligenceEventProjection.Map(completed).ToArray();

        Assert.Collection(
            frames,
            frame =>
            {
                Assert.Equal(IntelligenceEventType.Reasoning, frame.Type);
                Assert.Equal(reasoning, frame.Reasoning);
                Assert.Equal(reasoning.Text, frame.Message);
            },
            frame =>
            {
                Assert.Equal(IntelligenceEventType.Result, frame.Type);
                Assert.Equal("answer", frame.Message);
                Assert.Equal("0", frame.Data);
                Assert.Null(frame.Usage);
                Assert.Equal("stop", frame.FinishReason);
                Assert.Equal(["structured warning"], frame.Warnings);
            });
    }

    [Fact]
    public void IntelligenceEventProjection_FailedToolWithoutPublicText_UsesSyntheticErrorAndResult()
    {
        ToolInvocationCompleted completed = new(
            Correlation(1),
            "call-1",
            "lookup",
            "{}",
            "synthetic result",
            Failed: true,
            Denied: false,
            ToleratedFailure: true,
            PublicErrorText: null,
            Duration: TimeSpan.FromMilliseconds(5),
            AttachmentPostProcessed: false);

        IntelligenceEvent[] frames = IntelligenceEventProjection.Map(completed).ToArray();

        Assert.Collection(
            frames,
            error =>
            {
                Assert.Equal(IntelligenceEventType.ToolError, error.Type);
                Assert.Contains("failed and was tolerated", error.Data, StringComparison.Ordinal);
                Assert.Equal("call-1", error.ToolCall?.CallId);
            },
            result =>
            {
                Assert.Equal(IntelligenceEventType.ToolResult, result.Type);
                Assert.Equal("synthetic result", result.Data);
                Assert.Equal("call-1", result.ToolCall?.CallId);
            });
    }

    [Fact]
    public void IntelligenceEventProjection_DeniedTool_PreservesNonWireOutcome()
    {
        ToolInvocationCompleted completed = new(
            Correlation(1),
            "call-denied",
            "execute_command",
            "{}",
            "blocked",
            Failed: false,
            Denied: true,
            ToleratedFailure: false,
            PublicErrorText: null,
            Duration: TimeSpan.Zero,
            AttachmentPostProcessed: false);

        IntelligenceEvent result = Assert.Single(
            IntelligenceEventProjection.Map(completed));

        Assert.True(result.ToolDenied);

        string json = JsonSerializer.Serialize(
            result,
            ArcanumJsonContext.Default.IntelligenceEvent);

        Assert.DoesNotContain(
            "toolDenied",
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AttachmentRefreshed_ProjectsToNativeNdjson()
    {
        AttachmentRefreshEvent detail = new(
            Guid.NewGuid(),
            "notes.txt",
            2,
            NewVersionCreated: true,
            QueuedForInjection: true,
            "notes.txt",
            "ABC123",
            12,
            DateTimeOffset.UtcNow);
        AttachmentRefreshed refreshed = new(Correlation(1), detail);

        IntelligenceEvent native = Assert.Single(IntelligenceEventProjection.Map(refreshed));
        Assert.Equal(IntelligenceEventType.AttachmentRefreshed, native.Type);
        Assert.Equal(detail, native.AttachmentRefresh);
    }

    /// <summary>
    /// A Ward records an ordinary tool call. The tool name alone does not describe which command runs
    /// against which path, so the arguments — including the <c>_arcanumRiskDisclosure</c> required by
    /// DESIGN §11.14 — remain part of the audit frame for every client (Command Center included).
    /// </summary>
    [Fact]
    public void IntelligenceEventProjection_Warded_CarriesTheToolArgumentsOntoTheFrame()
    {
        ApprovalRequested approval = new(
            Correlation(1),
            "ward-7",
            "execute_command",
            """{"command":"rm -rf build","_arcanumRiskDisclosure":"Runs a shell command."}""");

        IntelligenceEvent frame = Assert.Single(IntelligenceEventProjection.Map(approval));
        Assert.Equal(IntelligenceEventType.Warded, frame.Type);
        Assert.Equal("ward-7", frame.WardId);
        Assert.Equal("execute_command", frame.WardToolName);

        JsonElement arguments = Assert.IsType<JsonElement>(frame.WardArguments);
        Assert.Equal("rm -rf build", arguments.GetProperty("command").GetString());
        Assert.Equal(
            "Runs a shell command.",
            arguments.GetProperty("_arcanumRiskDisclosure").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    public void IntelligenceEventProjection_Warded_OmitsUnusableArgumentsRatherThanFailing(string argumentsJson)
    {
        IntelligenceEvent frame = Assert.Single(
            IntelligenceEventProjection.Map(
                new ApprovalRequested(Correlation(1), "ward-8", "workspace_check", argumentsJson)));

        Assert.Equal(IntelligenceEventType.Warded, frame.Type);
        Assert.Null(frame.WardArguments);
    }

    [Fact]
    public void IntelligenceEventProjection_NonTransportSemanticEvents_AreFiltered()
    {
        Error error = new(ErrorCodes.Hub.Error, "detail");
        TurnEvent[] filtered =
        [
            new RunStarted(Correlation(1)),
            new ProviderAttemptStarted(Correlation(2), "provider", "model"),
            new ProviderSelected(Correlation(3), "provider", "model"),
            new ProviderAttemptCommitted(Correlation(4)),
            new ProviderAttemptCompleted(Correlation(5)),
            new ProviderAttemptFailed(Correlation(6), error, IsConnectivityFailure: true),
            new ModelCallStarted(Correlation(7), ModelCallPurpose.MainInference),
            new ModelCallCompleted(Correlation(8), Usage: null),
            new ModelCallFailed(Correlation(9), error, IsConnectivityFailure: false),
            new HumanInputReceived(Correlation(10), "call-human", "yes"),
            new ToolInvocationStarted(Correlation(11), "call-tool", "tool"),
            new OutputValidated(Correlation(12), Passed: true, Warnings: []),
        ];

        foreach (TurnEvent evt in filtered)
        {
            Assert.Empty(IntelligenceEventProjection.Map(evt));
        }
    }

    [Fact]
    public void ProjectionConstructors_RejectNullWriters()
    {
        Assert.Throws<ArgumentNullException>(() => new IntelligenceEventProjection(null!));
    }

    private static TurnEventCorrelation Correlation(long sequence) =>
        new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            sequence,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence));

    private static ContextTokenBreakdown Breakdown() =>
        new()
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
            InputTokens = 100,
            ReservedTokens = 32,
            TotalTokens = 132,
            OverallClassification = TokenEstimateClassification.Estimated,
            SafetyMarginTokens = 10,
        };
}
