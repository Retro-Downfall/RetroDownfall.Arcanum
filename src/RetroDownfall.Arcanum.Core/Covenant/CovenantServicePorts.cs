using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The one authenticated read port behind every Covenant management surface.
/// </summary>
/// <remarks>
/// One port rather than one per route, because the routes share a lease, a snapshot policy, and an
/// authority requirement, and splitting them would let a later surface acquire coverage the others
/// deliberately do not have. The HTTP layer maps requests to these calls and owns the cursor
/// envelope; nothing below here knows what a cursor string looks like (§10.16).
///
/// <para>Every content-bearing method borrows a caller-owned snapshot lease and returns a plain
/// result. The endpoint acquires that lease, hands it here, and then holds it across serialization,
/// so a reset that lands mid-response is refused before the first byte rather than discovered by a
/// client holding half a page of content that no longer exists. The service never acquires or
/// releases a lease of its own: a service that could escalate its own coverage would make the gate's
/// drain guarantee unprovable, because a reader could appear inside an operation that had already
/// finished draining (§10.18).</para>
///
/// <para><see cref="StatusAsync"/> is the one exception and takes no lease. It is content-free
/// capability health, reachable wherever <c>/api/memory/status</c> is, and requiring a Covenant lease
/// for it would make the aggregate status surface unavailable exactly when an operator is trying to
/// find out why Covenant is unavailable.</para>
/// </remarks>
public interface ICovenantManagementService
{

    ValueTask<Result<CovenantPageDto>> ListAsync(
        CovenantListRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantPageDto>> QueryAsync(
        CovenantQueryRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantDetailDto>> DetailAsync(
        CovenantDetailRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantVersionPageDto>> VersionsAsync(
        CovenantVersionsRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantSourcesDto>> SourcesAsync(
        CovenantSourcesRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    /// <summary>
    /// Explains one explicit evaluation against a fresh management-only snapshot.
    /// </summary>
    /// <remarks>
    /// It detaches its own state lease rather than borrowing the caller's, because explain runs the
    /// live loader, linker, and admission code over a snapshot it creates for the purpose. Borrowing
    /// the caller's read lease would either explain a different snapshot than the one it evaluated or
    /// force a nested acquisition, and a nested acquisition inside a drain is a deadlock.
    /// </remarks>
    ValueTask<CovenantLeasedServiceResult<CovenantExplainDto>> ExplainAsync(
        CovenantExplainRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantStatusDto>> StatusAsync(CancellationToken cancellationToken);

}

/// <summary>
/// The one operator mutation port: prepare, then commit against exactly what was prepared.
/// </summary>
/// <remarks>
/// Prepare and apply are separate methods on one port rather than two ports, because the preflight
/// calculator they share is what makes the token binding meaningful. A second port that could prepare
/// without the one that commits would be a second opinion about the same effect.
/// </remarks>
public interface ICovenantMutationService
{

    ValueTask<Result<CovenantMutationPreflightDto>> PrepareSetAsync(
        CovenantSetPrepareRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantMutationPreflightDto>> PrepareRetireAsync(
        CovenantRetirePrepareRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantMutationResultDto>> SetAsync(
        CovenantSetRequest request,
        CovenantWriteLease writeLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantMutationResultDto>> RetireAsync(
        CovenantRetireRequest request,
        CovenantWriteLease writeLease,
        CancellationToken cancellationToken);

}

/// <summary>
/// The one maintenance and recovery port.
/// </summary>
/// <remarks>
/// Index rebuild and family reinitialize return a durable operation descriptor rather than a result,
/// because both outlive the request that started them. Returning a completed result would mean the
/// HTTP connection was the only thing tracking a database-replacing operation.
/// </remarks>
public interface ICovenantMaintenanceService
{

    ValueTask<Result<CovenantSchemaRepairResultDto>> RepairSchemaAsync(
        CovenantSchemaRepairRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<LongRunningOperationDto>> RebuildIndexAsync(
        CovenantIndexRebuildRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantFamilyReinitializePlanDto>> PrepareFamilyReinitializeAsync(
        CovenantFamilyReinitializePrepareRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<LongRunningOperationDto>> ApplyFamilyReinitializeAsync(
        CovenantFamilyReinitializeApplyRequest request,
        CancellationToken cancellationToken);

}

/// <summary>
/// The one Campaign physical-root administration port.
/// </summary>
/// <remarks>
/// The server opens and identifies the path; the CLI never touches a marker. A client that inspected
/// the filesystem itself would be describing its own machine, which is the wrong machine whenever the
/// host is not the caller (§10.12).
/// </remarks>
public interface ICampaignPathIdentityService
{

    ValueTask<Result<CampaignPathIdentityStatusPageDto>> StatusAsync(
        CampaignPathIdentityStatusRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CampaignPathIdentityPlanDto>> PreparePathAsync(
        Guid campaignId,
        CampaignPathPrepareRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the completed receipt and any active marker intent in one snapshot, under a short read
    /// lease, before anything closes Campaign admission.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ApplyPathAsync"/> because a replay must not cost an exclusive
    /// acquisition. Closing and draining a Campaign's admission to answer "you already did this" would
    /// make the cheapest possible request the most disruptive one, and a client retrying a completed
    /// operation is the most common request this surface sees.
    /// </remarks>
    ValueTask<Result<CampaignPathApplyProbe>> ProbeApplyAsync(
        Guid campaignId,
        CampaignPathApplyRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs the crash-recoverable marker protocol under a Campaign-exclusive lease the caller
    /// acquired after releasing its probe lease.
    /// </summary>
    /// <remarks>
    /// The probe lease must be released first. Holding a read lease while waiting for an exclusive
    /// drain over the same Campaign is a self-deadlock: the drain waits for the reader that is
    /// waiting for the drain.
    /// </remarks>
    ValueTask<CovenantExclusiveLeasedServiceResult<CampaignPathIdentityResultDto>> ApplyPathAsync(
        Guid campaignId,
        CampaignPathApplyRequest request,
        CovenantCampaignExclusiveLease exclusiveLease,
        CancellationToken cancellationToken);

}

/// <summary>
/// The one port that resolves a legacy-unresolved Session's immutable binding.
/// </summary>
/// <remarks>
/// It operates only on <c>LegacyUnresolved</c>. A final Global-only or Campaign binding is not
/// editable through this port or any other, because a Session whose scope could move would carry a
/// history assembled under one Covenant into a turn evaluated under another.
/// </remarks>
public interface ISessionCampaignBindingService
{

    ValueTask<Result<SessionCampaignBindingStatusPageDto>> StatusAsync(
        SessionCampaignBindingStatusRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<SessionCampaignBindingPlanDto>> PrepareBindingAsync(
        SessionCampaignBindingPrepareRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<SessionCampaignBindingResultDto>> ApplyBindingAsync(
        SessionCampaignBindingApplyRequest request,
        CovenantWriteLease writeLease,
        CancellationToken cancellationToken);

}
