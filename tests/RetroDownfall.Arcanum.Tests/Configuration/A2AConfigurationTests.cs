using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class A2AConfigurationTests
{

    [Fact]
    public void ConclaveA2ASettings_defaults_are_disabled_and_conservative()
    {

        ConclaveA2ASettings a2a = new();

        Assert.False(a2a.Enabled);

        Assert.False(a2a.ServerEnabled);

        Assert.False(a2a.ClientEnabled);

        Assert.Equal("/api/conclave/a2a", a2a.ServerPath);

        Assert.Equal(50, a2a.MaxExternalTasks);

        Assert.Equal(60, a2a.ExternalTaskTimeoutMinutes);

        Assert.Empty(a2a.AllowedRemoteAgents);

        Assert.Equal(string.Empty, a2a.DefaultWorkspace);

        Assert.Null(a2a.AgentCardName);

        Assert.Null(a2a.AgentCardDescription);

    }

    [Fact]
    public void ConclaveSettings_A2A_defaults_to_a_new_disabled_block()
    {

        ConclaveSettings conclave = new();

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
    public void MaxExternalTasks_clamps_to_1_500(int input, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.MaxExternalTasks(input));

    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 5)]
    [InlineData(60, 60)]
    [InlineData(1_440, 1_440)]
    [InlineData(1_441, 1_440)]
    public void ExternalTaskTimeoutMinutes_clamps_to_5_1440(int input, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.ExternalTaskTimeoutMinutes(input));

    }

    [Fact]
    public void Both_Conclave_and_A2A_Enabled_are_required_for_the_surface_to_activate()
    {

        // Zero behavior change until an operator explicitly opts both flags in (constraint: disabled by default).
        ArcanumSettings conclaveOffOnly = new()
        {
            Conclave = new ConclaveSettings { Enabled = false, A2A = new ConclaveA2ASettings { Enabled = true, ServerEnabled = true, ClientEnabled = true } },
        };

        ArcanumSettings a2aOffOnly = new()
        {
            Conclave = new ConclaveSettings { Enabled = true, A2A = new ConclaveA2ASettings { Enabled = false, ServerEnabled = true, ClientEnabled = true } },
        };

        ArcanumSettings bothOn = new()
        {
            Conclave = new ConclaveSettings { Enabled = true, A2A = new ConclaveA2ASettings { Enabled = true, ServerEnabled = true, ClientEnabled = true } },
        };

        Assert.False(IsA2AServerActive(conclaveOffOnly));

        Assert.False(IsA2AServerActive(a2aOffOnly));

        Assert.True(IsA2AServerActive(bothOn));

    }

    private static bool IsA2AServerActive(ArcanumSettings settings) =>
        settings.Conclave.Enabled && settings.Conclave.A2A.Enabled && settings.Conclave.A2A.ServerEnabled;

}
