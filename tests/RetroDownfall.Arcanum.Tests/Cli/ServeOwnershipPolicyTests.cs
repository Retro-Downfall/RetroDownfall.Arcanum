using RetroDownfall.Arcanum.Cli.Services;

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

    private static ServeLaunchResult Launch(ServeLaunchStatus status) =>
        new(status, HealthProbeState.Healthy, TimeSpan.Zero, LogPath: null, Guidance: null);

}
