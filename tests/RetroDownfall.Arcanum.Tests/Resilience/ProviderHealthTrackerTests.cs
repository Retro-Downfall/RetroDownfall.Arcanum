using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Infrastructure.Resilience;

namespace RetroDownfall.Arcanum.Tests.Resilience;

public sealed class ProviderHealthTrackerTests
{

    private static IProviderHealthTracker CreateTracker()
    {
        return new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance);
    }

    private static int FailureThreshold =>
        ArcanumSettingClamps.HealthFailureThreshold(
            ArcanumRuntimeDefaults.Resilience.HealthFailureThreshold);

    [Fact]
    public void IsHealthy_returns_true_for_unknown_provider()
    {

        IProviderHealthTracker tracker = CreateTracker();

        Assert.True(tracker.IsHealthy("never-seen"));

    }

    [Fact]
    public void MarkFailed_increments_consecutive_failures()
    {

        IProviderHealthTracker tracker = CreateTracker();

        tracker.MarkFailed("ollama");

        ProviderHealthStatus status = Assert.Single(tracker.GetAllStatuses());

        Assert.Equal("ollama", status.ProviderName);

        Assert.Equal(1, status.ConsecutiveFailures);

    }

    [Fact]
    public void IsHealthy_returns_false_after_threshold()
    {

        IProviderHealthTracker tracker = CreateTracker();

        for (int i = 0; i < FailureThreshold; i++)
        {
            tracker.MarkFailed("ollama");
        }

        Assert.False(tracker.IsHealthy("ollama"));

    }

    [Fact]
    public void MarkHealthy_resets_to_healthy()
    {

        IProviderHealthTracker tracker = CreateTracker();

        for (int i = 0; i < FailureThreshold; i++)
        {
            tracker.MarkFailed("ollama");
        }

        Assert.False(tracker.IsHealthy("ollama"));

        tracker.MarkHealthy("ollama");

        Assert.True(tracker.IsHealthy("ollama"));

        ProviderHealthStatus status = Assert.Single(tracker.GetAllStatuses());

        Assert.Equal(0, status.ConsecutiveFailures);

    }

    [Fact]
    public void MarkFailed_below_threshold_stays_healthy()
    {

        IProviderHealthTracker tracker = CreateTracker();

        for (int i = 0; i < FailureThreshold - 1; i++)
        {
            tracker.MarkFailed("ollama");
        }

        Assert.True(tracker.IsHealthy("ollama"));

    }

    [Fact]
    public void HealthChanged_fires_only_on_transition()
    {

        IProviderHealthTracker tracker = CreateTracker();

        List<ProviderHealthStatus> transitions = [];

        tracker.HealthChanged += transitions.Add;

        for (int i = 0; i < FailureThreshold - 1; i++)
        {
            tracker.MarkFailed("ollama");
        }

        Assert.Empty(transitions);

        tracker.MarkFailed("ollama");

        ProviderHealthStatus unhealthy = Assert.Single(transitions);

        Assert.False(unhealthy.IsHealthy);

        tracker.MarkHealthy("ollama");

        Assert.Equal(2, transitions.Count);

        Assert.True(transitions[1].IsHealthy);

    }

}
