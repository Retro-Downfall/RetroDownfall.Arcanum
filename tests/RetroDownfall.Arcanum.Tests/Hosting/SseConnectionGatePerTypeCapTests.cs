using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class SseConnectionGatePerTypeCapTests
{

    [Fact]
    public void SubscribeAsync_rejects_when_per_type_cap_exceeded()
    {

        ArcanumSettings settings = new()
        {
            Execution = new ExecutionSettings { MaxSseConnectionsPerType = 1 },
        };

        SseConnectionGate gate = new(new SseConnectionCounter(), new TestOptionsMonitor<ArcanumSettings>(settings));

        Assert.True(gate.TryAcquire(SseEventTypes.Logs, out SseConnectionLease? first, out SseConnectionDenial firstDenial));

        Assert.NotNull(first);

        Assert.Equal(SseConnectionDenial.None, firstDenial);

        Assert.False(gate.TryAcquire(SseEventTypes.Logs, out SseConnectionLease? second, out SseConnectionDenial secondDenial));

        Assert.Null(second);

        Assert.Equal(SseDenialReason.PerType, secondDenial.Reason);

        Assert.Equal(SseEventTypes.Logs, secondDenial.EventType);

        Assert.Equal(1, secondDenial.Limit);

        first!.Dispose();

    }

    [Fact]
    public void SubscribeAsync_allows_different_types_when_one_is_full()
    {

        ArcanumSettings settings = new()
        {
            Execution = new ExecutionSettings { MaxSseConnectionsPerType = 1 },
        };

        SseConnectionGate gate = new(new SseConnectionCounter(), new TestOptionsMonitor<ArcanumSettings>(settings));

        Assert.True(gate.TryAcquire(SseEventTypes.Logs, out SseConnectionLease? logsLease, out _));

        Assert.False(gate.TryAcquire(SseEventTypes.Logs, out SseConnectionLease? secondLogsLease, out SseConnectionDenial deniedLogs));

        Assert.Null(secondLogsLease);

        Assert.Equal(SseDenialReason.PerType, deniedLogs.Reason);

        Assert.True(gate.TryAcquire(SseEventTypes.Daemon, out SseConnectionLease? daemonLease, out SseConnectionDenial daemonDenial));

        Assert.NotNull(daemonLease);

        Assert.Equal(SseConnectionDenial.None, daemonDenial);

        logsLease!.Dispose();

        daemonLease!.Dispose();

    }

    [Fact]
    public void Disconnect_frees_per_type_slot()
    {

        ArcanumSettings settings = new()
        {
            Execution = new ExecutionSettings { MaxSseConnectionsPerType = 1 },
        };

        SseConnectionGate gate = new(new SseConnectionCounter(), new TestOptionsMonitor<ArcanumSettings>(settings));

        Assert.True(gate.TryAcquire(SseEventTypes.Chronicle, out SseConnectionLease? first, out _));

        Assert.False(gate.TryAcquire(SseEventTypes.Chronicle, out SseConnectionLease? blocked, out SseConnectionDenial blockedDenial));

        Assert.Null(blocked);

        Assert.Equal(SseDenialReason.PerType, blockedDenial.Reason);

        first!.Dispose();

        Assert.True(gate.TryAcquire(SseEventTypes.Chronicle, out SseConnectionLease? afterRelease, out SseConnectionDenial afterReleaseDenial));

        Assert.NotNull(afterRelease);

        Assert.Equal(SseConnectionDenial.None, afterReleaseDenial);

        afterRelease!.Dispose();

    }

    [Fact]
    public void Global_cap_still_enforced()
    {

        // MaxSseConnectionsPerType must be set high enough here (5) that the per-type cap never
        // engages — otherwise this test would prove nothing about the global cap, since the
        // default per-type cap (20) is well above the global cap (1) too, but an explicit low
        // per-type cap would trigger PerType denial first and mask the global-cap behavior.
        ArcanumSettings settings = new()
        {
            Execution = new ExecutionSettings { MaxSseConnections = 1, MaxSseConnectionsPerType = 5 },
        };

        SseConnectionGate gate = new(new SseConnectionCounter(), new TestOptionsMonitor<ArcanumSettings>(settings));

        Assert.True(gate.TryAcquire(SseEventTypes.Daemon, out SseConnectionLease? first, out SseConnectionDenial firstDenial));

        Assert.NotNull(first);

        Assert.Equal(SseConnectionDenial.None, firstDenial);

        Assert.False(gate.TryAcquire(SseEventTypes.Mcp, out SseConnectionLease? second, out SseConnectionDenial secondDenial));

        Assert.Null(second);

        Assert.Equal(SseDenialReason.Global, secondDenial.Reason);

        first!.Dispose();

    }

}
