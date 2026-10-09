using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>Independent literal additions; the original 249-vector v1 corpus remains unchanged.</summary>
public sealed class CampaignRollupPolicyV1VectorTests
{
    [Theory]
    [InlineData(CovenantMaintenanceStep.CampaignRollup, "D743F76FB6082DD10240D3CB92CDEA94AA4DC71E3B7AA05C2D21BF841B641E2D")]
    [InlineData(CovenantMaintenanceStep.CampaignContribution, "C7F5AAF1DC41678BACEFC323A7391DC45C5FF74353BCA2A63FBE9A16B9C2EC6D")]
    public void Appended_maintenance_codes_have_fixed_v1_bytes(CovenantMaintenanceStep step, string expected) =>
        Assert.Equal(expected, Convert.ToHexString(CovenantDigests.MaintenanceDispatchEffect(new(G(1), step, 2, D(1), D(2))).Bytes));

    [Theory]
    [InlineData(SensitiveArtifactKind.CampaignRollup, "C3C22176EBAD037E23510A4216B95F932485CB0941A60E3171862482A3EC3054")]
    [InlineData(SensitiveArtifactKind.CampaignContribution, "EC38A062A7D3087F43258DEEE686C517B0C48CDFA66D76ED3384B39BA85D46BF")]
    public void Appended_artifact_codes_have_fixed_v1_bytes(SensitiveArtifactKind kind, string expected) =>
        Assert.Equal(expected, Convert.ToHexString(CovenantDigests.ArtifactLabel(new(kind, G(1),
            kind is SensitiveArtifactKind.CampaignContribution ? G(2) : null, G(3), G(4), 1, D(1), D(2), null, null, D(3))).Bytes));

    [Theory]
    [InlineData(CovenantPromptAttribution.CampaignRollup, "9E6D52EC5B6EB896C29125B63A3EDECE9EF52C7AA456707B064F3E6E4DC731FF")]
    [InlineData(CovenantPromptAttribution.SessionRollup, "DA7FE29B656CCA1EC7DCF13CBF0CD50262BCD1F9BA10568EDF64CF919E063BD4")]
    public void Appended_attribution_codes_have_fixed_v1_bytes(CovenantPromptAttribution attribution, string expected) =>
        Assert.Equal(expected, Convert.ToHexString(CovenantDigests.ProviderCall(new("p", "m", CovenantProviderDispatchMode.Buffered,
            "token", 4096, 0, D(1), D(2), [(byte)'x'],
            [new(attribution, 0, 1, new(SHA256.HashData("x"u8)))], D(3), [], [], null)).Bytes));

    private static CovenantDigest D(byte value) => new(Enumerable.Repeat(value, 32).ToArray());

    private static Guid G(int value) => Guid.Parse($"{value:x8}-{value:x4}-4{value:x3}-8{value:x3}-{value:x12}");
}
