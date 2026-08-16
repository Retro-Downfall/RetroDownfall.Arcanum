using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// The single seam that mints operator authority, owned by the authenticated HTTP boundary.
/// </summary>
/// <remarks>
/// There is one implementation and it reads only the currently published authority snapshot. It
/// performs no secret-store access and no database I/O, because issuance sits in front of every
/// operator route and a boundary that had to query storage to learn whether it was authorized would be
/// both slow and circular (§10.12).
/// </remarks>
public interface IOperatorAuthorityContextIssuer
{

    /// <summary>
    /// Issues a context for exactly one requirement, or refuses when authority is unavailable.
    /// </summary>
    Result<OperatorAuthorityContext> Issue(CovenantAuthorityRequirement requirement);

    /// <summary>
    /// Issues the lighter read epoch an eligible inference surface carries.
    /// </summary>
    /// <remarks>
    /// Lives on the same interface because it is the same boundary, the same published snapshot, and
    /// the same clean-authority precondition. Splitting it into a second issuer would give the two a
    /// chance to disagree about whether this installation is clean.
    /// </remarks>
    Result<CovenantReadAuthorityEpoch> IssueReadEpoch();

    /// <summary>
    /// Reconfirms that a previously issued context still binds the current authority generation.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Issue"/> because a long operation must recheck rather than reissue. A
    /// reissue would silently adopt whatever generation is now current, which is exactly the mid-flight
    /// authority change the recheck exists to catch.
    /// </remarks>
    Result Revalidate(OperatorAuthorityContext context);

}
