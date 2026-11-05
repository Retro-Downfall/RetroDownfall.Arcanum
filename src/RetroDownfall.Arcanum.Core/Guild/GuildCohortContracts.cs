using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildCohortState>))]
public enum GuildCohortState
{
    [JsonStringEnumMemberName("open")]
    Open = 1,

    [JsonStringEnumMemberName("closed")]
    Closed = 2,

    [JsonStringEnumMemberName("draining")]
    Draining = 3,

    [JsonStringEnumMemberName("completed")]
    Completed = 4,

    [JsonStringEnumMemberName("failed")]
    Failed = 5,

    [JsonStringEnumMemberName("canceled")]
    Canceled = 6,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildCohortMemberState>))]
public enum GuildCohortMemberState
{
    [JsonStringEnumMemberName("active")]
    Active = 1,

    [JsonStringEnumMemberName("withdrawn")]
    Withdrawn = 2,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildMemberAssociationKind>))]
public enum GuildMemberAssociationKind
{
    [JsonStringEnumMemberName("primary")]
    Primary = 1,

    [JsonStringEnumMemberName("award-work")]
    AwardWork = 2,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildContributionOutcome>))]
public enum GuildContributionOutcome
{
    [JsonStringEnumMemberName("accepted")]
    Accepted = 1,

    [JsonStringEnumMemberName("failed")]
    Failed = 2,

    [JsonStringEnumMemberName("canceled")]
    Canceled = 3,

    [JsonStringEnumMemberName("rejected")]
    Rejected = 4,

    [JsonStringEnumMemberName("withdrawn")]
    Withdrawn = 5,

    [JsonStringEnumMemberName("invalid")]
    Invalid = 6,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildConvergenceKind>))]
public enum GuildConvergenceKind
{
    [JsonStringEnumMemberName("all")]
    All = 1,

    [JsonStringEnumMemberName("first")]
    First = 2,

    [JsonStringEnumMemberName("quorum")]
    Quorum = 3,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildConvergenceDecision>))]
public enum GuildConvergenceDecision
{
    [JsonStringEnumMemberName("success")]
    Success = 1,

    [JsonStringEnumMemberName("impossible")]
    Impossible = 2,

    [JsonStringEnumMemberName("canceled")]
    Canceled = 3,
}

public sealed record GuildRevisionRequest(
    Guid IdempotencyKey,
    long ExpectedRevision);

public sealed record GuildResultContract(string MimeType);

public sealed record GuildConvergencePolicy(
    GuildConvergenceKind Kind,
    int? Quorum);

public sealed record CreateCohortRequest(
    Guid IdempotencyKey,
    Guid IssuerCommissionId,
    string Name,
    GuildConvergencePolicy Policy,
    GuildResultContract ResultContract,
    GuildRequestedGrant? RequestedGrant);

public sealed record AdmitCohortMemberRequest(
    Guid IdempotencyKey,
    long ExpectedRevision,
    GuildIssueChildData Commission);

public sealed record CohortDto(
    Guid Id,
    string Name,
    Guid RootCommissionId,
    Guid IssuerCommissionId,
    Guid? CampaignId,
    GuildConvergencePolicy Policy,
    GuildResultContract ResultContract,
    GuildCohortState State,
    long Revision,
    long MembershipVersion,
    long? ClosedMembershipVersion,
    GuildGrant CohortGrant,
    Guid AllocationAccountId,
    long EventWatermark,
    GuildMutationReceiptDto? Receipt = null);

public sealed record CohortMemberDto(
    Guid Id,
    Guid CohortId,
    Guid PrimaryVotingCommissionId,
    Guid AgentId,
    long AdmissionSequence,
    GuildCohortMemberState State,
    bool InClosedSnapshot,
    long? RemovalSequence,
    long AssociatedCommissionCount,
    GuildMutationReceiptDto? Receipt = null);

public sealed record CohortMemberCommissionDto(
    Guid MemberId,
    Guid CommissionId,
    GuildMemberAssociationKind AssociationKind,
    long AssociationSequence);

public sealed record CohortContributionDto(
    Guid CohortId,
    Guid MemberId,
    Guid PrimaryVotingCommissionId,
    long ResultSequence,
    GuildContributionOutcome Outcome,
    GuildDataRef? ResultRef,
    string? AgreementDigest,
    bool Selected);

public sealed record CohortConvergenceDto(
    Guid CohortId,
    long Revision,
    GuildCohortState State,
    GuildConvergenceDecision? Decision,
    string? ReasonCode,
    GuildDataRef? SelectedResultRef,
    long FrozenMemberCount,
    long PendingMemberCount,
    long PendingDescendantCount,
    long CancelRequestedCount,
    GuildSpendDto Spend,
    long EventWatermark);

public sealed record CohortPageDto(
    ImmutableArray<CohortDto> Items,
    string? NextCursor,
    long SnapshotSequence,
    string StateConsistency = "livePerPage");

public sealed record CohortMemberPageDto(
    ImmutableArray<CohortMemberDto> Items,
    string? NextCursor,
    long SnapshotSequence,
    string StateConsistency = "livePerPage");

public sealed record CohortMemberCommissionPageDto(
    ImmutableArray<CohortMemberCommissionDto> Items,
    string? NextCursor,
    long SnapshotSequence,
    string StateConsistency = "livePerPage");

public sealed record CohortContributionPageDto(
    ImmutableArray<CohortContributionDto> Items,
    string? NextCursor,
    long SnapshotSequence,
    string StateConsistency = "livePerPage");
