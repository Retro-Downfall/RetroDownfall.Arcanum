using System.Reflection;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class ApiBootstrapperMetricsAuthTests : IDisposable
{

    private readonly string? _originalHostAny;

    public ApiBootstrapperMetricsAuthTests()
    {

        _originalHostAny = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", null);

    }

    public void Dispose()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", _originalHostAny);

    }

    [Fact]
    public void MetricsSecurityAndFeatureSettings_DefaultToEnabled()
    {

        Assert.True(new SecuritySettings().MetricsRequireApiKey);

        Assert.True(new FeatureSettings().Metrics);

    }

    [Fact]
    public void IsMetricsRequireApiKeyEffective_UnsetConfig_DefaultsToTrue()
    {

        IConfiguration configuration = new ConfigurationBuilder().Build();

        Assert.True(InvokeIsMetricsRequireApiKeyEffective(configuration));

    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void IsMetricsRequireApiKeyEffective_Loopback_HonorsConfiguredValue(string configured, bool expected)
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Security:MetricsRequireApiKey"] = configured,
                ["Arcanum:Host:ListenAny"] = "false",
            })
            .Build();

        Assert.Equal(expected, InvokeIsMetricsRequireApiKeyEffective(configuration));

    }

    [Fact]
    public void IsMetricsRequireApiKeyEffective_ListenAny_ForcesTrueEvenWhenConfiguredFalse()
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Security:MetricsRequireApiKey"] = "false",
                ["Arcanum:Host:ListenAny"] = "true",
            })
            .Build();

        Assert.True(InvokeIsMetricsRequireApiKeyEffective(configuration));

    }

    [Fact]
    public void IsMetricsRequireApiKeyEffective_HostAnyEnv_ForcesTrueEvenWhenConfiguredFalse()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", "1");

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Security:MetricsRequireApiKey"] = "false",
                ["Arcanum:Host:ListenAny"] = "false",
            })
            .Build();

        Assert.True(InvokeIsMetricsRequireApiKeyEffective(configuration));

    }

    [Fact]
    public void IsMetricsRequireApiKeyEffective_UnrecognizedValue_DefaultsToTrue()
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Security:MetricsRequireApiKey"] = "maybe",
            })
            .Build();

        Assert.True(InvokeIsMetricsRequireApiKeyEffective(configuration));

    }

    private static bool InvokeIsMetricsRequireApiKeyEffective(IConfiguration configuration)
    {

        MethodInfo? method = typeof(ApiBootstrapper).GetMethod(
            "IsMetricsRequireApiKeyEffective",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        object? result = method.Invoke(null, [configuration]);

        return Assert.IsType<bool>(result);

    }

}
