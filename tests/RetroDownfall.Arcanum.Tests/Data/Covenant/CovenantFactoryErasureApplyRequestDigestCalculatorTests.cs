using RetroDownfall.Arcanum.Core.DataLifecycle;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed class CovenantFactoryErasureApplyRequestDigestCalculatorTests
{

    [Fact]
    public void Compute_PinsTheHealthyCatalogFactoryErasureDomainAndPlanEncoding()
    {

        CovenantFactoryErasureApplyRequestDigestCalculator calculator = new();

        string digest = Convert.ToHexString(
            calculator
                .Compute(new CovenantFactoryErasureApplyRequestDigestInput("plan-128"))
                .Value
                .Bytes);

        Assert.Equal(
            "6E2DC4FEC155A79B032812B7FE6CAE90A7C6C5878F6132D125E8667D445C22CC",
            digest);

    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Compute_RefusesAPlanWithoutIdentity(string planId)
    {

        CovenantFactoryErasureApplyRequestDigestCalculator calculator = new();

        Assert.True(
            calculator
                .Compute(new CovenantFactoryErasureApplyRequestDigestInput(planId))
                .IsFailure);

    }

}
