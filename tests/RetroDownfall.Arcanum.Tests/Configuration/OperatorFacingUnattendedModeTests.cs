using RetroDownfall.Arcanum.Core.Configuration;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class OperatorFacingUnattendedModeTests
{
    [Fact]
    public void WardSettings_UnattendedMode_defaults_to_false()
    {
        Assert.False(new WardSettings().UnattendedMode);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Resolve_cli_flag_or_host_setting(bool cliFlag, bool hostSetting, bool expected)
    {
        WardSettings ward = new() { UnattendedMode = hostSetting };

        Assert.Equal(expected, OperatorFacingUnattendedMode.Resolve(cliFlag, ward));
    }

    [Fact]
    public void Resolve_null_ward_uses_false_host_default()
    {
        Assert.False(OperatorFacingUnattendedMode.Resolve(cliUnattendedFlag: false, ward: null));
        Assert.True(OperatorFacingUnattendedMode.Resolve(cliUnattendedFlag: true, ward: null));
    }
}
