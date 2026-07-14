using RetroDownfall.TheForge.Core.Models;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class TheForgeSettingsTests
{

    [Fact]
    public void TheForgeSettings_Defaults_AreExpected()
    {

        TheForgeSettings settings = new();

        Assert.Equal("http://localhost:5001", settings.BaseUrl);

        Assert.Null(settings.ApiKey);

        Assert.Equal("dark", settings.Theme);

        Assert.Null(settings.LastCampaignId);

        Assert.Null(settings.LayoutState);

        Assert.True(settings.AutoConnect);

        Assert.Null(settings.ActiveSessionId);

    }

}
