namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Atomic slot gate for <see cref="ApprenticeService"/> concurrent execution.
/// Uses increment-then-compare so parallel starts cannot over-subscribe the cap.
/// </summary>
public sealed class ApprenticeConcurrencyGate
{

    private int _runningCount;

    public bool TryAcquire(int maxConcurrent)
    {

        int active = Interlocked.Increment(ref _runningCount);

        if (active > maxConcurrent)
        {

            Interlocked.Decrement(ref _runningCount);

            return false;

        }

        return true;

    }

    public void Release()
    {

        Interlocked.Decrement(ref _runningCount);

    }

    public int RunningCount => Volatile.Read(ref _runningCount);

}
