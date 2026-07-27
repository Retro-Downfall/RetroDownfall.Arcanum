using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class A2AConfigurationTests
{

    [Fact]
    public void ConclaveA2ASettings_defaults_are_disabled_and_conservative()
    {

        ConclaveA2ASettings a2a = ArcanumRuntimeDefaults.Conclave.A2A;

        Assert.False(a2a.Enabled);

        Assert.False(a2a.ServerEnabled);

        Assert.False(a2a.ClientEnabled);

        Assert.Equal("/api/conclave/a2a", a2a.ServerPath);

        Assert.Equal(50, new ArcanumSettings().Execution.MaxConcurrentA2ATasks);

        Assert.Empty(a2a.AllowedRemoteAgents);

        Assert.Equal(string.Empty, a2a.DefaultWorkspace);

        Assert.Null(a2a.AgentCardName);

        Assert.Null(a2a.AgentCardDescription);

    }

    [Fact]
    public void ConclaveSettings_A2A_defaults_to_a_new_disabled_block()
    {

        ConclaveSettings conclave = ArcanumRuntimeDefaults.Conclave;

        Assert.False(conclave.Enabled);

        Assert.NotNull(conclave.A2A);

        Assert.False(conclave.A2A.Enabled);

    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    [InlineData(-5, 1)]
    public void MaxConcurrentA2ATasks_clamps_to_1_500(int input, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.MaxConcurrentA2ATasks(input));

    }

    [Theory]
    [InlineData(nameof(FeatureSettings.A2AServer))]
    [InlineData(nameof(FeatureSettings.A2AClient))]
    public void A2A_feature_implies_Conclave_availability(string featureName)
    {

        FeatureSettings features = featureName switch
        {
            nameof(FeatureSettings.A2AServer) => new FeatureSettings { A2AServer = true },
            nameof(FeatureSettings.A2AClient) => new FeatureSettings { A2AClient = true },
            _ => throw new ArgumentOutOfRangeException(nameof(featureName)),
        };

        ArcanumSettings settings = new()
        {
            Features = features,
        };

        ConclaveSettings conclave = settings.ResolveConclave();

        Assert.True(conclave.Enabled);

        Assert.True(conclave.A2A.Enabled);

        Assert.Equal(features.A2AServer, conclave.A2A.ServerEnabled);

        Assert.Equal(features.A2AClient, conclave.A2A.ClientEnabled);

    }

    [Fact]
    public void Conclave_feature_can_enable_Conclave_without_A2A()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Conclave = true },
        };

        ConclaveSettings conclave = settings.ResolveConclave();

        Assert.True(conclave.Enabled);

        Assert.False(conclave.A2A.Enabled);

        Assert.False(conclave.A2A.ServerEnabled);

        Assert.False(conclave.A2A.ClientEnabled);

    }

}
