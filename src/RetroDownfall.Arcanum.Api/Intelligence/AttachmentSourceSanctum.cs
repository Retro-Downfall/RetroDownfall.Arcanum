using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Decides whether a Sanctum lets a live read of a workspace attachment go on, for the two operator routes that read one outside a turn:
/// creating a reference and refreshing one.
/// </summary>
/// <remarks>
/// The Campaign that governs the read is chosen the way it is for a turn, by <see cref="SanctumCampaignResolution"/>: the Session's binding,
/// the Campaign the Session row stores, and the registered root that contains the source's path are each a claim, and the stricter Sanctum
/// among the claims governs. The path that is claimed is the one the source is spelled with, not the one its links resolve to, so a link
/// that lies inside a Campaign's root and leaves it is still that Campaign's read. The Campaign's path boundary then judges the path the
/// opened handle resolves to, and the Campaign's mode decides what a refusal costs (<see cref="SanctumEnforcement.AllowsContinuation"/>):
/// a Strict Sanctum stops the read, an audit-only one lets a policy refusal through once the guard has recorded it, and no mode lets
/// through a Sanctum that could not be read or a breach that could not be recorded. A source no claim names is unrestricted.
/// </remarks>
internal static class AttachmentSourceSanctum
{
    /// <summary>The operation the guard is told the read is, which its breach row records.</summary>
    internal const string OperationType = "attachment source read";

    /// <summary>
    /// The authorizer for one Session's source reads. <paramref name="sessionCampaignId"/> is the Campaign the Session row stores;
    /// <paramref name="surfaceName"/> is the name the guard records the read under.
    /// </summary>
    internal static AttachmentSourcePathAuthorizer Authorizer(
        Guid sessionId,
        Guid? sessionCampaignId,
        string surfaceName,
        ICanonicalCampaignContextResolver resolver,
        ICampaignRepository campaigns,
        ISanctumGuard guard,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceName);

        ArgumentNullException.ThrowIfNull(resolver);

        ArgumentNullException.ThrowIfNull(campaigns);

        ArgumentNullException.ThrowIfNull(guard);

        ArgumentNullException.ThrowIfNull(logger);

        return async (read, cancellationToken) =>
        {
            SanctumCampaignResolution.Resolved governing = await SanctumCampaignResolution
                .ResolveAsync(
                    sessionId,
                    sessionCampaignId,
                    read.SourcePath,
                    known: null,
                    resolver,
                    campaigns,
                    cancellationToken)
                .ConfigureAwait(false);

            if (governing.PolicyUnreadable || (governing.CampaignId is not null && governing.Loaded is null))
            {
                // A Campaign whose Sanctum cannot be read is not one whose Sanctum is off. Nothing was evaluated, so no breach is recorded
                // (there may be no Campaign row to hold one) and no mode can override the refusal.
                logger.LogWarning(
                    "The Sanctum policy of the Campaign {CampaignId} that governs an attachment source read could not be read; the read is refused.",
                    governing.CampaignId);

                return false;
            }

            if (governing.CampaignId is not { } campaignId || governing.Loaded is not { } campaign)
            {
                return true;
            }

            SanctumResult verdict = await guard
                .ValidatePathAsync(
                    campaignId.ToString("D"),
                    read.CanonicalPath,
                    OperationType,
                    surfaceName,
                    cancellationToken)
                .ConfigureAwait(false);

            return SanctumEnforcement.AllowsContinuation(verdict, CampaignRepository.GetSanctumConfig(campaign).Mode);
        };
    }
}
