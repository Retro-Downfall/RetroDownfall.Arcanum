using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ProviderAttemptCommitTrackerTests
{

    [Fact]
    public void CommitsProviderAttempt_OnNonEmptyTextDelta()
    {
        ModelCallUpdate delta = new ModelCallTextDelta(
            ModelCallPurpose.MainInference,
            "call-1",
            "hello");

        Assert.True(ProviderAttemptCommitTracker.CommitsProviderAttempt(delta));
    }

    [Fact]
    public void CommitsProviderAttempt_RejectsEmptyTextDelta()
    {
        ModelCallUpdate delta = new ModelCallTextDelta(
            ModelCallPurpose.MainInference,
            "call-1",
            string.Empty);

        Assert.False(ProviderAttemptCommitTracker.CommitsProviderAttempt(delta));
    }

    [Theory]
    [InlineData("visible reasoning", false)]
    [InlineData("", true)]
    [InlineData("", false)]
    public void CommitsProviderAttempt_OnAnyExplicitReasoningContent(
        string visibleText,
        bool hasProtectedData)
    {
        ModelCallUpdate reasoning = new ModelCallReasoningUpdate(
            ModelCallPurpose.MainInference,
            "call-1",
            visibleText,
            RequestedOutput: ReasoningOutputMode.Summary,
            EffectiveOutput: ReasoningOutputMode.Summary,
            HasProtectedData: hasProtectedData);

        Assert.True(ProviderAttemptCommitTracker.CommitsProviderAttempt(reasoning));
    }

    [Fact]
    public void CommitsProviderAttempt_OnBufferedProtectedReasoningMetadata()
    {
        ModelCallReasoningResult reasoning = new(
            Segments: [],
            RequestedOutput: null,
            EffectiveOutput: ReasoningOutputMode.None,
            HasProviderContent: true,
            HasProtectedData: true);

        Assert.True(ProviderAttemptCommitTracker.CommitsProviderAttempt(reasoning));
    }

    [Fact]
    public void CommitsProviderAttempt_RejectsBufferedMetadataWithoutReasoningContent()
    {
        ModelCallReasoningResult reasoning = new(
            Segments: [],
            RequestedOutput: null,
            EffectiveOutput: ReasoningOutputMode.None,
            HasProviderContent: false,
            HasProtectedData: false);

        Assert.False(ProviderAttemptCommitTracker.CommitsProviderAttempt(reasoning));
    }

    [Fact]
    public void CommitsOnCompleteToolProposal_RequiresActionableCalls()
    {
        Assert.True(ProviderAttemptCommitTracker.CommitsOnCompleteToolProposal(hasActionableToolCalls: true));

        Assert.False(ProviderAttemptCommitTracker.CommitsOnCompleteToolProposal(hasActionableToolCalls: false));
    }

    [Fact]
    public void CommitsOnEmptySuccessfulRound_OnlyWhenNoTextOrTools()
    {
        Assert.True(ProviderAttemptCommitTracker.CommitsOnEmptySuccessfulRound(hasText: false, hasToolCalls: false));

        Assert.False(ProviderAttemptCommitTracker.CommitsOnEmptySuccessfulRound(hasText: true, hasToolCalls: false));

        Assert.False(ProviderAttemptCommitTracker.CommitsOnEmptySuccessfulRound(hasText: false, hasToolCalls: true));
    }

}
