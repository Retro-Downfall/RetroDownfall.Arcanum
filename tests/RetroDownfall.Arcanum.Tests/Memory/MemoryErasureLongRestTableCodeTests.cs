using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Memory;

public sealed class MemoryErasureLongRestTableCodeTests
{
    [Theory]
    [InlineData(SensitiveArtifactKind.Saga)]
    [InlineData(SensitiveArtifactKind.Lexicon)]
    public void Every_declared_memory_erasure_target_has_a_registered_effect_code(SensitiveArtifactKind kind)
    {
        CovenantArtifactPurgePlan plan = CovenantArtifactPurgePlans.Resolve(kind);

        CovenantArtifactPurgeTarget artifact = Assert.IsType<CovenantArtifactPurgeTarget>(plan.Artifact);

        Assert.All(plan.Projections.Append(artifact), target =>
            Assert.InRange(MemoryErasureTableCodes.For(target.Table), (byte)1, byte.MaxValue));
    }
}
