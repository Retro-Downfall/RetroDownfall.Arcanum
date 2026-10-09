using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>The exact source vector one authenticated pending claim may summarize.</summary>
public sealed record CampaignMaintenanceIdentity(
    SessionTurnClaimLease ClaimLease,
    CanonicalCampaignContext Campaign,
    CovenantMaintenanceStep Step,
    Guid? SourceSessionId,
    CovenantDigest SourceManifestDigest,
    long SourceGeneration,
    long ExpectedOutputRevision);

/// <summary>A durable physical-attempt allocation, or a committed checkpoint that cannot execute again.</summary>
public sealed record CampaignMaintenanceAttempt(
    CampaignMaintenanceIdentity Identity,
    long CheckpointRevision,
    ulong PhysicalProviderAttemptOrdinal,
    CovenantDigest ProviderCallDigest,
    CovenantMaintenanceCheckpoint State,
    Guid? OutputArtifactId,
    long? OutputRevision,
    long ExpectedClaimSensitivityRevision = 0);

/// <summary>Evidence publication validates and commits in the artifact's own transaction.</summary>
public sealed record CampaignMaintenancePublication(
    CampaignMaintenanceAttempt Attempt,
    CovenantDigest DisclosureReceiptDigest);

/// <summary>
/// Fences a maintenance attempt to the live pending claim and allocates physical ordinals durably.
/// Artifact publication alone commits its checkpoint; an acknowledged dispatch is never a result.
/// </summary>
public interface ICampaignMaintenanceCheckpointStore
{

    Task<Result> ValidateClaimAsync(
        SessionTurnClaimLease claimLease,
        CanonicalCampaignContext campaign,
        CancellationToken cancellationToken);

    Task<Result<SessionTurnClaimLease>> RenewClaimAsync(
        SessionTurnClaimLease claimLease,
        CanonicalCampaignContext campaign,
        CancellationToken cancellationToken);

    Task<Result<CampaignMaintenanceAttempt>> PrepareAttemptAsync(
        CampaignMaintenanceIdentity identity,
        CovenantDigest providerCallDigest,
        CancellationToken cancellationToken);

    Task<Result> ValidateAttemptAsync(
        CampaignMaintenanceAttempt attempt,
        CancellationToken cancellationToken);

    Task<Result<CampaignMaintenanceAttempt>> RecordDisclosureAsync(
        CampaignMaintenanceAttempt attempt,
        CovenantDigest disclosureReceiptDigest,
        CancellationToken cancellationToken);

}
