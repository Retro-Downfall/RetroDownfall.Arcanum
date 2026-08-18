using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.DataLifecycle;

/// <summary>
/// One artifact a direct-deletion route intends to remove.
/// </summary>
/// <remarks>
/// Identity and kind only. Whether the artifact is labelled, which owner scope it belongs to, and what
/// its current revision and digest are, are all facts the purger reads from the database inside its own
/// transaction — a caller-supplied label is exactly how a confused or hostile request talks a deleter
/// into removing something it never described (§10.20.2).
/// </remarks>
public sealed record CovenantSensitivePurgeTarget
{

    public CovenantSensitivePurgeTarget(SensitiveArtifactKind kind, Guid artifactId)
    {

        Kind = CovenantSensitiveArtifactPurgePolicy.IsCovered(kind)
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(kind));

        ArtifactId = CovenantValidation.RequireNonEmpty(artifactId, nameof(artifactId));

    }

    public SensitiveArtifactKind Kind { get; }

    public Guid ArtifactId { get; }

}

/// <summary>
/// What the purger did about one target.
/// </summary>
/// <remarks>
/// Three arms, and the separation is the whole contract with the caller. <see cref="Unlabeled"/> is an
/// instruction — the route performs its ordinary delete, because nothing here is protected.
/// <see cref="Purged"/> means the shared kernel already removed the artifact, its projections, and its
/// label, so a route that then ran its own delete would be deleting a second time and could not tell an
/// already-gone row from one somebody else removed. <see cref="Blocked"/> means the artifact is still
/// there and must stay there.
///
/// <para>No member is zero, so a default-initialized disposition can never read as permission to
/// delete.</para>
/// </remarks>
public enum CovenantSensitivePurgeDisposition : byte
{

    /// <summary>No live label. The caller performs its ordinary delete.</summary>
    Unlabeled = 1,

    /// <summary>Labelled, and removed in full through the shared kernel. The caller deletes nothing.</summary>
    Purged = 2,

    /// <summary>Labelled, and left exactly as found. The caller deletes nothing.</summary>
    Blocked = 3,

}

/// <summary>The disposition of one target.</summary>
public sealed record CovenantSensitivePurgeResult(
    Guid ArtifactId,
    SensitiveArtifactKind Kind,
    CovenantSensitivePurgeDisposition Disposition,
    CovenantErasureBlocker Blocker);

/// <summary>
/// The content-free outcome of one purge call.
/// </summary>
public sealed record CovenantSensitivePurgeOutcome(
    IReadOnlyList<CovenantSensitivePurgeResult> Results,
    CovenantArtifactErasureProgress Progress)
{

    /// <summary>Nothing in this batch carried a label, so the installation has no protected state here.</summary>
    public bool AllUnlabeled =>
        Results.All(static result => result.Disposition is CovenantSensitivePurgeDisposition.Unlabeled);

    /// <summary>At least one labelled artifact could not be removed and is still present.</summary>
    public bool IsBlocked =>
        Results.Any(static result => result.Disposition is CovenantSensitivePurgeDisposition.Blocked);

    /// <summary>Whether the caller should still perform its own ordinary delete for this artifact.</summary>
    public bool RequiresOrdinaryDelete(Guid artifactId) =>
        Results.Any(result =>
            result.ArtifactId == artifactId
            && result.Disposition is CovenantSensitivePurgeDisposition.Unlabeled);

    /// <summary>Whether the shared kernel removed this artifact.</summary>
    public bool WasPurged(Guid artifactId) =>
        Results.Any(result =>
            result.ArtifactId == artifactId
            && result.Disposition is CovenantSensitivePurgeDisposition.Purged);

}

/// <summary>
/// The one narrow boundary every direct-deletion route dispatches a potentially labelled artifact
/// through.
/// </summary>
/// <remarks>
/// Deliberately narrow. It resolves each target's live label, and for a labelled one delegates to the
/// shared database or managed-file erasure kernel; it contains no second purge, capability-open,
/// ownership-verification, compare-delete, fsync, or label-removal algorithm, and creates no enum
/// value, numeric mapping, policy switch, or second registry. Everything it knows about how to delete
/// an artifact comes from <see cref="CovenantSensitiveArtifactPurgePolicy"/>.
///
/// <para>An installation with no Covenant arm answers every target <see cref="CovenantSensitivePurgeDisposition.Unlabeled"/>
/// without acquiring anything, so the six routes behave exactly as they did before this boundary
/// existed (§10.20.2).</para>
/// </remarks>
public interface ICovenantSensitiveArtifactPurger
{

    /// <summary>The largest number of artifacts one purge call may carry.</summary>
    public const int MaxTargets = 256;

    ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(
        IReadOnlyList<CovenantSensitivePurgeTarget> targets,
        CancellationToken cancellationToken = default);

}

/// <summary>
/// The request-scoped slot the authenticated boundary publishes a retention-purge authority into.
/// </summary>
/// <remarks>
/// Registered scoped, so the object is the request's own and there is nothing process-wide to race on.
/// Write-once by construction: a filter publishes the context the pre-binding middleware already
/// issued, and nothing downstream can replace it. A repository that could swap the authority would let
/// a route act under a context minted for a different requirement, and a body that could publish one
/// would be authority arriving from the network (§10.12).
///
/// <para>The value itself is unforgeable independently of this class —
/// <c>OperatorAuthorityContext</c> has no public constructor and is minted only by
/// <see cref="IOperatorAuthorityContextIssuer"/> — so write-once is about ordering, not about
/// trust.</para>
/// </remarks>
public sealed class CovenantSensitivePurgeAuthorityScope
{

    private OperatorAuthorityContext? _context;

    /// <summary>The authority issued for this request, or null when none was.</summary>
    public OperatorAuthorityContext? Current => Volatile.Read(ref _context);

    /// <summary>
    /// Publishes the already-issued authority for this request.
    /// </summary>
    /// <remarks>
    /// Refuses a context issued for anything but
    /// <see cref="CovenantAuthorityRequirement.SensitivityRetentionPurge"/>, and refuses a second
    /// publication outright rather than overwriting the first.
    /// </remarks>
    public Result Publish(OperatorAuthorityContext context)
    {

        ArgumentNullException.ThrowIfNull(context);

        if (!context.Satisfies(CovenantAuthorityRequirement.SensitivityRetentionPurge))
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "A sensitivity purge scope accepts only authority issued for a retention purge.");

        }

        if (Interlocked.CompareExchange(ref _context, context, null) is not null)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "The retention-purge authority for this request has already been published.");

        }

        return Result.Success();

    }

}
