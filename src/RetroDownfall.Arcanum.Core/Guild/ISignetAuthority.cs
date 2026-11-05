using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Host-only authority seam. Mint and attenuation participate in coordinator atomic admission; a handle may escape only after commit. Recovery rebind preserves existing immutable authority.</summary>
public interface ISignetAuthority
{
    Task<Result<GuildExecutionHandle>> MintAsync(GuildAdmissionContext admission, GuildSignetBinding binding, CancellationToken cancellationToken);

    Task<Result<GuildExecutionHandle>> AttenuateAsync(GuildAdmissionContext admission, GuildExecutionHandle parent, GuildSignetBinding child, GuildRequestedGrant request, CancellationToken cancellationToken);

    Task<Result<GuildVerifiedAuthority>> VerifyAsync(GuildExecutionHandle execution, GuildActionRequest action, CancellationToken cancellationToken);

    Task<Result<SignetRevocationDto>> RevokeAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid signetId, GuildRevokeSignetRequest request, CancellationToken cancellationToken);

    Task<Result<GuildExecutionHandle>> RebindAsync(GuildRecoveryContext recovery, CancellationToken cancellationToken);
}
