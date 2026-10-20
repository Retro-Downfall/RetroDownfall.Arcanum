using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class MemoryDelegationDispatchTests
{
    [Fact]
    public void Protected_tool_input_survives_later_empty_dispatch()
    {
        var scope = CovenantTurnScope.NotEligible();
        Guid generation = Guid.NewGuid();
        var provenance = GenerationProvenance.CreateExact([generation]);
        var sensitivity = new ProviderCallSensitivity(ContentSensitivity.CovenantDerived, provenance,
            CovenantDigests.Sensitivity(new(ContentSensitivity.CovenantDerived, provenance.Mode, provenance.ExactGenerationIds, provenance.BloomBits)));

        scope.RecordDerived(sensitivity);

        var next = CovenantDispatchGate.ResolveSensitivity(scope, CovenantDispatchPlan.Empty);

        Assert.Equal(ContentSensitivity.CovenantDerived, next.Level);
        Assert.Contains(generation, next.Provenance.ExactGenerationIds);
    }

    [Fact]
    public void Interrupted_or_empty_output_does_not_downgrade_prior_disclosure()
    {
        var scope = CovenantTurnScope.NotEligible();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        foreach (Guid generation in new[] { first, second })
        {
            var provenance = GenerationProvenance.CreateExact([generation]);
            scope.RecordDerived(new(ContentSensitivity.CovenantDerived, provenance,
                CovenantDigests.Sensitivity(new(ContentSensitivity.CovenantDerived, provenance.Mode, provenance.ExactGenerationIds, provenance.BloomBits))));
        }
        var clean = GenerationProvenance.CreateExact([]);
        scope.RecordDerived(new(ContentSensitivity.None, clean,
            CovenantDigests.Sensitivity(new(ContentSensitivity.None, clean.Mode, clean.ExactGenerationIds, clean.BloomBits))));

        Assert.Equal(ContentSensitivity.CovenantDerived, scope.DerivedSensitivity!.Level);
        Assert.Contains(first, scope.DerivedSensitivity.Provenance.ExactGenerationIds);
        Assert.Contains(second, scope.DerivedSensitivity.Provenance.ExactGenerationIds);
    }
}
