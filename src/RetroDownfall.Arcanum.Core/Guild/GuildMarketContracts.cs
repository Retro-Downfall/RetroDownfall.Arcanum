using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildWorkOfferState>))]
public enum GuildWorkOfferState
{
    [JsonStringEnumMemberName("open")]
    Open = 1,

    [JsonStringEnumMemberName("awarded")]
    Awarded = 2,

    [JsonStringEnumMemberName("withdrawn")]
    Withdrawn = 3,

    [JsonStringEnumMemberName("closed")]
    Closed = 4,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildWorkBidState>))]
public enum GuildWorkBidState
{
    [JsonStringEnumMemberName("submitted")]
    Submitted = 1,

    [JsonStringEnumMemberName("withdrawn")]
    Withdrawn = 2,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildWorkAwardState>))]
public enum GuildWorkAwardState
{
    [JsonStringEnumMemberName("committed")]
    Committed = 1,
}

public sealed record PostWorkOfferRequest(
    Guid IdempotencyKey,
    long ExpectedCohortRevision,
    string SkillId,
    string Goal,
    GuildRequestedGrant? RequestedGrant,
    GuildResultContract ResultContract);

/// <summary>Data for a bid authored through the verified member tool path.</summary>
public sealed record SubmitBidRequest(
    Guid IdempotencyKey,
    long ExpectedRevision,
    string Note,
    decimal? ClaimedCostUsd);

public sealed record AwardWorkRequest(
    Guid IdempotencyKey,
    long ExpectedRevision,
    long ExpectedCohortRevision,
    Guid BidId);

public sealed record WorkOfferDto(
    Guid Id,
    Guid CohortId,
    Guid IssuerCommissionId,
    long OfferSequence,
    long Revision,
    string SkillId,
    GuildResultContract ResultContract,
    GuildDataRef GoalRef,
    GuildWorkOfferState State,
    Guid? AwardId,
    Guid? CommissionId,
    GuildMutationReceiptDto? Receipt = null);

public sealed record WorkBidDto(
    Guid Id,
    Guid OfferId,
    Guid MemberId,
    Guid AgentId,
    long CardRevision,
    long BidSequence,
    long Revision,
    GuildWorkBidState State,
    GuildDataRef NoteRef,
    decimal? ClaimedCostUsd,
    GuildMutationReceiptDto? Receipt = null);

public sealed record WorkAwardDto(
    Guid Id,
    Guid OfferId,
    Guid BidId,
    Guid CommissionId,
    Guid MemberId,
    GuildWorkAwardState State,
    GuildMutationReceiptDto? Receipt = null);

public sealed record WorkOfferPageDto(
    ImmutableArray<WorkOfferDto> Items,
    string? NextCursor,
    long SnapshotSequence,
    string StateConsistency = "livePerPage");

public sealed record WorkBidPageDto(
    ImmutableArray<WorkBidDto> Items,
    string? NextCursor,
    long SnapshotSequence,
    string StateConsistency = "livePerPage");
