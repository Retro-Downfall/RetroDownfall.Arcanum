using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class PromptCachePolicyV2Tests
{
    [Theory]
    [InlineData(PromptCacheSourceClass.CodeOwnedFraming)]
    [InlineData(PromptCacheSourceClass.CodeOwnedFormatting)]
    public void IsEligible_AdmitsOnlyInvariantNonsensitiveCodeOwnedSources(PromptCacheSourceClass sourceClass)
    {
        Assert.True(PromptCachePolicyV2.IsEligible(sourceClass, stable: true, sensitive: false));

        Assert.False(PromptCachePolicyV2.IsEligible(sourceClass, stable: false, sensitive: false));

        Assert.False(PromptCachePolicyV2.IsEligible(sourceClass, stable: true, sensitive: true));

        Assert.False(PromptCachePolicyV2.IsEligible(sourceClass, stable: false, sensitive: true));
    }

    [Theory]
    [InlineData(PromptCacheSourceClass.Unclassified)]
    [InlineData(PromptCacheSourceClass.Protected)]
    [InlineData(PromptCacheSourceClass.Dynamic)]
    [InlineData((PromptCacheSourceClass)(-1))]
    [InlineData((PromptCacheSourceClass)int.MaxValue)]
    public void IsEligible_StableFlagCannotPromoteUnknownProtectedOrDynamicSources(PromptCacheSourceClass sourceClass)
    {
        Assert.False(PromptCachePolicyV2.IsEligible(sourceClass, stable: true, sensitive: false));

        Assert.False(PromptCachePolicyV2.IsEligible(sourceClass, stable: false, sensitive: false));

        Assert.False(PromptCachePolicyV2.IsEligible(sourceClass, stable: true, sensitive: true));
    }

    [Theory]
    [InlineData(PromptSegmentKind.Preamble, PromptCacheSourceClass.CodeOwnedFraming)]
    [InlineData(PromptSegmentKind.ContextHeader, PromptCacheSourceClass.CodeOwnedFraming)]
    [InlineData(PromptSegmentKind.DataHeader, PromptCacheSourceClass.CodeOwnedFraming)]
    [InlineData(PromptSegmentKind.InstructionsHeader, PromptCacheSourceClass.CodeOwnedFraming)]
    [InlineData(PromptSegmentKind.ContextPlaceholder, PromptCacheSourceClass.CodeOwnedFraming)]
    [InlineData(PromptSegmentKind.InstructionsPlaceholder, PromptCacheSourceClass.CodeOwnedFraming)]
    [InlineData(PromptSegmentKind.TerminalFormatting, PromptCacheSourceClass.CodeOwnedFormatting)]
    [InlineData(PromptSegmentKind.CovenantProposed, PromptCacheSourceClass.Protected)]
    [InlineData(PromptSegmentKind.CovenantGlobalConfirmed, PromptCacheSourceClass.Protected)]
    [InlineData(PromptSegmentKind.CovenantCampaignConfirmed, PromptCacheSourceClass.Protected)]
    [InlineData(PromptSegmentKind.Data, PromptCacheSourceClass.Dynamic)]
    [InlineData(PromptSegmentKind.WorkspaceContext, PromptCacheSourceClass.Dynamic)]
    [InlineData(PromptSegmentKind.CampaignSummary, PromptCacheSourceClass.Dynamic)]
    [InlineData(PromptSegmentKind.CampaignRollup, PromptCacheSourceClass.Dynamic)]
    [InlineData(PromptSegmentKind.SessionRollup, PromptCacheSourceClass.Dynamic)]
    [InlineData(PromptSegmentKind.AdditionalInstructions, PromptCacheSourceClass.Dynamic)]
    [InlineData(PromptSegmentKind.Codex, PromptCacheSourceClass.Unclassified)]
    [InlineData(PromptSegmentKind.PrimarySpell, PromptCacheSourceClass.Unclassified)]
    [InlineData(PromptSegmentKind.ResonantSpells, PromptCacheSourceClass.Unclassified)]
    [InlineData((PromptSegmentKind)int.MaxValue, PromptCacheSourceClass.Unclassified)]
    public void BuildResult_ProjectsOwnerKindWithoutTreatingStabilityAsProof(
        PromptSegmentKind kind,
        PromptCacheSourceClass expected)
    {
        SystemPromptBuildResult build = new SystemPromptDocument(
            [new PromptSegment(kind, PromptSegmentStability.Stable, "same arbitrary text", true)])
            .BuildResult();

        Assert.Equal(expected, Assert.Single(build.CacheSegments).SourceClass);

        Assert.Equal("same arbitrary text", build.Prompt);
    }
}
