using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Proof that the installation's master API key was presented for exactly one declared requirement.
/// </summary>
/// <remarks>
/// This is the operator's authority, and the model never holds it. It is established only at the
/// authenticated HTTP boundary after the existing constant-time key comparison succeeds, and it is
/// nonserializable so that internal inference, MCP arguments, repositories, and deserialized request
/// types cannot construct one (§10.12).
///
/// <para>It binds four facts a later caller can be checked against: the single
/// <see cref="Requirement"/> it was issued for, the clean <see cref="AuthorityEpoch"/>, the
/// <see cref="MasterKeyVersion"/> in force, and the <see cref="InstallationIdentity"/>. Binding the
/// key version is what makes a rotation invalidate work in flight rather than letting a context minted
/// under the old key authorize an effect under the new one.</para>
///
/// <para><see cref="IssuerNonce"/> is fresh per issuance. It exists so a durable receipt can record
/// which issuance authorized an effect without recording anything about the key itself.</para>
/// </remarks>
public sealed class OperatorAuthorityContext
{

    private OperatorAuthorityContext(
        CovenantAuthorityRequirement requirement,
        string installationIdentity,
        long authorityEpoch,
        uint masterKeyVersion,
        Guid issuerNonce)
    {

        Requirement = requirement;

        InstallationIdentity = installationIdentity;

        AuthorityEpoch = authorityEpoch;

        MasterKeyVersion = masterKeyVersion;

        IssuerNonce = issuerNonce;

    }

    /// <summary>The single requirement this context satisfies.</summary>
    public CovenantAuthorityRequirement Requirement { get; }

    /// <summary>The installation this authority belongs to.</summary>
    public string InstallationIdentity { get; }

    /// <summary>The clean authority epoch in force at issuance.</summary>
    public long AuthorityEpoch { get; }

    /// <summary>The master-key version in force at issuance.</summary>
    public uint MasterKeyVersion { get; }

    /// <summary>One fresh nonce per issuance, safe to record in a content-free receipt.</summary>
    public Guid IssuerNonce { get; }

    /// <summary>
    /// Whether this context was issued for exactly the requirement the caller needs.
    /// </summary>
    public bool Satisfies(CovenantAuthorityRequirement requirement) => Requirement == requirement;

    /// <summary>
    /// Whether the authority generation and key version this context bound are still current.
    /// </summary>
    public bool Matches(CovenantAuthoritySnapshot? current) =>
        current is not null
        && current.HostToolsState is CovenantHostToolsState.Clean
        && current.AuthorityEpoch == AuthorityEpoch
        && current.MasterKeyVersion == MasterKeyVersion
        && string.Equals(current.InstallationIdentity, InstallationIdentity, StringComparison.Ordinal);

    /// <summary>
    /// Issues a context from a committed clean authority snapshot.
    /// </summary>
    /// <remarks>
    /// Internal because issuance belongs to the authenticated boundary alone. The production issuer is
    /// <see cref="IOperatorAuthorityContextIssuer"/>; nothing else in the closure can reach this.
    /// </remarks>
    internal static Result<OperatorAuthorityContext> Create(
        CovenantAuthorityRequirement requirement,
        CovenantAuthoritySnapshot? snapshot)
    {

        if (!Enum.IsDefined(requirement))
        {
            return Result<OperatorAuthorityContext>.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "An undefined authority requirement cannot be issued."));
        }

        if (snapshot is null)
        {
            return Result<OperatorAuthorityContext>.Failure(
                new Error(
                    ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                    "Installation authority has not been established."));
        }

        if (snapshot.HostToolsState is not CovenantHostToolsState.Clean)
        {
            return Result<OperatorAuthorityContext>.Failure(
                new Error(
                    ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                    "This installation carries a host-process tools taint and grants no operator authority."));
        }

        if (string.IsNullOrWhiteSpace(snapshot.InstallationIdentity)
            || snapshot.AuthorityEpoch <= 0
            || snapshot.MasterKeyVersion == 0)
        {
            return Result<OperatorAuthorityContext>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The published authority snapshot is malformed."));
        }

        return Result<OperatorAuthorityContext>.Success(
            new OperatorAuthorityContext(
                requirement,
                snapshot.InstallationIdentity,
                snapshot.AuthorityEpoch,
                snapshot.MasterKeyVersion,
                Guid.NewGuid()));

    }

    /// <summary>
    /// Builds a context directly, for tests that have no live authority publisher.
    /// </summary>
    internal static OperatorAuthorityContext CreateForTests(
        CovenantAuthorityRequirement requirement,
        Guid installationIdentity,
        long authorityEpoch,
        uint masterKeyVersion) =>
        new(
            requirement,
            installationIdentity.ToString().ToUpperInvariant(),
            authorityEpoch,
            masterKeyVersion,
            Guid.NewGuid());

}
