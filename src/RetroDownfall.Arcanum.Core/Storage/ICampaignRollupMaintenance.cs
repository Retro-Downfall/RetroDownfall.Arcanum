using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>One borrowed authenticated request envelope, consumed by exactly one maintenance operation.</summary>
public sealed class CampaignRollupMaintenanceCapability
{
    private int _consumed;

    private CampaignRollupMaintenanceCapability(
        ArcanumInvocationContext invocation,
        SessionTurnClaimLease claimLease,
        CanonicalCampaignContext campaign,
        CovenantTurnLease authority,
        CovenantDigest configurationDigest)
    {
        Invocation = invocation;

        ClaimLease = claimLease;

        Campaign = campaign;

        Authority = authority;

        ConfigurationDigest = configurationDigest;
    }

    public ArcanumInvocationContext Invocation { get; }

    public SessionTurnClaimLease ClaimLease { get; }

    public CanonicalCampaignContext Campaign { get; }

    /// <summary>The caller owns this lease and retains it through guarded publication.</summary>
    public CovenantTurnLease Authority { get; }

    /// <summary>The exact settings accepted while this request capability was established.</summary>
    public CovenantDigest ConfigurationDigest { get; }

    public static Result<CampaignRollupMaintenanceCapability> Create(
        ArcanumInvocationContext invocation,
        SessionTurnClaimLease claimLease,
        CanonicalCampaignContext campaign,
        CovenantTurnLease authority,
        CovenantDigest configurationDigest)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        ArgumentNullException.ThrowIfNull(claimLease);

        ArgumentNullException.ThrowIfNull(authority);

        if (invocation.Surface is not ArcanumExecutionSurface.SessionBackedOperatorTurn
            || !invocation.CanReadCovenant
            || invocation.Campaign != campaign
            || !campaign.IsCampaignBound
            || !configurationDigest.IsValid
            || authority.Snapshot.Scope != CovenantOperationScope.ForCampaign(campaign.CampaignId!.Value)
            || !invocation.ReadAuthorityEpoch!.Matches(authority.Snapshot)
            || !claimLease.IsExecutable
            || claimLease.Claim.State is not SessionTurnClaimState.PendingMaintenance
            || claimLease.ExecutorId is null
            || claimLease.OwnerBootId is null
            || claimLease.LeaseDeadlineUtc is not { } deadline
            || deadline <= DateTimeOffset.UtcNow)
        {
            return new Error(ErrorCodes.Covenant.ForbiddenAuthority, "This request cannot authorize Campaign summary maintenance.");
        }

        return Result<CampaignRollupMaintenanceCapability>.Success(new(invocation, claimLease, campaign, authority, configurationDigest));
    }

    public Result Consume() => Interlocked.Exchange(ref _consumed, 1) == 0
        ? Result.Success()
        : Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "This Campaign maintenance capability has already been consumed."));
}

/// <summary>A content-free disposition of bounded maintenance work.</summary>
public sealed record CampaignRollupMaintenanceResult(
    int PublishedPages,
    bool Deferred,
    string? DeferredReason);

/// <summary>The shared clean-background and explicitly authorized request summary lane.</summary>
public interface ICampaignRollupMaintenance
{
    Task<Result<CampaignRollupMaintenanceCapability>> CreateCapabilityAsync(
        ArcanumInvocationContext invocation,
        SessionTurnClaimLease claimLease,
        CanonicalCampaignContext campaign,
        CovenantTurnLease maintenanceTurnLease,
        CancellationToken cancellationToken);

    Task<Result<CampaignRollupMaintenanceResult>> ProcessSessionAsync(
        Guid sessionId,
        long? upperSequence,
        CampaignRollupMaintenanceCapability? capability,
        CancellationToken cancellationToken);

    Task<Result<CampaignRollupMaintenanceResult>> ProcessCampaignAsync(
        Guid campaignId,
        CampaignRollupMaintenanceCapability? capability,
        CancellationToken cancellationToken);
}
