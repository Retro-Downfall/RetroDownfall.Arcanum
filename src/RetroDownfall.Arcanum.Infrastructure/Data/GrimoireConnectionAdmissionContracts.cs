using System.Data.Common;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Process-local authority over ordinary physical Grimoire opens and exclusive maintenance closure.
/// </summary>
internal interface IGrimoireConnectionAdmissionGate
{

    long CurrentGeneration { get; }

    bool TryAcquireRequestLease(
        GrimoireRequestKind kind,
        out IGrimoireRequestLease? lease);

    bool TryAcquireWorkLease(
        GrimoireWorkKind kind,
        out IGrimoireWorkLease? lease);

    IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection);

    Result<IGrimoireClosingOwner> BeginOrResumeExclusive(
        CovenantExclusiveRecoveryOwner owner,
        IGrimoireRequestLease? initiatingRequest = null,
        DbConnection? scopedConnection = null);

    ValueTask<Result> DrainRequestAndWorkAsync(
        IGrimoireClosingOwner closingOwner,
        CancellationToken cancellationToken);

    ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(
        IGrimoireClosingOwner closingOwner,
        CancellationToken cancellationToken);

    ValueTask<Result> AbortClosingAsync(
        IGrimoireClosingOwner closingOwner,
        Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync,
        CancellationToken cancellationToken);

    Task<long> WaitForNextOpenGenerationAsync(
        long observedGeneration,
        CancellationToken cancellationToken);

    ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>> AcquireExpiredLeaseAdoptionInterlockAsync(
        CovenantExclusiveRecoveryOwner candidateOwner,
        Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>>
            revalidateDurableOwnerAsync,
        CancellationToken cancellationToken);

}

/// <summary>
/// The ordinary request lifetime policy applied before endpoint execution.
/// </summary>
internal enum GrimoireRequestKind : byte
{

    Finite = 1,

    QuiesceableStream = 2,

}

/// <summary>
/// The complete background unit whose scope and durable disposition drain together.
/// </summary>
internal enum GrimoireWorkKind : byte
{

    SessionAttachmentIndexing = 1,

    EntryWeaving = 2,

    SagaExtraction = 3,

}

/// <summary>
/// One admitted request, held through asynchronous disposal of its request scope.
/// </summary>
internal interface IGrimoireRequestLease : IAsyncDisposable
{

    GrimoireRequestKind Kind { get; }

    long Generation { get; }

    CancellationToken MaintenanceRevocation { get; }

}

/// <summary>
/// One admitted background unit, held through its final effect disposition and scope disposal.
/// </summary>
internal interface IGrimoireWorkLease : IAsyncDisposable
{

    GrimoireWorkKind Kind { get; }

    long Generation { get; }

    CancellationToken MaintenanceRevocation { get; }

    bool TryBeginExternalEffectGroup(
        out IGrimoireExternalEffectGroup? effectGroup);

}

/// <summary>
/// Atomic authority for one independently resumable external-effect group.
/// </summary>
internal interface IGrimoireExternalEffectGroup : IAsyncDisposable
{
}

/// <summary>
/// One physical ordinary-open attempt, from before native open through its terminal callback.
/// </summary>
internal interface IGrimoireConnectionOpenTicket : IDisposable
{

    long Generation { get; }

    Result RevalidateAfterNativeOpen();

    Result MarkOpened();

    void MarkFailed();

    void MarkRefusedAfterOpen();

}

/// <summary>
/// The exact owner token for the recoverable transition that precedes closed admission.
/// </summary>
internal interface IGrimoireClosingOwner : IAsyncDisposable
{

    CovenantExclusiveRecoveryOwner Owner { get; }

    long Generation { get; }

}

/// <summary>
/// Exclusive authority issued only after every unresolved physical open has terminated.
/// </summary>
internal interface IGrimoireExclusiveClosedLease : IAsyncDisposable
{

    CovenantExclusiveRecoveryOwner Owner { get; }

    long Generation { get; }

    Result<IGrimoireScopedConnectionPermit> AcquireScopedConnectionPermit(
        DbConnection connection);

