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
            EventBus = new EventBusSettings { MaxSseConnections = 2 },
        };

        SseConnectionGate gate = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        Assert.True(gate.TryAcquire(out SseConnectionLease? first));

        Assert.NotNull(first);

        Assert.True(gate.TryAcquire(out SseConnectionLease? second));

        Assert.NotNull(second);

        Assert.False(gate.TryAcquire(out SseConnectionLease? third));

        Assert.Null(third);

        first!.Dispose();

        Assert.True(gate.TryAcquire(out SseConnectionLease? afterRelease));

        Assert.NotNull(afterRelease);

        second!.Dispose();

        afterRelease!.Dispose();

    }

}
