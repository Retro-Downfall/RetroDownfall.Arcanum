using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Decides which Campaign's Sanctum governs a turn or a web-research run.
/// </summary>
/// <remarks>
/// A Sanctum is a property of the Campaign a turn is bound to, never of how the working directory happens to be spelled. The old rule
/// switched it on only when the request's working directory string equalled the registered path exactly, so a turn launched from
/// <c>&lt;campaign&gt;/src</c>, from the root spelled with a trailing separator, or by a Session with no working directory at all stayed bound to
/// a Strict-Sanctum Campaign and ran with no Sanctum.
///
/// <para>The Campaign is, in order:</para>
/// <list type="number">
/// <item>The one the turn is canonically bound to: the invocation's canonical context when the surface resolved one, otherwise the answer of
/// <see cref="ICanonicalCampaignContextResolver"/> for the request's Session, explicit Campaign and working directory (internal callers pass
/// no canonical context, and a subagent or an Apprentice is as contained as the operator's own turn). That resolution recognises a working
/// directory by the physical identity of its registered root and its ancestors, so a case or symlink variant of the path is the same
/// Campaign.</item>
/// <item>Otherwise the registered Campaign whose root contains the working directory
/// (<see cref="ICampaignRepository.GetByContainedPathAsync"/>). The physical identity of a root is only known once the root has been
/// registered, and the Campaign table's path is the registry every installation has, so a directory below it, or the root spelled with a
/// trailing separator, is still that Campaign's workspace.</item>
/// <item>Otherwise, only when no canonical resolution answered at all, the Campaign the request names.</item>
/// </list>
/// A directory that no registered root contains, on a turn bound to no Campaign, stays unrestricted.
/// </remarks>
internal static class SanctumCampaignResolution
{
    /// <summary>The governing Campaign: its identifier, and its row when finding it already read it.</summary>
    internal readonly record struct Resolved(Guid? CampaignId, Campaign? Loaded);

    internal static async Task<Resolved> ResolveAsync(
        Guid? sessionId,
        Guid? explicitCampaignId,
        string? workingDirectory,
        CanonicalCampaignContext? known,
        ICanonicalCampaignContextResolver resolver,
        ICampaignRepository campaigns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        ArgumentNullException.ThrowIfNull(campaigns);

        string? directory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : workingDirectory;

        bool canonicalAnswered = false;

        Guid? canonicalId = null;

        if (known is { } resolved && resolved != default)
        {
            canonicalAnswered = true;

            canonicalId = resolved.CampaignId;
        }
        else if ((sessionId is not null || explicitCampaignId is not null || directory is not null))
        {
            Result<CanonicalCampaignContext> answer = await resolver
                .ResolveAsync(
                    new CanonicalCampaignResolutionRequest(sessionId, explicitCampaignId, directory),
                    cancellationToken)
                .ConfigureAwait(false);

            // A refusal (a conflict, a Session that cannot be read) says nothing about which Campaign governs. It is not "no Campaign",
            // so the registered-root lookup below still applies.
            if (answer.IsSuccess)
            {
                canonicalAnswered = true;

                canonicalId = answer.Value.CampaignId;
            }
        }

        if (canonicalId is { } bound)
        {
            return new Resolved(bound, null);
        }

        if (directory is not null)
        {
            Campaign? containing = await campaigns
                .GetByContainedPathAsync(directory, cancellationToken)
                .ConfigureAwait(false);

            if (containing is not null)
            {
                return new Resolved(containing.Id, containing);
            }
        }

        return !canonicalAnswered && explicitCampaignId is { } named
            ? new Resolved(named, null)
            : default;
    }
}
