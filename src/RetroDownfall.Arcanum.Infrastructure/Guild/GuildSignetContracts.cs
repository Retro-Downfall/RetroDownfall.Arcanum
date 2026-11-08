using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Canonical owner and workspace pins supplied by trusted coordinator admission, never caller ids.</summary>
internal sealed record GuildSignetOwnerBinding(Guid? SessionId, Guid? CampaignId, Guid? WorkspaceId,
    string? PeerId = null, string? PeerGrantRevision = null, uint? WorkspaceIdentityPolicyVersion = null,
    long? WorkspaceIdentityRevision = null, byte[]? WorkspaceRootIdentity = null);

internal sealed record GuildSignetRecord(GuildSignetBinding Binding, GuildSignetOwnerBinding Owners,
    Guid InstallationGeneration, Guid AuthorityEpoch, long Revision, long CredentialGeneration,
    byte[] TokenDigest, long RevocationGeneration, bool Revoked, bool RestoredInert, bool Retired, bool CancellationRequested);

internal sealed record GuildSignetPolicyEvidence(GuildGrant HostGrant, GuildGrantScopeEvidence ScopeEvidence);

internal enum GuildSignetIssuanceEdge
{
    Root,

    Local,

    Remote,

    LocalCohort,

    RemoteCohort,
}

internal sealed record GuildSignetAdmissionEvidence(GuildSignetOwnerBinding Owners, GuildSignetIssuanceEdge Edge);

/// <summary>
/// Authentication and coordinator participant boundary. An implementation must recognize host-issued
/// admission/recovery claims, canonical ownership and current peer/config policy; ids alone never qualify.
/// Production installs only an unavailable policy until the coordinator delivery slice supplies authenticated authority.
/// </summary>
internal interface IGuildSignetHostPolicy
{
    Guid InstallationGeneration { get; }

    Task<Result<GuildSignetAdmissionEvidence>> AuthorizeAdmissionAsync(GuildAdmissionContext admission,
        GuildSignetBinding binding, CancellationToken cancellationToken);

    Task<Result<GuildSignetPolicyEvidence>> ResolveCurrentAsync(ImmutableArray<GuildSignetRecord> lineage,
        CancellationToken cancellationToken);

    Task<Result> AuthorizeRevocationAsync(GuildCallerContext caller, GuildSignetRecord target, CancellationToken cancellationToken);

    Task<Result> AuthorizeRecoveryAsync(GuildRecoveryContext recovery, GuildSignetRecord target, CancellationToken cancellationToken);
}
