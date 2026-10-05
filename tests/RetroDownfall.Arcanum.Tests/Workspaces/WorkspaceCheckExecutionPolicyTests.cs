using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

/// <summary>
/// The mandatory-jail probe spawns <c>/usr/bin/sandbox-exec</c>. A healthy result is cached for the
/// process lifetime so the <c>workspace_check</c> invocation path and the tools/list advertisement gate
/// do not pay that spawn on every call; a failed result is never cached (a transient failure must not
/// disable the tool until restart), and a disabled tool never probes at all. Every test builds its own
/// cache or injects its own probe, so none of them touches process-global state.
/// </summary>
public sealed class WorkspaceCheckExecutionPolicyTests
{
    [Fact]
    public void Successful_probe_is_cached_for_the_process_lifetime()
    {
        int probes = 0;

        MandatoryJailProbeCache cache = new(() =>
        {
            probes++;

            return true;
        });

        Assert.True(cache.IsAvailable());

        Assert.True(cache.IsAvailable());

        Assert.True(cache.IsAvailable());

        Assert.Equal(1, probes);

        Assert.Equal(1, cache.ProbeCount);
    }

    [Fact]
    public void Failed_probe_is_retried_on_next_call()
    {
        Queue<bool> results = new([false, false, true]);

        MandatoryJailProbeCache cache = new(results.Dequeue);

        Assert.False(cache.IsAvailable());

        Assert.False(cache.IsAvailable());

        Assert.True(cache.IsAvailable());

        Assert.True(cache.IsAvailable());

        Assert.Equal(3, cache.ProbeCount);
    }

    [Fact]
    public void Reset_clears_the_cached_result_and_the_counter()
    {
        MandatoryJailProbeCache cache = new(() => true);

        Assert.True(cache.IsAvailable());

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
