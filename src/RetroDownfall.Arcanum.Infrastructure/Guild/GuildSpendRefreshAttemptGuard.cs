using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Host-owned proof retained only while the exact physical attempt and its work group remain owned.</summary>
internal sealed class GuildSpendRefreshAttemptGuard(
    IGrimoireConnectionAdmissionGate gate, IGrimoireWorkLease work, IGrimoireExternalEffectGroup group,
    IDisposable physical, GuildSpendRefreshTracker.Entry entry, LongRunningOperationOwnership ownership, Guid operationId) : IDisposable
{
    private readonly LongRunningOperationOwnership.ClaimObservation? _claim = ownership.Observe(operationId);

    private int _disposed;

    internal bool IsCurrent(Guid expectedOperation, Guid expectedRoot)
    {
        if (operationId != expectedOperation || entry.Root != expectedRoot || Volatile.Read(ref _disposed) != 0 || group is null || physical is null || _claim?.IsCurrent != true || !entry.IsOpen) return false;

        bool admitted = !work.MaintenanceRevocationPending && work.Generation == gate.CurrentGeneration;

        return admitted && Volatile.Read(ref _disposed) == 0 && _claim?.IsCurrent == true && entry.IsOpen;
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
