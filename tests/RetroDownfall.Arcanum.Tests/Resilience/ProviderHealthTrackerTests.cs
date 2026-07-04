using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Infrastructure.Resilience;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Resilience;

public sealed class ProviderHealthTrackerTests
{

    private static IProviderHealthTracker CreateTracker(int healthFailureThreshold = 3)
    {

        ArcanumSettings settings = new()
        {

            Resilience = new ResilienceSettings { HealthFailureThreshold = healthFailureThreshold },

        };

        return new ProviderHealthTracker(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<ProviderHealthTracker>.Instance);

    }

    [Fact]
    public void IsHealthy_returns_true_for_unknown_provider()
    {

        IProviderHealthTracker tracker = CreateTracker();

        Assert.True(tracker.IsHealthy("never-seen"));

    }

    [Fact]
    public void MarkFailed_increments_consecutive_failures()
    {

        IProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 3);

        tracker.MarkFailed("ollama");

        ProviderHealthStatus status = Assert.Single(tracker.GetAllStatuses());

        Assert.Equal("ollama", status.ProviderName);

        Assert.Equal(1, status.ConsecutiveFailures);

    }

    [Fact]
    public void IsHealthy_returns_false_after_threshold()
    {

        IProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 3);

        tracker.MarkFailed("ollama");

        tracker.MarkFailed("ollama");

        tracker.MarkFailed("ollama");

        Assert.False(tracker.IsHealthy("ollama"));

    }

    [Fact]
    public void MarkHealthy_resets_to_healthy()
    {

        IProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 3);

        tracker.MarkFailed("ollama");

        tracker.MarkFailed("ollama");

        tracker.MarkFailed("ollama");

        Assert.False(tracker.IsHealthy("ollama"));

        tracker.MarkHealthy("ollama");

        Assert.True(tracker.IsHealthy("ollama"));

        ProviderHealthStatus status = Assert.Single(tracker.GetAllStatuses());

        Assert.Equal(0, status.ConsecutiveFailures);

    }

    [Fact]
    public void MarkFailed_below_threshold_stays_healthy()
    {

        IProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 3);

        tracker.MarkFailed("ollama");

        tracker.MarkFailed("ollama");

        Assert.True(tracker.IsHealthy("ollama"));

    }

    [Fact]
    public void HealthChanged_fires_only_on_transition()
    {

        IProviderHealthTracker tracker = CreateTracker(healthFailureThreshold: 2);

        List<ProviderHealthStatus> transitions = [];

        tracker.HealthChanged += transitions.Add;

        tracker.MarkFailed("ollama");

        Assert.Empty(transitions);

        tracker.MarkFailed("ollama");

        ProviderHealthStatus unhealthy = Assert.Single(transitions);

        Assert.False(unhealthy.IsHealthy);

        tracker.MarkHealthy("ollama");

        Assert.Equal(2, transitions.Count);

        Assert.True(transitions[1].IsHealthy);

    }

}
