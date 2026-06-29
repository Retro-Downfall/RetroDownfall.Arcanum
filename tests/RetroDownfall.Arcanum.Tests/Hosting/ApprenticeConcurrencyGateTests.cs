using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class ApprenticeConcurrencyGateTests
{

    [Fact]
    public void TryAcquire_RespectsMaxConcurrent()
    {

        ApprenticeConcurrencyGate gate = new();

        Assert.True(gate.TryAcquire(2, out IDisposable? first));

        Assert.True(gate.TryAcquire(2, out IDisposable? second));

        Assert.False(gate.TryAcquire(2, out _));

        Assert.Equal(2, gate.RunningCount);

        first!.Dispose();

        Assert.True(gate.TryAcquire(2, out IDisposable? third));

        Assert.Equal(2, gate.RunningCount);

        second!.Dispose();

        third!.Dispose();

    }

    [Fact]
    public void ParallelTryAcquire_DoesNotOverSubscribe()
    {

        ApprenticeConcurrencyGate gate = new();

        const int max = 3;

        const int attempts = 50;

        List<IDisposable> leases = [];

        object leasesLock = new();

        int acquired = 0;

        Parallel.For(0, attempts, _ =>
        {

            if (gate.TryAcquire(max, out IDisposable? lease))
            {

                Interlocked.Increment(ref acquired);

                lock (leasesLock)
                {

                    leases.Add(lease!);

                }

            }

        });

        Assert.True(acquired <= max);

        Assert.Equal(acquired, gate.RunningCount);

        foreach (IDisposable lease in leases)
        {

            lease.Dispose();

        }

        Assert.Equal(0, gate.RunningCount);

    }

}
