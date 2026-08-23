using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Tower;

/// <summary>
/// The approved fill-and-verify table, as one pure function.
/// </summary>
/// <remarks>
/// Every supplied source is verified against every other one. The old behaviour returned as soon as any
/// source produced an answer, so a request naming Campaign C while standing in Campaign D's directory
/// silently got one of the two — and which one depended on argument order. Here that is a conflict
/// (§10.12).
///
/// <para>The asymmetry worth naming: an <em>unregistered</em> working directory contributes nothing when
/// no other source establishes a Campaign, but conflicts once one has. Before a Campaign is
/// established, an unregistered path is simply a path Arcanum knows nothing about. After one is
/// established, that same path is a claim that the turn is running somewhere the bound Campaign does
/// not contain, and ignoring it would let a workspace tool operate outside the memory scope the turn
/// was planned under.</para>
///
/// <para>Pure, and deliberately so. It performs no I/O and holds no clock, so every row of the table is
/// a literal test rather than a fixture.</para>
/// </remarks>
public static class CanonicalCampaignResolutionPolicy
{

    /// <summary>
    /// Applies the table to already-read facts.
    /// </summary>
    /// <param name="session">
    /// The Session's immutable binding, or <see langword="null"/> when the request supplied no Session.
    /// A supplied-but-missing Session is a failure the caller reports before reaching here.
    /// </param>
    /// <param name="explicitCampaignId">The Campaign the request named, if any.</param>
    /// <param name="workspace">The most specific registered Campaign containing the supplied path, if any.</param>
    /// <param name="workingDirectorySupplied">
    /// Whether a working directory was supplied at all. An unregistered supplied path is distinct from
    /// no path, and only the first can conflict.
    /// </param>
    /// <param name="campaignAvailabilityGeneration">
    /// The resolved Campaign's current availability generation, proving it still exists.
    /// </param>
    public static Result<CanonicalCampaignContext> Resolve(
        SessionCampaignBinding? session,
        Guid? explicitCampaignId,
        RegisteredCampaignIdentity? workspace,
        bool workingDirectorySupplied,
        long? campaignAvailabilityGeneration)
    {

        if (explicitCampaignId == Guid.Empty)
        {
            return Conflict("An empty Campaign identity cannot be resolved.");
        }

        if (session is { Kind: SessionCampaignBindingKind.LegacyUnresolved })
        {
            return Result<CanonicalCampaignContext>.Failure(
                new Error(
                    ErrorCodes.Session.CampaignBindingRequired,
                    "This Session predates immutable Campaign binding and must be resolved before it can be used."));
        }

        Guid? sessionCampaignId = session is { Kind: SessionCampaignBindingKind.Campaign }
            ? session.Value.CampaignId
            : null;

        bool sessionIsGlobalOnly = session is { Kind: SessionCampaignBindingKind.GlobalOnly };

        // An existing Global-only Session is an explicit immutable binding, not an absence. Any source
        // naming a Campaign contradicts it, and a binding that could be widened later would not be
        // immutable.
        if (sessionIsGlobalOnly)
        {

            if (explicitCampaignId is not null)
            {
                return Conflict("This Session is bound to no Campaign and cannot accept an explicit Campaign.");
            }

            if (workspace is not null)
            {
                return Conflict("This Session is bound to no Campaign and cannot run inside a registered Campaign root.");
            }

            return Result<CanonicalCampaignContext>.Success(CanonicalCampaignContext.GlobalOnly);

        }

        Guid? resolved = sessionCampaignId ?? explicitCampaignId ?? workspace?.CampaignId;

        if (resolved is null)
        {

            // No source established a Campaign. An unregistered supplied path contributes nothing here,
            // which is the one case where "Arcanum does not know this directory" is not an error.
            return Result<CanonicalCampaignContext>.Success(CanonicalCampaignContext.GlobalOnly);

        }

        if (sessionCampaignId is { } bound)
        {

            if (explicitCampaignId is { } named && named != bound)
            {
                return Conflict("The requested Campaign does not match this Session's immutable binding.");
            }

            if (workspace is { } inside && inside.CampaignId != bound)
            {
                return Conflict("The supplied working directory resolves to a different Campaign than this Session.");
            }

        }
        else if (explicitCampaignId is { } named2 && workspace is { } inside2 && inside2.CampaignId != named2)
        {
            return Conflict("The supplied working directory resolves to a different Campaign than the one requested.");
        }

        // Once a Campaign is established, a supplied path that resolves to nothing is an escape rather
        // than a shrug: the turn claims to be running somewhere the bound Campaign does not contain.
        if (workingDirectorySupplied && workspace is null)
        {
            return Conflict("The supplied working directory is not contained by the resolved Campaign's registered root.");
        }

        if (campaignAvailabilityGeneration is not { } generation)
        {
            return Result<CanonicalCampaignContext>.Failure(
                new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier."));
        }

        return Result<CanonicalCampaignContext>.Success(
            CanonicalCampaignContext.Create(
                SessionCampaignBinding.ForCampaign(resolved.Value),
                generation,
                workspace?.PolicyVersion ?? CampaignPathIdentityPolicy.Version,
                workspace?.Revision,
                workspace?.PhysicalIdentityDigest));

    }

    private static Result<CanonicalCampaignContext> Conflict(string message) =>
        Result<CanonicalCampaignContext>.Failure(
            new Error(ErrorCodes.Covenant.CampaignBindingConflict, message));

}

/// <summary>
/// The pinned derivation policy behind every Campaign physical-root identity.
/// </summary>
/// <remarks>
/// The version travels with every stored digest because the derivation inputs will change over time,
/// and a digest is only comparable against one derived the same way. Comparing across versions would
/// silently report every registered root as moved.
/// </remarks>
public static class CampaignPathIdentityPolicy
{

    /// <summary>The current derivation version.</summary>
    public const uint Version = 1;

    /// <summary>The domain-separation label mixed into every root identity.</summary>
    public const string Label = "Arcanum.Campaign.RootIdentity.v1";

    /// <summary>
    /// The deepest ancestor chain the resolver will consider for one supplied directory.
    /// </summary>
    /// <remarks>
    /// Bounded so a pathological path cannot turn one resolution into an unbounded walk, and so the
    /// identity <c>IN</c> list stays far below SQLite's parameter limit.
    /// </remarks>
    public const int MaxAncestorCandidates = 64;

}
