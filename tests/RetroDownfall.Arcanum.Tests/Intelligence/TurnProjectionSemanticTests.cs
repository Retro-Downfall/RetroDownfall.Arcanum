using System.Threading.Channels;
using System.Text.Json;

using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
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
                    TurnTerminationReason.ClientDisconnected,
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
    public void AttachmentRefreshed_ProjectsToNativeNdjson_AndOpenAiIgnoresIt()
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

        Channel<OpenAiChatChunk> channel = Channel.CreateUnbounded<OpenAiChatChunk>();
        OpenAiSseProjection openAi = new(channel.Writer, "chatcmpl-refresh", "model", 1);
        Assert.Empty(openAi.Map(refreshed));
    }

    /// <summary>
    /// A Ward asks the operator to approve a Forbidden Art. The tool name alone is not informed
    /// consent — the arguments are what says which command runs against which path, and they carry
    /// the <c>_arcanumRiskDisclosure</c> DESIGN §11.14 mandates. Dropping them from the wire frame
    /// leaves every client (Command Center included) approving blind.
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
    public void OpenAiSseProjection_NonOpenAiSemanticEvents_AreFiltered()
    {
        Channel<OpenAiChatChunk> channel = Channel.CreateUnbounded<OpenAiChatChunk>();
        OpenAiSseProjection projection = new(
            channel.Writer,
            "chatcmpl-filter",
            "model",
            createdUnixSeconds: 1);
        Error error = new(ErrorCodes.Hub.Error, "detail");
        TurnEvent[] filtered =
        [
            new RunStarted(Correlation(1)),
            new TurnStatusChanged(Correlation(2), "working"),
            new SessionBound(Correlation(3), Guid.NewGuid()),
            new ContextCompressed(Correlation(4), "compressed"),
            new ContextAccounted(Correlation(4), Breakdown()),
            new ProviderAttemptStarted(Correlation(5), "provider", "model"),
            new ProviderSelected(Correlation(6), "provider", "model"),
            new ProviderAttemptCommitted(Correlation(7)),
            new ProviderAttemptCompleted(Correlation(8)),
            new ProviderAttemptFailed(Correlation(9), error, IsConnectivityFailure: true),
            new ModelCallStarted(Correlation(10), ModelCallPurpose.MainInference),
            new ModelCallCompleted(Correlation(11), Usage: null),
            new ModelCallFailed(Correlation(12), error, IsConnectivityFailure: false),
            new ApprovalRequested(Correlation(13), "ward", "tool", "{}"),
            new ApprovalResolved(Correlation(14), "ward", "tool", Allowed: false, Reason: "no"),
            new HumanInputRequested(Correlation(15), "call-human", "choose"),
            new HumanInputReceived(Correlation(16), "call-human", "yes"),
            new ToolInvocationStarted(Correlation(17), "call-tool", "tool"),
            new ToolInvocationCompleted(
                Correlation(18),
                "call-tool",
                "tool",
                "{}",
                "result",
                Failed: false,
                Denied: false,
                ToleratedFailure: false,
                PublicErrorText: null,
                Duration: TimeSpan.Zero,
                AttachmentPostProcessed: false),
            new OutputValidated(Correlation(19), Passed: true, Warnings: []),
        ];

        foreach (TurnEvent evt in filtered)
        {
            Assert.Empty(projection.Map(evt));
        }
    }

    [Fact]
    public async Task OpenAiSseProjection_ApplyAsync_FiltersNonTerminalAndCompletesOnFailure()
    {
        Channel<OpenAiChatChunk> channel = Channel.CreateUnbounded<OpenAiChatChunk>();
        OpenAiSseProjection projection = new(
            channel.Writer,
            "chatcmpl-apply",
            "model",
            createdUnixSeconds: 1);

        await projection.ApplyAsync(new TurnStatusChanged(Correlation(1), "working"));

        Assert.False(channel.Reader.TryRead(out _));
        Assert.False(channel.Reader.Completion.IsCompleted);

        await projection.ApplyAsync(
            new RunFailed(
                Correlation(2),
                new Error(ErrorCodes.Hub.Error, "failed"),
                TurnTerminationReason.ProviderFailure,
                Usage: null,
                Warnings: [],
                Interrupted: false,
                PartialText: null));

        OpenAiChatChunk chunk = await channel.Reader.ReadAsync();
        Assert.Equal("error", Assert.Single(chunk.Choices).FinishReason);
        Assert.Equal("inference_failed", chunk.Error?.Code);
        await channel.Reader.Completion;
    }

    [Fact]
    public void ProjectionConstructors_RejectNullWriters()
    {
        Assert.Throws<ArgumentNullException>(() => new IntelligenceEventProjection(null!));
        Assert.Throws<ArgumentNullException>(
            () => new OpenAiSseProjection(null!, "chatcmpl-test", "model"));
    }

    [Fact]
    public void OpenAiSseProjection_DefaultMetadata_IsGeneratedForMissingInputs()
    {
        Channel<OpenAiChatChunk> channel = Channel.CreateUnbounded<OpenAiChatChunk>();
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        OpenAiSseProjection projection = new(
            channel.Writer,
            completionId: " ",
            model: null!,
            createdUnixSeconds: null);

        OpenAiChatChunk chunk = Assert.Single(
            projection.Map(new TextDelta(Correlation(1), "text")));

        Assert.StartsWith("chatcmpl-", chunk.Id, StringComparison.Ordinal);
        Assert.Equal(string.Empty, chunk.Model);
        Assert.InRange(chunk.Created, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static TurnEventCorrelation Correlation(long sequence) =>
        new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            sequence,
            ProviderAttempt: 1,
            ModelRound: 1,
            ModelCallId: "model-call",
            ToolCallId: null,
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
