using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

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

    /// <summary>
    /// A plan identity with an unpaired surrogate has no stable UTF-8 bytes. The default encoding
    /// would hash U+FFFD in its place, so two different malformed identities would share one digest.
    /// Built in the body: xunit's theory serialization replaces a lone surrogate with U+FFFD.
    /// </summary>
    [Fact]
    public void Compute_RefusesAPlanIdentityWithAnUnpairedSurrogateAsAnIntegrityFailure()
    {
        CovenantFactoryErasureApplyRequestDigestCalculator calculator = new();

        foreach (string planId in new[] { "plan-\uD800", "\uDC00-plan" })
        {
            Result<CovenantDigest> computed = calculator.Compute(
                new CovenantFactoryErasureApplyRequestDigestInput(planId));

            Assert.True(computed.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, computed.Error.Code);
        }
    }
}
