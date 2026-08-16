using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// The one production issuer of operator authority and Covenant read epochs.
/// </summary>
/// <remarks>
/// Lives in Core beside the values it mints, because their factories are internal on purpose: a
/// <see cref="CovenantAuthoritySnapshot"/> is an ordinary public record, so an issuer in another
/// assembly could only be given one that any caller could also fabricate. Here, the only reachable
/// input is the live <see cref="ICovenantAuthoritySnapshotProvider"/>, whose implementation is
/// internal to Infrastructure and is published only by the startup path that owns the core install
/// transaction (§10.12).
///
/// <para>Performs no secret-store access and no database I/O. It sits in front of every operator route,
/// and a boundary that had to query storage to learn whether it was authorized would be both slow and
/// circular.</para>
/// </remarks>
public sealed class OperatorAuthorityContextIssuer(ICovenantAuthoritySnapshotProvider authority)
    : IOperatorAuthorityContextIssuer
{

    private readonly ICovenantAuthoritySnapshotProvider _authority =
        authority ?? throw new ArgumentNullException(nameof(authority));

    /// <inheritdoc/>
    public Result<OperatorAuthorityContext> Issue(CovenantAuthorityRequirement requirement) =>
        OperatorAuthorityContext.Create(requirement, _authority.Current);

    /// <inheritdoc/>
    public Result<CovenantReadAuthorityEpoch> IssueReadEpoch() =>
        CovenantReadAuthorityEpoch.Create(_authority.Current);

    /// <inheritdoc/>
    public Result Revalidate(OperatorAuthorityContext context)
    {

        ArgumentNullException.ThrowIfNull(context);

        return context.Matches(_authority.Current)
            ? Result.Success()
            : Result.Failure(
                new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "Installation authority changed after this operation was authorized."));

    }

}
