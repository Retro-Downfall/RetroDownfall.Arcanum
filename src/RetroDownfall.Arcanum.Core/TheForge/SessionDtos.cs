using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record CreateSessionRequest(
    Guid? CampaignId,
    string? Title);

public sealed record UpdateSessionRequest(
    string? Title,
    string? Status);

public sealed record AppendEntryRequest(
    MessageRole Role,
    string Content,
    string? ModelUsed = null);

public sealed record SessionSummaryDto(
    Guid Id,
    Guid? CampaignId,
    string? Title,
    string Status,
    int EntryCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? ForkedFromSessionId = null);

public sealed record SessionDetailDto(
    Guid Id,
    Guid? CampaignId,
    string? Title,
    string Status,
    int EntryCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Summary,
    long TotalTokensUsed,
    Guid? ForkedFromSessionId = null,
    decimal TotalCostUsd = 0);

/// <summary>
/// Body for <c>POST /api/sessions/{id}/fork</c>. All fields optional: <paramref name="Title"/>
/// defaults to <c>"Fork of {source title}"</c>; <paramref name="UpToEntryId"/> copies only entries
/// up to and including that entry (must belong to the source session) instead of the full
/// transcript; <paramref name="CampaignId"/> defaults to the source session's campaign.
/// </summary>
public sealed record ForkSessionRequest(
    string? Title = null,
    Guid? UpToEntryId = null,
    Guid? CampaignId = null);

public sealed record EntryDto(
    Guid Id,
    Guid SessionId,
    string Role,
    string Content,
    string? ToolCallId,
    string? ToolName,
    DateTimeOffset CreatedAt,
    bool IsPinned = false);

public sealed record SessionQueryRequest(
    Guid? CampaignId = null,
    string? Status = null,
    string? Search = null,
    string? Title = null,
    MessageRole? Role = null,
    string? Model = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int? Limit = null,
    DateTimeOffset? BeforeUpdatedAt = null);

public sealed record SessionQueryResult(
    SessionSummaryDto[] Summaries,
    DateTimeOffset? NextBeforeUpdatedAt,
    bool HasMore);

public sealed record SessionAnalytics(
    int TotalSessions,
    int ActiveSessions,
    int ArchivedSessions,
    int TotalEntries,
    int UserEntries,
    int AssistantEntries,
    int ToolEntries,
    int SystemEntries,
    long TotalTokensUsed,
    Dictionary<string, int> EntriesByModel);

[JsonConverter(typeof(JsonStringEnumConverter<SessionExportFormat>))]
public enum SessionExportFormat
{

    [JsonStringEnumMemberName("json")]
    Json,

    [JsonStringEnumMemberName("markdown")]
    Markdown,

}

public sealed record SessionExportResult(
    Guid SessionId,
    string Format,
    string Content,
    string ContentType);

public sealed record SessionExportPayload(
    Storage.Entities.Session Session,
    List<Storage.Entities.Entry> Entries);

public sealed record CompactResult(
    int TokensBefore,
    int TokensAfter,
    int EntriesRemoved);

public sealed record SessionEntryCountDto(int Count);

/// <summary>
/// Bound session attachment metadata for <c>GET /api/sessions/{id}/attachments</c>
/// (list + Reveal). Does not include pending rows or file bytes.
/// </summary>
public sealed record SessionAttachmentDto(
    Guid Id,
    string LogicalKey,
    string OriginalFileName,
    int Version,
    string RelativePath,
    string MimeType,
    long ByteLength,
    SessionAttachmentKind Kind,
    string ContentSha256,
    DateTimeOffset CreatedAt,
    AttachmentSourceKind SourceKind = AttachmentSourceKind.SnapshotOnly,
    string? SourceWorkspaceIdentity = null,
    string? SourceRelativePath = null,
    bool IsRefreshable = false,
    AttachmentSourceStatus SourceStatus = AttachmentSourceStatus.NotApplicable,
    string? SourceDiagnosticReason = null,
    string? LastObservedSourceContentSha256 = null,
    DateTimeOffset? LastObservedSourceWriteTime = null,
    long? LastObservedSourceByteLength = null,
    SessionAttachmentIndexStatus IndexingStatus = SessionAttachmentIndexStatus.NotEligible);

public sealed record CreateSessionContextPinRequest(
    SessionContextPinKind Kind,
    string TargetIdentifier,
    string? DisplayLabel = null,
    string? ContentVersion = null);

public sealed record SessionContextPinDto(
    Guid Id,
    Guid SessionId,
    SessionContextPinKind Kind,
    string TargetIdentifier,
    string DisplayLabel,
    string? ContentVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
