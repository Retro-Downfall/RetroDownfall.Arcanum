namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>Conservative information-flow join, independent of read or effect authority.</summary>
public static class ProviderCallSensitivities
{
    public static ProviderCallSensitivity Create(ContentSensitivity level, GenerationProvenance provenance) =>
        new(level, provenance, CovenantDigests.Sensitivity(new(level, provenance.Mode, provenance.ExactGenerationIds, provenance.BloomBits)));

    public static ProviderCallSensitivity Merge(ProviderCallSensitivity? left, ProviderCallSensitivity right)
    {
        ArgumentNullException.ThrowIfNull(right);

        return left is null ? right : Create(ContentSensitivityAlgebra.Maximum(left.Level, right.Level), left.Provenance.Merge(right.Provenance));
    }
}
