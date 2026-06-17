using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Storage.Entities;

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
    DateTimeOffset UpdatedAt);

public sealed record SessionDetailDto(
    Guid Id,
    Guid? CampaignId,
    string? Title,
    string Status,
    int EntryCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Summary,
    long TotalTokensUsed);

public sealed record EntryDto(
    Guid Id,
    Guid SessionId,
    string Role,
    string Content,
    string? ToolCallId,
    string? ToolName,
    DateTimeOffset CreatedAt);

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
