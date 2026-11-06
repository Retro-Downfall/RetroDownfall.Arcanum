using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildWorkspaceLeaseState>))]
public enum GuildWorkspaceLeaseState
{
    [JsonStringEnumMemberName("held")]
    Held = 1,

    [JsonStringEnumMemberName("draining")]
    Draining = 2,

    [JsonStringEnumMemberName("released")]
    Released = 3,

    [JsonStringEnumMemberName("reconciliation-required")]
    ReconciliationRequired = 4,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildWorkspaceLeaseOwnerKind>))]
public enum GuildWorkspaceLeaseOwnerKind
{
    [JsonStringEnumMemberName("quest")]
    Quest = 1,

    [JsonStringEnumMemberName("operator")]
    Operator = 2,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildPathScopeKind>))]
public enum GuildPathScopeKind
{
    [JsonStringEnumMemberName("file")]
    File = 1,

    [JsonStringEnumMemberName("subtree")]
    Subtree = 2,
}

public sealed record GuildPathScope(
    string RelativePath,
    GuildPathScopeKind Kind);

public sealed record AcquireWorkspaceLeaseRequest(
    Guid IdempotencyKey,
    Guid WorkspaceId,
    ImmutableArray<GuildPathScope> Scopes);

public sealed record RenewWorkspaceLeaseRequest(
    Guid IdempotencyKey,
    long ExpectedRevision,
    long Epoch);

public sealed record ReleaseWorkspaceLeaseRequest(
    Guid IdempotencyKey,
    long ExpectedRevision,
    long Epoch);

/// <summary>A lease observation; its identity is a selector and never a writer credential.</summary>
public sealed record WorkspaceLeaseDto(
    Guid Id,
    Guid WorkspaceId,
    GuildWorkspaceLeaseOwnerKind OwnerKind,
    Guid? QuestId,
    Guid? RootQuestId,
    ImmutableArray<GuildPathScope> Scopes,
    GuildWorkspaceLeaseState State,
    long Epoch,
    long Revision,
    DateTimeOffset ExpiresAt,
    long InFlightMutationCount,
    string? ReasonCode,
    GuildMutationReceiptDto? Receipt = null);

public sealed record WorkspaceLeasePageDto(
    ImmutableArray<WorkspaceLeaseDto> Items,
    string? NextCursor,
    long SnapshotSequence,
    string StateConsistency = "livePerPage");