    Result<IGrimoireMaintenanceRenewalTicket> IssueMaintenanceRenewalTicket(
        IGrimoireMaintenanceIoLane lane);

    Result<IGrimoireMaintenanceConnectionCapability> IssueMaintenanceConnectionCapability(
        string canonicalPath,
        CovenantMaintenanceConnectionMode mode,
        CovenantMaintenanceConnectionPurpose purpose,
        IGrimoireMaintenanceIoLane lane);

    ValueTask<Result<IGrimoireMaintenanceIoLane>> AcquireMaintenanceIoLaneAsync(
        Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>>
            revalidateDurableOwnerAsync,
        CancellationToken cancellationToken);

    ValueTask<Result> CompleteAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken);

}

/// <summary>
/// Reusable authority for physical opens of one exact maintenance-owned connection object.
/// </summary>
internal interface IGrimoireScopedConnectionPermit : IAsyncDisposable
{

    Result<IGrimoireTrackedMaintenanceHandle> AcquireOpen(
        DbConnection connection,
        CovenantExclusiveRecoveryOwner owner,
        long generation,
        IGrimoireMaintenanceIoLane lane);

}

/// <summary>
/// One-shot authority for one independent unpooled durable-owner renewal.
/// </summary>
internal interface IGrimoireMaintenanceRenewalTicket : IAsyncDisposable
{

    Result<IGrimoireTrackedMaintenanceHandle> Consume(
        CovenantExclusiveRecoveryOwner owner,
        long generation,
        IGrimoireMaintenanceIoLane lane);

}

/// <summary>
/// One-shot authority for one purpose-, mode-, and canonical-path-bound maintenance open.
/// </summary>
internal interface IGrimoireMaintenanceConnectionCapability : IAsyncDisposable
{

    Result<IGrimoireTrackedMaintenanceHandle> Consume(
        CovenantExclusiveRecoveryOwner owner,
        long generation,
        string canonicalPath,
        CovenantMaintenanceConnectionMode mode,
        CovenantMaintenanceConnectionPurpose purpose,
        IGrimoireMaintenanceIoLane lane);

}

/// <summary>
/// One maintenance physical-open lifetime that ends only after open failure or physical closure.
/// </summary>
internal interface IGrimoireTrackedMaintenanceHandle
{

    Result ReportOpenStarted();

    Result ReportNotOpened();

    Result ReportPhysicallyClosed();

}

/// <summary>
/// Exclusive connection-sensitive phase ownership over the shared adoption interlock.
/// </summary>
internal interface IGrimoireMaintenanceIoLane : IAsyncDisposable
{

    CovenantExclusiveRecoveryOwner Owner { get; }

    long Generation { get; }

    ValueTask<Result> RevalidateDurableOwnerAsync(
        Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>>
            revalidateDurableOwnerAsync,
        CancellationToken cancellationToken);

}

/// <summary>
/// Exclusive expired-owner adoption ownership over the shared maintenance interlock.
/// </summary>
internal interface IGrimoireExpiredLeaseAdoptionInterlock : IAsyncDisposable
{

    CovenantExclusiveRecoveryOwner CandidateOwner { get; }

}

/// <summary>
/// SQLite access requested by a maintenance phase.
/// </summary>
internal enum CovenantMaintenanceConnectionMode : byte
{

    ReadOnly = 1,

    ReadWrite = 2,

}

/// <summary>
/// The closed phase that is authorized to construct one fresh maintenance connection.
/// </summary>
internal enum CovenantMaintenanceConnectionPurpose : byte
{

    CanonicalErasure = 1,

    Compaction = 2,

    IntegrityVerification = 3,

    SidecarProof = 4,

    ReopenVerification = 5,

}

/// <summary>
/// Expected refusal raised before ordinary code reaches SQLite while admission is closed.
/// </summary>
internal sealed class GrimoireMaintenanceUnavailableException : InvalidOperationException
{

    internal GrimoireMaintenanceUnavailableException()
        : base("The Grimoire is temporarily unavailable while maintenance owns connection admission.")
    {
    }

}
