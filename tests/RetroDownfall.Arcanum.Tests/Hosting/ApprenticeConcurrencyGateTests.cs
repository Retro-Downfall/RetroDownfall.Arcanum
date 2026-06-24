using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class ApprenticeConcurrencyGateTests
{

    [Fact]
    public void TryAcquire_RespectsMaxConcurrent()
    {

        ApprenticeConcurrencyGate gate = new();

        Assert.True(gate.TryAcquire(2));

        Assert.True(gate.TryAcquire(2));

        Assert.False(gate.TryAcquire(2));

        Assert.Equal(2, gate.RunningCount);

        gate.Release();

        Assert.True(gate.TryAcquire(2));

        Assert.Equal(2, gate.RunningCount);

    }

    [Fact]
    public void ParallelTryAcquire_DoesNotOverSubscribe()
    {

        ApprenticeConcurrencyGate gate = new();

        const int max = 3;

        const int attempts = 50;

        int acquired = 0;

        Parallel.For(0, attempts, _ =>
        {

            if (gate.TryAcquire(max))
            {

                Interlocked.Increment(ref acquired);

            }

        });

        Assert.True(acquired <= max);

        Assert.Equal(acquired, gate.RunningCount);

        for (int i = 0; i < acquired; i++)
        {

            gate.Release();

        }

        Assert.Equal(0, gate.RunningCount);

    }

}
