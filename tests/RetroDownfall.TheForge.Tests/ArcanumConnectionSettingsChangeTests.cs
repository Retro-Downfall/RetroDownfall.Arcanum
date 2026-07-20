using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class ArcanumConnectionSettingsChangeTests
{

    [Fact]
    public void ConnectionSettingsUnchanged_IgnoresLayoutThemeCampaignAndApiKeyPatches()
    {

        TheForgeSettings previous = new()
        {
            BaseUrl = "http://127.0.0.1:5001",
            ApiKey = "secret",
            AutoConnect = true,
            Theme = "light",
            LayoutState = null,
            LastCampaignId = null,
        };

        TheForgeSettings patched = previous with
        {
            Theme = "dark",
            LayoutState = "{\"version\":1}",
            LastCampaignId = Guid.NewGuid(),
            ApiKey = null, // keychain migration strips plaintext
        };

        Assert.True(ArcanumConnectionService.ConnectionSettingsUnchanged(previous, patched));

    }

    [Fact]
    public void ConnectionSettingsUnchanged_DetectsBaseUrlAndAutoConnect()
    {

        TheForgeSettings previous = new()
        {
            BaseUrl = "http://127.0.0.1:5001",
            ApiKey = "secret",
            AutoConnect = true,
        };

        Assert.False(
            ArcanumConnectionService.ConnectionSettingsUnchanged(
                previous,
                previous with { BaseUrl = "http://127.0.0.1:5002" }));

        Assert.False(
            ArcanumConnectionService.ConnectionSettingsUnchanged(
                previous,
                previous with { AutoConnect = false }));

    }

    [Theory]
    [InlineData("Security.MissingApiKey", true)]
    [InlineData("Auth.Unauthorized", true)]
    [InlineData("Connection.Failed", false)]
    [InlineData("Health.Unhealthy", false)]
    public void IsImmediateError_OnlyAuthCodes(string code, bool expected) =>
        Assert.Equal(expected, ArcanumConnectionService.IsImmediateError(code));

}
