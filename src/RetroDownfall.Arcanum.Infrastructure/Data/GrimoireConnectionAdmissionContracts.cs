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

    ValueTask<Result> CompleteAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken);

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
