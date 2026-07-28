using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class SseConnectionGateTests
{

    [Fact]
    public void TryAcquire_RespectsMaxConnections()
    {

        ArcanumSettings settings = new()
        {
            Execution = new ExecutionSettings { MaxSseConnections = 2 },
        };

        SseConnectionGate gate = new(new SseConnectionCounter(), new TestOptionsMonitor<ArcanumSettings>(settings));

        Assert.True(gate.TryAcquire("Test", out SseConnectionLease? first, out _));

        Assert.NotNull(first);

        Assert.True(gate.TryAcquire("Test", out SseConnectionLease? second, out _));

        Assert.NotNull(second);

        Assert.False(gate.TryAcquire("Test", out SseConnectionLease? third, out SseConnectionDenial denial));

        Assert.Null(third);

        Assert.Equal(SseDenialReason.Global, denial.Reason);

        first!.Dispose();

        Assert.True(gate.TryAcquire("Test", out SseConnectionLease? afterRelease, out _));

        Assert.NotNull(afterRelease);

        second!.Dispose();

        afterRelease!.Dispose();

    }

}
