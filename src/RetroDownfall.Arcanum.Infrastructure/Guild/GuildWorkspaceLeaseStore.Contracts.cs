using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Canonical host-authenticated ownership; workspace ids are retention selectors, never physical isolation.</summary>
internal sealed record GuildWorkspaceLeaseOwner(GuildWorkspaceLeaseOwnerKind Kind, Guid PrincipalId,
    Guid? QuestId, Guid? RootQuestId, Guid? SessionId, Guid? CampaignId, Guid WorkspaceId, Guid InstallationGeneration);

internal enum GuildWorkspaceLeaseEffectKind
{
    Managed = 0,

    Process = 1,
}

/// <summary>Durable evidence only. Neither a row nor its public id authorizes an effect.</summary>
internal sealed record GuildWorkspaceLeaseRecord(Guid Id, GuildWorkspaceLeaseOwner Owner, string WorkspaceRoot,
    string IdentityJson, string IdentityDigest, Guid HostInstanceId, long Epoch, long Revision,
    GuildWorkspaceLeaseState State, DateTimeOffset HeartbeatAt, DateTimeOffset ExpiresAt,
    long InFlightMutationCount, GuildWorkspaceLeaseEffectKind EffectKind, string? ReasonCode,
    Guid IdempotencyKey, string RequestDigest);

internal sealed record GuildWorkspaceLeaseMutationReceipt(Guid Id, Guid IdempotencyKey, string RequestDigest,
    GuildAuthorityAction Action, DateTimeOffset AcceptedAt, GuildWorkspaceLeaseRecord Result);
