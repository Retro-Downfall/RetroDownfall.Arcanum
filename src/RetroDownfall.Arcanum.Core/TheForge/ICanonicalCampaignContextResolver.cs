using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.TheForge;

/// <summary>
/// Everything a caller may say about which Campaign a turn belongs to.
/// </summary>
/// <remarks>
/// Three optional inputs and no others. In particular there is no "assume the working directory wins"
/// flag and no way to declare a Campaign the resolver should trust without checking: the whole point of
/// the fill-and-verify table is that every supplied source is verified against every other one, and a
/// bypass parameter would be a way to skip that (§10.12).
/// </remarks>
public sealed record CanonicalCampaignResolutionRequest(
    Guid? SessionId,
    Guid? ExplicitCampaignId,
    string? WorkingDirectory);

/// <summary>
/// The one Campaign-resolution seam for inference.
/// </summary>
/// <remarks>
/// Runs before Session creation, Covenant reads, prompt construction, and internal-tool exposure, and
/// its answer is carried unchanged for the rest of the turn. It replaces the old behaviour in
/// <c>PingRequestResolver</c>, which returned early whenever a working directory was already present
/// and therefore let a caller-supplied path silently disagree with the Campaign it named.
/// </remarks>
public interface ICanonicalCampaignContextResolver
{

    ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
        CanonicalCampaignResolutionRequest request,
        CancellationToken cancellationToken);

}

/// <summary>
/// The immutable binding of one Session, as the resolver reads it.
/// </summary>
/// <remarks>
/// <see langword="null"/> from the reader means the Session does not exist, which is a failure. It is
/// deliberately distinct from a <see cref="SessionCampaignBindingKind.GlobalOnly"/> row, which is a
/// real and immutable answer: "this Session is bound to no Campaign, and never will be."
/// </remarks>
public sealed record SessionCampaignBindingRecord(
    Guid SessionId,
    SessionCampaignBinding Binding);

/// <summary>
/// One registered Campaign root, keyed by the opaque identity of the physical directory.
/// </summary>
/// <remarks>
/// Carries no path as authority. <see cref="Depth"/> exists so nested registrations resolve
/// most-specifically without a prefix comparison over path text, which is exactly the comparison that
/// makes <c>/work/app</c> and <c>/work/app-legacy</c> confusable.
/// </remarks>
public sealed record RegisteredCampaignIdentity(
    Guid CampaignId,
    uint PolicyVersion,
    long Revision,
    int Depth,
    CovenantDigest PhysicalIdentityDigest);

/// <summary>
/// Reads the exactly-one immutable Campaign binding of a Session.
/// </summary>
public interface ISessionCampaignBindingReader
{

    ValueTask<Result<SessionCampaignBindingRecord?>> FindAsync(
        Guid? sessionId,
        CancellationToken cancellationToken);

}

/// <summary>
/// Resolves a supplied working directory to the most specific registered Campaign root.
/// </summary>
/// <remarks>
/// The implementation opens the directory and its bounded physical ancestors with no-follow handles,
/// derives their versioned opaque identities, and queries the unique identity index with an
/// <c>IN</c> list. It never performs a path-prefix scan, because a prefix scan is what turns a rename,
/// a symlink, a bind mount, or a copied marker into someone else's Campaign authority.
/// </remarks>
public interface ICampaignPathIdentityReader
{

    /// <summary>
    /// The deepest registered Campaign containing <paramref name="workingDirectory"/>, if any.
    /// </summary>
    ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(
        string? workingDirectory,
        CancellationToken cancellationToken);

    /// <summary>
    /// The registered root of one Campaign, used to verify containment once a Campaign is established.
    /// </summary>
    ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(
        Guid campaignId,
        CancellationToken cancellationToken);

}

/// <summary>
/// Reports whether a Campaign still exists and which availability generation it is on.
/// </summary>
/// <remarks>
/// Separate from the path reader because a Campaign can exist with no registered root at all, and a
/// resolver that inferred existence from a path registration would fail closed on every Campaign an
/// operator created before path administration landed.
/// </remarks>
public interface ICampaignAvailabilityReader
{

    ValueTask<Result<long?>> FindAvailabilityGenerationAsync(
        Guid campaignId,
        CancellationToken cancellationToken);

}
