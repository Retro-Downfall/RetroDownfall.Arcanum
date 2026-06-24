using RetroDownfall.Arcanum.Core.Environment;

namespace RetroDownfall.Arcanum.Tests.Environment;

public sealed class ArcanumEnvironmentTests : IDisposable
{

    private readonly string? _originalHostAny;

    public ArcanumEnvironmentTests()
    {

        _originalHostAny = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

    }

    public void Dispose()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", _originalHostAny);

    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("TRUE", true)]
    [InlineData("FALSE", false)]
    public void IsHostAnyEnabled_EnvironmentOverride_WinsOverConfig(string envValue, bool expected)
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", envValue);

        bool result = ArcanumEnvironment.IsHostAnyEnabled(configValue: false);

        Assert.Equal(expected, result);

    }

    [Fact]
    public void IsHostAnyEnabled_UnrecognizedEnvValue_FallsBackToConfig()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", "maybe");

        Assert.True(ArcanumEnvironment.IsHostAnyEnabled(configValue: true));

        Assert.False(ArcanumEnvironment.IsHostAnyEnabled(configValue: false));

    }

    [Fact]
    public void IsHostAnyEnabled_UnsetEnv_UsesConfigValue()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", null);

        Assert.True(ArcanumEnvironment.IsHostAnyEnabled(configValue: true));

        Assert.False(ArcanumEnvironment.IsHostAnyEnabled(configValue: false));

    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void IsRateLimitEnabled_ConfigOrHostAny(bool rateLimitConfig, bool listenAnyConfig, bool expected)
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", null);

        bool result = ArcanumEnvironment.IsRateLimitEnabled(rateLimitConfig, listenAnyConfig);

        Assert.Equal(expected, result);

    }

    [Fact]
    public void IsRateLimitEnabled_HostAnyEnv_EnablesEvenWhenConfigDisabled()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", "1");

        bool result = ArcanumEnvironment.IsRateLimitEnabled(
            rateLimitConfigEnabled: false,
            listenAnyConfigValue: false);

        Assert.True(result);

    }

}
