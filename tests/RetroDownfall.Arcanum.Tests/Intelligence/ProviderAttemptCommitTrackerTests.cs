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
