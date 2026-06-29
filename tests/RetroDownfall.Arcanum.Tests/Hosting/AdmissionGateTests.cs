using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class AdmissionGateTests
{

    [Fact]
    public void TryEnter_RespectsMax()
    {

        AdmissionGate gate = new();

        Assert.True(gate.TryEnter(2, out IDisposable? first));

        Assert.NotNull(first);

        Assert.True(gate.TryEnter(2, out IDisposable? second));

        Assert.NotNull(second);

        Assert.False(gate.TryEnter(2, out IDisposable? third));

        Assert.Null(third);

        Assert.Equal(2, gate.CurrentCount);

        first!.Dispose();

        Assert.Equal(1, gate.CurrentCount);

        Assert.True(gate.TryEnter(2, out IDisposable? afterRelease));

        Assert.NotNull(afterRelease);

        second!.Dispose();

        afterRelease!.Dispose();

        Assert.Equal(0, gate.CurrentCount);

    }

    [Fact]
    public void ParallelTryEnter_DoesNotOverSubscribe()
    {

        AdmissionGate gate = new();

        const int max = 3;

        const int attempts = 50;

        List<IDisposable> leases = [];

        object leasesLock = new();

        int acquired = 0;

        Parallel.For(0, attempts, _ =>
        {

            if (gate.TryEnter(max, out IDisposable? lease))
            {

                Interlocked.Increment(ref acquired);

                lock (leasesLock)
                {

                    leases.Add(lease!);

                }

            }

        });

        Assert.True(acquired <= max);

        Assert.Equal(acquired, gate.CurrentCount);

        foreach (IDisposable lease in leases)
        {

            lease.Dispose();

        }

        Assert.Equal(0, gate.CurrentCount);

    }

    [Fact]
    public void Dispose_IsIdempotentAndReleasesOnce()
    {

        AdmissionGate gate = new();

        Assert.True(gate.TryEnter(1, out IDisposable? lease));

        Assert.NotNull(lease);

        Assert.Equal(1, gate.CurrentCount);

        lease!.Dispose();

        Assert.Equal(0, gate.CurrentCount);

        lease.Dispose();

        Assert.Equal(0, gate.CurrentCount);

    }

    [Fact]
    public void Dispose_FreesSlotForNextEnter()
    {

        AdmissionGate gate = new();

        Assert.True(gate.TryEnter(1, out IDisposable? first));

        Assert.False(gate.TryEnter(1, out _));

        first!.Dispose();

        Assert.True(gate.TryEnter(1, out IDisposable? second));

        Assert.NotNull(second);

        second!.Dispose();

    }

}
