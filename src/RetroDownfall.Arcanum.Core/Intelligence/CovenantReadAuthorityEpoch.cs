using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Proof that an authenticated boundary observed a clean authority generation for this request.
/// </summary>
/// <remarks>
/// Nonserializable, and constructible only from a committed <see cref="CovenantAuthoritySnapshot"/>.
/// That is the whole design: a request body, an MCP argument, a deserialized DTO, or a repository can
/// name an epoch, but none of them can produce this object, so none of them can assert that the
/// installation was clean when the request arrived (§10.12).
///
/// <para>It carries no key, no fingerprint, and no requirement. It answers exactly one question —
/// which clean authority generation authorized this read — and an object that answered more would be
/// a way for a read boundary to hand out mutation reach it never earned. Mutation and recovery use
/// <see cref="OperatorAuthorityContext"/>, which binds a requirement and a master-key version.</para>
///
/// <para>A tainted or pending-taint installation yields no epoch at all. Once same-identity code could
/// have recovered this installation's credentials, there is no clean generation left to bind to, and
/// returning a "best effort" epoch would be indistinguishable from returning a real one.</para>
/// </remarks>
public sealed class CovenantReadAuthorityEpoch
{

    private CovenantReadAuthorityEpoch(
        string installationIdentity,
        long runtimeAuthorityGeneration,
        long authorityEpoch)
    {

        InstallationIdentity = installationIdentity;

        RuntimeAuthorityGeneration = runtimeAuthorityGeneration;

        AuthorityEpoch = authorityEpoch;

    }

    /// <summary>The installation this epoch belongs to. Identities are not comparable across them.</summary>
    public string InstallationIdentity { get; }

    /// <summary>The process-local authority generation observed at the boundary.</summary>
    public long RuntimeAuthorityGeneration { get; }

    /// <summary>The monotonic clean authority epoch observed at the boundary.</summary>
    public long AuthorityEpoch { get; }

    /// <summary>
    /// Issues an epoch from a committed authority snapshot, or refuses when authority is not clean.
    /// </summary>
    /// <remarks>
    /// Internal because a <see cref="CovenantAuthoritySnapshot"/> is an ordinary public record that any
    /// caller could construct. Routing issuance through <see cref="OperatorAuthorityContextIssuer"/>,
    /// which reads only the published provider, is what makes a fabricated snapshot useless.
    /// </remarks>
    internal static Result<CovenantReadAuthorityEpoch> Create(CovenantAuthoritySnapshot? snapshot)
    {

        if (snapshot is null)
        {
            return Result<CovenantReadAuthorityEpoch>.Failure(
                new Error(
                    ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                    "Installation authority has not been established."));
        }

        if (snapshot.HostToolsState is not CovenantHostToolsState.Clean)
        {
            return Result<CovenantReadAuthorityEpoch>.Failure(
                new Error(
                    ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                    "This installation carries a host-process tools taint and has no clean authority epoch."));
        }

        if (string.IsNullOrWhiteSpace(snapshot.InstallationIdentity)
            || snapshot.RuntimeAuthorityGeneration <= 0
            || snapshot.AuthorityEpoch <= 0)
        {
            return Result<CovenantReadAuthorityEpoch>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The published authority snapshot is malformed."));
        }

        return Result<CovenantReadAuthorityEpoch>.Success(
            new CovenantReadAuthorityEpoch(
                snapshot.InstallationIdentity,
                snapshot.RuntimeAuthorityGeneration,
                snapshot.AuthorityEpoch));

    }

    /// <summary>
    /// Whether this epoch still matches the authority currently published.
    /// </summary>
    public bool Matches(CovenantAuthoritySnapshot? current) =>
        current is not null
        && current.HostToolsState is CovenantHostToolsState.Clean
        && current.RuntimeAuthorityGeneration == RuntimeAuthorityGeneration
        && current.AuthorityEpoch == AuthorityEpoch
        && string.Equals(current.InstallationIdentity, InstallationIdentity, StringComparison.Ordinal);

    /// <summary>Whether a newly acquired turn lease belongs to this exact request authority epoch.</summary>
    public bool Matches(CovenantOperationLeaseSnapshot lease) =>
        lease.RuntimeAuthorityGeneration == RuntimeAuthorityGeneration
        && lease.AuthorityEpoch == AuthorityEpoch;

    /// <summary>
    /// Builds an epoch directly, for tests that have no live authority publisher.
    /// </summary>
    internal static CovenantReadAuthorityEpoch CreateForTests(
        Guid installationIdentity,
        long runtimeAuthorityGeneration,
        long authorityEpoch) =>
        new(
            installationIdentity.ToString().ToUpperInvariant(),
            CovenantValidation.RequirePositive(runtimeAuthorityGeneration, nameof(runtimeAuthorityGeneration)),
            CovenantValidation.RequirePositive(authorityEpoch, nameof(authorityEpoch)));

}
