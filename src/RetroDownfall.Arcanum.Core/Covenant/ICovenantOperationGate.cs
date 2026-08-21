using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The one linearizable gate every Covenant read, write, turn, MCP staging, accelerator batch,
/// cleanup batch, and destructive operation passes through.
/// </summary>
/// <remarks>
/// Ordinary work acquires a generation-bound lease and keeps it for the whole operation, including
/// serialization and stream completion. A destructive operation closes the affected scopes, drains
/// every live lease over them, and only then reports that it may change anything. That ordering is
/// the entire point: without it, a reader could still be writing a Covenant-derived byte to a
/// response while reset was reporting the data gone.
///
/// <para>Acquisition and revalidation are memory-only. A gate that had to query the database to
/// learn whether the database was usable would be both slow and circular.</para>
/// </remarks>
public interface ICovenantOperationGate
{

    ValueTask<Result<CovenantInstallationReadLease>> AcquireInstallationReadAsync(
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantReadLease>> AcquireReadAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantWriteLease>> AcquireWriteAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantTurnLease>> AcquireTurnAsync(
        CanonicalCampaignContext campaign,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantMcpLease>> AcquireMcpAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantAcceleratorLease>> AcquireAcceleratorAsync(
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantCleanupLease>> AcquireCleanupAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantCampaignExclusiveLease>> AcquireCampaignExclusiveAsync(
        Guid campaignId,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantProtectedTransferLease>> AcquireProtectedTransferAsync(
        ProtectedTransferScope scope,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resumes this exact installation owner when a durable closure exists, or acquires a fresh
    /// closure only when the decision observes no installation closure at all.
    /// </summary>
    ValueTask<Result<CovenantExclusiveLease>> ResumeOrAcquireExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantCampaignExclusiveLease>> ResumeCampaignExclusiveAsync(
        Guid campaignId,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantProtectedTransferLease>> ResumeProtectedTransferAsync(
        ProtectedTransferScope scope,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantExclusiveLease>> ResumeExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);

}
