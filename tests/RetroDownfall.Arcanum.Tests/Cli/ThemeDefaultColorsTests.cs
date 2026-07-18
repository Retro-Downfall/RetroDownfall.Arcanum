using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ThemeDefaultColorsTests
{
    [Fact]
    public void Light_heading_default_is_royal_blue()
    {
        Assert.Equal("#1E3A8A", new ThemeSemanticColors().Heading);
    }

    [Fact]
    public void Dark_heading_default_is_soft_blue()
    {
        Assert.Equal("#60A5FA", new ThemeColors().Dark.Heading);
    }

    [Fact]
    public void Error_defaults_remain_red()
    {
        Assert.Equal("#C41E3A", new ThemeSemanticColors().Error);
        Assert.Equal("#FF6B6B", new ThemeColors().Dark.Error);
    }
}
