using System.Threading;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>The closed set of legacy V3 maintenance actions that require an exact exclusive lease.</summary>
internal enum CovenantV3MaintenancePurpose : byte
{
    CanonicalErasure = 1,

    WalTruncation = 2,

    CompactionVacuum = 3,

    CompactionExport = 4,

    CompactionExportVerification = 5,

    CompactionPostReplaceJournalRestore = 6,

    AcceleratorInitialization = 7,

    CandidateReopenVerification = 8,
}

/// <summary>A one-shot, purpose-bound proof that a V3 maintenance action is entered by its owner.</summary>
internal sealed class CovenantV3MaintenanceCapability : IAsyncDisposable
{
    private readonly ICovenantExclusiveOperationLease _lease;

    private readonly CovenantV3MaintenancePurpose _purpose;

    private int _consumed;

    private CovenantV3MaintenanceCapability(
        ICovenantExclusiveOperationLease lease,
        CovenantV3MaintenancePurpose purpose,
        CovenantExclusiveOperation operation)
    {
        _lease = lease;
        _purpose = purpose;
        Operation = operation;
    }

    internal CovenantExclusiveOperation Operation { get; }

    internal static async ValueTask<Result<CovenantV3MaintenanceCapability>> MintAsync(
        ICovenantExclusiveOperationLease exactLease,
        CovenantV3MaintenancePurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactLease);

        CovenantExclusiveRecoveryOwner? owner = exactLease.Snapshot.RecoveryOwner;

        if (owner is not { IsValid: true }
            || owner.Value.Operation is not CovenantExclusiveOperation.CovenantReset
                and not CovenantExclusiveOperation.HealthyCatalogFactoryErasure)
        {
            return Refusal();
        }

        Result revalidated = await exactLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {
            return Result<CovenantV3MaintenanceCapability>.Failure(revalidated.Error);
        }

        CovenantV3MaintenanceCapability? minted = null;

        Result held = exactLease.ExecuteWhileHeld(() =>
        {
            minted = new CovenantV3MaintenanceCapability(exactLease, purpose, owner.Value.Operation);

            return Result.Success();
        });

        return held.IsFailure
            ? Result<CovenantV3MaintenanceCapability>.Failure(held.Error)
            : Result<CovenantV3MaintenanceCapability>.Success(minted!);
    }

    internal async ValueTask<Result> ConsumeAsync(
        CovenantV3MaintenancePurpose expectedPurpose,
        CancellationToken cancellationToken)
    {
        if (_purpose != expectedPurpose || Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
        {
            return RefusalResult();
        }

        Result revalidated = await _lease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {
            return revalidated;
        }

        return _lease.ExecuteWhileHeld(() =>
        {
            CovenantExclusiveRecoveryOwner? owner = _lease.Snapshot.RecoveryOwner;

            return owner is { IsValid: true } && owner.Value.Operation == Operation
                ? Result.Success()
                : RefusalResult();
        });
    }

    public ValueTask DisposeAsync()
    {
        _ = Interlocked.CompareExchange(ref _consumed, 1, 0);

        return ValueTask.CompletedTask;
    }

    private static Result<CovenantV3MaintenanceCapability> Refusal() =>
        Result<CovenantV3MaintenanceCapability>.Failure(RefusalResult().Error);

    private static Result RefusalResult() =>
        Result.Failure(new Error(
            ErrorCodes.Covenant.InvalidScope,
            "A legacy Covenant V3 maintenance action requires its exact live exclusive operation lease."));
}

/// <summary>The four independent one-shot proofs a V3 compaction may need.</summary>
internal sealed class CovenantV3CompactionCapabilities : IAsyncDisposable
{
    internal CovenantV3CompactionCapabilities(
        CovenantV3MaintenanceCapability vacuum,
        CovenantV3MaintenanceCapability export,
        CovenantV3MaintenanceCapability exportVerification,
        CovenantV3MaintenanceCapability postReplaceJournalRestore)
    {
        Vacuum = vacuum ?? throw new ArgumentNullException(nameof(vacuum));
        Export = export ?? throw new ArgumentNullException(nameof(export));
        ExportVerification = exportVerification ?? throw new ArgumentNullException(nameof(exportVerification));
        PostReplaceJournalRestore = postReplaceJournalRestore ?? throw new ArgumentNullException(nameof(postReplaceJournalRestore));
    }

    internal CovenantV3MaintenanceCapability Vacuum { get; }

    internal CovenantV3MaintenanceCapability Export { get; }

    internal CovenantV3MaintenanceCapability ExportVerification { get; }

    internal CovenantV3MaintenanceCapability PostReplaceJournalRestore { get; }

    public async ValueTask DisposeAsync()
    {
        await Vacuum.DisposeAsync().ConfigureAwait(false);
        await Export.DisposeAsync().ConfigureAwait(false);
        await ExportVerification.DisposeAsync().ConfigureAwait(false);
        await PostReplaceJournalRestore.DisposeAsync().ConfigureAwait(false);
    }
}
