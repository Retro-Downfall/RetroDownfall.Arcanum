using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.Infrastructure;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ServeOwnershipPolicyTests
{
    [Fact]
    public void OwnsHost_WhenThisProcessStartedTheHost_IsTrue()
    {
        Assert.True(ServeOwnershipPolicy.OwnsHost(Launch(ServeLaunchStatus.Started)));
    }

    [Fact]
    public void OwnsHost_WhenHostWasAlreadyRunning_IsFalse()
    {
        // The operator's own host must survive the Command Center closing.
        Assert.False(ServeOwnershipPolicy.OwnsHost(Launch(ServeLaunchStatus.AlreadyRunning)));
    }

    [Theory]
    [InlineData(ServeLaunchStatus.AuthFailed)]
    [InlineData(ServeLaunchStatus.LaunchDisabled)]
    [InlineData(ServeLaunchStatus.Failed)]
    public void OwnsHost_WhenNothingWasStarted_IsFalse(ServeLaunchStatus status)
    {
        Assert.False(ServeOwnershipPolicy.OwnsHost(Launch(status)));
    }

    [Fact]
    public void OwnsHost_WhenLaunchWasNeverAttempted_IsFalse()
    {
        Assert.False(ServeOwnershipPolicy.OwnsHost(null));
    }

    [Theory]
    [InlineData(ServeLaunchStatus.AlreadyRunning)]
    [InlineData(ServeLaunchStatus.Started)]
    [InlineData(ServeLaunchStatus.LaunchDisabled)]
    public void Caller_may_continue_only_when_launch_did_not_fail(
        ServeLaunchStatus status)
    {
        Assert.True(ServeOwnershipPolicy.CanProceed(Launch(status)));
    }

    [Theory]
    [InlineData(ServeLaunchStatus.AuthFailed, CliExitCode.ConfigurationError)]
    [InlineData(ServeLaunchStatus.Failed, CliExitCode.NetworkError)]
    public void Failed_launch_has_a_stable_cli_exit_code(
        ServeLaunchStatus status,
        CliExitCode expected)
    {
        Assert.False(ServeOwnershipPolicy.CanProceed(Launch(status)));
        Assert.Equal(expected, ServeOwnershipPolicy.FailureExitCode(Launch(status)));
    }

    private static ServeLaunchResult Launch(ServeLaunchStatus status) =>
        new(status, HealthProbeState.Healthy, TimeSpan.Zero, LogPath: null, Guidance: null);
}
