namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Atomic slot gate for <see cref="ApprenticeService"/> concurrent execution.
/// Uses increment-then-compare so parallel starts cannot over-subscribe the cap.
/// </summary>
public sealed class ApprenticeConcurrencyGate
{

    private readonly AdmissionGate _gate = new();

    public int RunningCount => _gate.CurrentCount;

    public bool TryAcquire(int maxConcurrent, out IDisposable? lease)
    {

        return _gate.TryEnter(maxConcurrent, out lease);

    }

}
