using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildNoteKind>))]
public enum GuildNoteKind
{
    [JsonStringEnumMemberName("observation")]
    Observation = 1,

    [JsonStringEnumMemberName("proposal")]
    Proposal = 2,

    [JsonStringEnumMemberName("correction")]
    Correction = 3,
}

/// <summary>Trusted relationship projection; deserialized data confers no authority.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildAuthorityKind>))]
public enum GuildAuthorityKind
{
    [JsonStringEnumMemberName("legacyUnknown")]
    LegacyUnknown = 0,

    [JsonStringEnumMemberName("operator")]
    Operator = 1,

    [JsonStringEnumMemberName("parent")]
    Parent = 2,

    [JsonStringEnumMemberName("peer")]
    Peer = 3,

    [JsonStringEnumMemberName("child")]
    Child = 4,
}

public sealed record AppendSharedNoteRequest(
    Guid IdempotencyKey,
    long ExpectedRevision,
    GuildNoteKind Kind,
    Guid? SubjectId,
    string Body,
    Guid? CorrectsNoteId);

public sealed record SharedNoteDto(
    Guid Id,
    Guid CohortId,
    long Sequence,
    Guid? AuthorQuestId,
    Guid? AuthorMemberId,
    GuildAuthorityKind Authority,
    GuildNoteKind Kind,
    Guid? SubjectId,
    GuildDataRef? BodyRef,
    Guid? CorrectsNoteId,
    DateTimeOffset CreatedAt,
    GuildMutationReceiptDto? Receipt = null,
    Guid? OperatorPrincipalId = null,
    string? Body = null);

public sealed record SharedNotePageDto(
    ImmutableArray<SharedNoteDto> Items,
    string? NextCursor,
    long SnapshotSequence,
    string StateConsistency = "livePerPage");
