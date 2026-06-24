using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ArcanumSettingClampsTests
{

    [Fact]
    public void EffectiveInProcessToolOutputCapBytes_respects_json_rpc_margin()
    {

        long cap = ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
            toolOutputCapBytes: 1_048_576,
            maxJsonRpcLineBytes: 2_228_224);

        Assert.True(cap >= 1_048_576);

    }

    [Fact]
    public void EffectiveInProcessToolOutputCapBytes_clamps_to_line_budget()
    {

        long cap = ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
            toolOutputCapBytes: 8_388_608,
            maxJsonRpcLineBytes: 131_072);

        Assert.True(cap < 8_388_608);

    }

}
