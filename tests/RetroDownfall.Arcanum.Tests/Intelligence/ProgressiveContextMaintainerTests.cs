namespace RetroDownfall.Arcanum.Tests.Intelligence;

public class ProgressiveContextMaintainerTests
{
    [Fact]
    public void Maintain_Progressive_Maintenance_Succeeds()
    {
        var compression = new Mock<IContextCompressionService>(MockBehavior.Strict);
        compression.Setup(c => c.CountTokens(It.IsAny<System.Collections.Generic.IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>(), null, null, null, 0, 0)).Returns(10);
        compression.Setup(c => c.ComputeEffectiveLimit(It.IsAny<int>(), It.IsAny<int>())).Returns(100);

        var estimator = new Mock<RetroDownfall.Arcanum.Core.Intelligence.IModelTokenEstimator>(MockBehavior.Strict);

        var maintainer = new RetroDownfall.Arcanum.Api.Intelligence.ProgressiveContextMaintainer(
            compression.Object,
            estimator.Object);

        var result = maintainer.Maintain(
            new System.Collections.Generic.List<Microsoft.Extensions.AI.ChatMessage>(),
            new RetroDownfall.Arcanum.Core.Intelligence.ModelTokenizationContracts.ContextTokenBreakdown
            {
                Provider = "test", Model = "test",
                Profile = new RetroDownfall.Arcanum.Core.Intelligence.ResolvedModelTokenizationProfile(),
                Components = new System.Collections.ObjectModel.ReadOnlyCollection<RetroDownfall.Arcanum.Core.Intelligence.ContextTokenComponent>(new List<RetroDownfall.Arcanum.Core.Intelligence.ContextTokenComponent>()),
                InputTokens = 50, ReservedTokens = 10, TotalTokens = 60,
                OverallClassification = RetroDownfall.Arcanum.Core.Intelligence.TokenEstimateClassification.Estimated,
                SafetyMarginTokens = 5
            },
            new RetroDownfall.Arcanum.Api.Intelligence.ContextMaintenanceContext(
                null, new List<string>(), new List<string>(), 1024, 512));

        Assert.True(result.Success);
    }
}
