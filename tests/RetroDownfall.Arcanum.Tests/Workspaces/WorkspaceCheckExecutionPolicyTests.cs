using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

/// <summary>
/// The mandatory-jail probe spawns <c>/usr/bin/sandbox-exec</c>. A healthy result is cached for the
/// process lifetime so the <c>workspace_check</c> invocation path and the tools/list advertisement gate
/// do not pay that spawn on every call. A failed result is remembered only for a short window: a transient
/// failure must not disable the tool until restart, but a host whose probe keeps failing (or hanging to its
/// two-second timeout) must not re-spawn it on every status, tools/list and health call either. A disabled
/// tool never probes at all. Every test builds its own cache (with its own clock) or injects its own probe,
/// so none of them touches process-global state.
/// </summary>
public sealed class WorkspaceCheckExecutionPolicyTests
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

    [Fact]
    public void Successful_probe_is_cached_for_the_process_lifetime()
    {
        int probes = 0;

        FakeTimeProvider time = new();

        MandatoryJailProbeCache cache = new(
            () =>
            {
                probes++;

                return true;
            },
            RetryAfter,
            time);

        time.Advance(TimeSpan.FromDays(30));

        Assert.True(cache.IsAvailable());

        Assert.True(cache.IsAvailable());

        Assert.True(cache.IsAvailable());

        Assert.Equal(1, probes);

        Assert.Equal(1, cache.ProbeCount);
    }

    [Fact]
    public void Failed_probe_is_retried_once_the_retry_window_has_passed()
    {
        Queue<bool> results = new([false, false, true]);

        FakeTimeProvider time = new();

        MandatoryJailProbeCache cache = new(results.Dequeue, RetryAfter, time);

        Assert.False(cache.IsAvailable());

        time.Advance(RetryAfter);

        Assert.False(cache.IsAvailable());

        time.Advance(RetryAfter);

        Assert.True(cache.IsAvailable());

        Assert.True(cache.IsAvailable());

        Assert.Equal(3, cache.ProbeCount);
    }

    [Fact]
    public void Failed_probe_is_not_repeated_inside_the_retry_window()
    {
        int probes = 0;

        FakeTimeProvider time = new();

        MandatoryJailProbeCache cache = new(
            () =>
            {
                probes++;

                return false;
            },
            RetryAfter,
            time);

        // A hung or persistently failing sandbox-exec costs up to its two-second timeout per probe, while
        // every GetStatus, tools/list and health call asks. Inside the window they all get the remembered
        // answer instead of each spawning (and each queueing behind the previous spawn).
        for (int call = 0; call < 50; call++)
        {
            Assert.False(cache.IsAvailable());

            time.Advance(TimeSpan.FromMilliseconds(50));
        }

        Assert.Equal(1, probes);

        Assert.Equal(1, cache.ProbeCount);

        time.Advance(RetryAfter);

        Assert.False(cache.IsAvailable());

        Assert.Equal(2, probes);
    }

    [Fact]
    public void Reset_clears_the_cached_result_the_failure_window_and_the_counter()
    {
        bool healthy = false;

        FakeTimeProvider time = new();

        MandatoryJailProbeCache cache = new(() => healthy, RetryAfter, time);

        Assert.False(cache.IsAvailable());

        healthy = true;

        // Still inside the failure window: the remembered failure answers.
        Assert.False(cache.IsAvailable());

        cache.Reset();

        Assert.Equal(0, cache.ProbeCount);

        Assert.True(cache.IsAvailable());

        Assert.Equal(1, cache.ProbeCount);
    }

    [Fact]
    public void GetStatus_when_disabled_does_not_probe()
    {
        int probes = 0;

        WorkspaceCheckRuntime runtime = new(
            new WorkspaceCheckSettings { Enabled = false },
            new NeverUsedServiceScopeFactory(),
            mandatoryJailAvailability: () =>
            {
                probes++;

                return true;
            });

        WorkspaceCheckExecutionStatus first = runtime.GetStatus(Path.GetTempPath());

        WorkspaceCheckExecutionStatus second = runtime.GetStatus(Path.GetTempPath());

        Assert.Equal(0, probes);

        Assert.False(first.IsEligible);

        Assert.False(second.IsEligible);

        Assert.Contains("disabled", first.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetStatus_when_enabled_consults_the_probe()
    {
        int probes = 0;

        WorkspaceCheckRuntime runtime = new(
            new WorkspaceCheckSettings { Enabled = true },
            new NeverUsedServiceScopeFactory(),
            mandatoryJailAvailability: () =>
            {
                probes++;

                return false;
            });

        WorkspaceCheckExecutionStatus status = runtime.GetStatus(Path.GetTempPath());

        Assert.Equal(1, probes);

        Assert.False(status.IsEligible);

        Assert.DoesNotContain("disabled by configuration", status.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NeverUsedServiceScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new NotSupportedException(
                "GetStatus never creates a scope; this factory exists only to satisfy the constructor.");
    }
}
