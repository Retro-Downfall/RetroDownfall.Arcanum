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
/// <para>Every method returns a lease-bound result. The lease is held across serialization, so a
/// reset that lands mid-response is refused before the first byte rather than discovered by a client
/// holding half a page of content that no longer exists.</para>
/// </remarks>
public interface ICovenantManagementService
{

    ValueTask<Result<CovenantPageDto>> ListAsync(
        CovenantListRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantPageDto>> QueryAsync(
        CovenantQueryRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantDetailDto>> DetailAsync(
        CovenantDetailRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantVersionPageDto>> VersionsAsync(
        CovenantVersionsRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantSourcesDto>> SourcesAsync(
        CovenantSourcesRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantExplainDto>> ExplainAsync(
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
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantMutationPreflightDto>> PrepareRetireAsync(
        CovenantRetirePrepareRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantMutationResultDto>> SetAsync(
        CovenantSetRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantMutationResultDto>> RetireAsync(
        CovenantRetireRequest request,
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
        CancellationToken cancellationToken);

    ValueTask<Result<CampaignPathIdentityPlanDto>> PreparePathAsync(
        Guid campaignId,
        CampaignPathPrepareRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CampaignPathIdentityResultDto>> ApplyPathAsync(
        Guid campaignId,
        CampaignPathApplyRequest request,
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
        CancellationToken cancellationToken);

    ValueTask<Result<SessionCampaignBindingPlanDto>> PrepareBindingAsync(
        SessionCampaignBindingPrepareRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<SessionCampaignBindingResultDto>> ApplyBindingAsync(
        SessionCampaignBindingApplyRequest request,
        CancellationToken cancellationToken);

}
