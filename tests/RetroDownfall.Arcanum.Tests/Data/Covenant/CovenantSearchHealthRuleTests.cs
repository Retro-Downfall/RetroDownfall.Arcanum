using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The one derivation both the status route and a search page apply to the facts they hold.
/// </summary>
public sealed class CovenantSearchHealthRuleTests
{
    [Theory]
    [InlineData(true, false, false, false, CovenantSearchRebuildGuidance.AcceleratorUnavailable)]
    [InlineData(true, false, false, true, CovenantSearchRebuildGuidance.AcceleratorUnavailable)]
    [InlineData(true, false, true, false, CovenantSearchRebuildGuidance.AcceleratorUnavailable)]
    [InlineData(true, false, true, true, CovenantSearchRebuildGuidance.AcceleratorUnavailable)]
    [InlineData(true, true, false, false, CovenantSearchRebuildGuidance.AcceleratorUnavailable)]
    [InlineData(true, true, false, true, CovenantSearchRebuildGuidance.AcceleratorUnavailable)]
    [InlineData(true, true, true, false, CovenantSearchRebuildGuidance.AcceleratorUnavailable)]
    [InlineData(true, true, true, true, CovenantSearchRebuildGuidance.AcceleratorUnavailable)]
    [InlineData(false, true, false, false, CovenantSearchRebuildGuidance.None)]
    [InlineData(false, true, false, true, CovenantSearchRebuildGuidance.None)]
    [InlineData(false, true, true, false, CovenantSearchRebuildGuidance.None)]
    [InlineData(false, true, true, true, CovenantSearchRebuildGuidance.None)]
    [InlineData(false, false, true, false, CovenantSearchRebuildGuidance.WaitForSynchronization)]
    [InlineData(false, false, true, true, CovenantSearchRebuildGuidance.WaitForSynchronization)]
    [InlineData(false, false, false, false, CovenantSearchRebuildGuidance.WaitForSynchronization)]
    [InlineData(false, false, false, true, CovenantSearchRebuildGuidance.RebuildRequired)]
    public void Guidance_follows_the_one_rule_for_every_combination_of_facts(
        bool acceleratorUnavailable,
        bool synchronized,
        bool outboxCanContinue,
        bool rebuildOwed,
        CovenantSearchRebuildGuidance expected) =>
        Assert.Equal(
            expected,
            CovenantSearchHealthRule.Guidance(acceleratorUnavailable, synchronized, outboxCanContinue, rebuildOwed));

    [Theory]
    [InlineData(true, false, false, CovenantSearchHealthState.Unavailable)]
    [InlineData(true, false, true, CovenantSearchHealthState.Unavailable)]
    [InlineData(true, true, false, CovenantSearchHealthState.Unavailable)]
    [InlineData(true, true, true, CovenantSearchHealthState.Unavailable)]
    [InlineData(false, true, false, CovenantSearchHealthState.Degraded)]
    [InlineData(false, true, true, CovenantSearchHealthState.Degraded)]
    [InlineData(false, false, true, CovenantSearchHealthState.Healthy)]
    [InlineData(false, false, false, CovenantSearchHealthState.Synchronizing)]
    public void State_follows_the_one_rule_for_every_combination_of_facts(
        bool acceleratorUnavailable,
        bool acceleratorDegraded,
        bool synchronized,
        CovenantSearchHealthState expected) =>
        Assert.Equal(
            expected,
            CovenantSearchHealthRule.State(acceleratorUnavailable, acceleratorDegraded, synchronized));
}
