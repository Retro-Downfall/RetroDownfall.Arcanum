using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnEngineProjectionCharacterizationTests
{

    [Fact]
    public void OpenAiSseProjection_MapsTextDelta_OmitsWardAndToolResult()
    {
        List<OpenAiChatChunk> chunks = [];
        System.Threading.Channels.Channel<OpenAiChatChunk> channel =
            System.Threading.Channels.Channel.CreateUnbounded<OpenAiChatChunk>();

        OpenAiSseProjection projection = new(channel.Writer, "chatcmpl-test", "gpt-test", createdUnixSeconds: 1);

        TurnEventEmitter emitter = new(Guid.NewGuid());
        TurnEventCorrelation c = emitter.NextCorrelation();

        Assert.Single(projection.Map(new TextDelta(c, "hello")));
        Assert.Empty(projection.Map(new ApprovalRequested(c, "w1", "tool", "{}")));
        Assert.Empty(projection.Map(new ToolInvocationCompleted(
            c,
            "call1",
            "tool",
            "{}",
            "ok",
            Failed: false,
            Denied: false,
            ToleratedFailure: false,
            PublicErrorText: null,
            Duration: TimeSpan.Zero,
            AttachmentPostProcessed: false)));
    }

    [Fact]
    public void BufferedTurnProjection_UsesRunCompletedAsAuthority()
    {
        BufferedTurnProjection projection = new();
        TurnEventEmitter emitter = new(Guid.NewGuid());

        projection.Apply(new RunStarted(emitter.NextCorrelation()));
        projection.Apply(new TextDelta(emitter.NextCorrelation(), "ignored when RunCompleted present"));
        projection.Apply(new RunCompleted(
            emitter.NextCorrelation(),
            FinalText: "final",
            Usage: null,
            ToolCalls: null,
            FinishReason: "stop",
            Warnings: ["w"],
            SessionId: null,
            StructuredOutputWarning: true));

        Result<Core.Intelligence.Models.PromptTurnResult> result = projection.ToResult();

        Assert.True(result.IsSuccess);
        Assert.Equal("final", result.Value.Text);
        Assert.Equal("stop", result.Value.FinishReason);
        Assert.Contains("w", result.Value.Warnings);
    }

    [Fact]
    public void BufferedTurnProjection_RunFailed_SurfacesError()
    {
        BufferedTurnProjection projection = new();
        TurnEventEmitter emitter = new(Guid.NewGuid());

        projection.Apply(new RunFailed(
            emitter.NextCorrelation(),
            new Error(ErrorCodes.Hub.Error, "boom"),
            TurnTerminationReason.ProviderFailure,
            Usage: null,
            Warnings: [],
            Interrupted: false,
            PartialText: null));

        Result<Core.Intelligence.Models.PromptTurnResult> result = projection.ToResult();

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Hub.Error, result.Error.Code);
    }

    [Fact]
    public void PingRequest_HasNoIdempotencyKeyProperty()
    {
        // Forged bodies cannot set HasIdempotencyKey — it is not on the public wire request.
        Assert.Null(typeof(Core.Intelligence.PingRequest).GetProperty("HasIdempotencyKey"));
        Assert.Null(typeof(Core.Intelligence.PingRequest).GetProperty("IdempotencyKey"));
    }

}
