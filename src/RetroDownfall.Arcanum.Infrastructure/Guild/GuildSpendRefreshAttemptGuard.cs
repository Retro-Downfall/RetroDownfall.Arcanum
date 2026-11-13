using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>
/// Observes attempt currentness while the service scope retains the actual physical, work and group authority.
/// The original generation is a maintenance veto only; this guard carries no work or group handle.
/// </summary>
internal sealed class GuildSpendRefreshAttemptGuard(
    IGrimoireConnectionAdmissionGate gate, long workGeneration,
    IDisposable physical, GuildSpendRefreshTracker.Entry entry, LongRunningOperationOwnership ownership, Guid operationId) : IDisposable
{
    private readonly LongRunningOperationOwnership.ClaimObservation? _claim = ownership.Observe(operationId);

    private int _disposed;

    internal bool IsCurrent(Guid expectedOperation, Guid expectedRoot)
    {
        if (operationId != expectedOperation || entry.Root != expectedRoot || Volatile.Read(ref _disposed) != 0 || physical is null || _claim?.IsCurrent != true || !entry.IsOpen) return false;

        bool admitted = gate.IsOrdinaryGeneration(workGeneration);

        return admitted && Volatile.Read(ref _disposed) == 0 && _claim?.IsCurrent == true && entry.IsOpen;
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
