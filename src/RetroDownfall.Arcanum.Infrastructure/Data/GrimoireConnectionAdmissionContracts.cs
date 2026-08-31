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

    IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection);

    Result<IGrimoireClosingOwner> BeginOrResumeExclusive(
        CovenantExclusiveRecoveryOwner owner);

    ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(
        IGrimoireClosingOwner closingOwner,
        CancellationToken cancellationToken);

    Task<long> WaitForNextOpenGenerationAsync(
        long observedGeneration,
        CancellationToken cancellationToken);

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
