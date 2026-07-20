using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class SessionMapping
{

    public static EntryDto ToEntryDto(Entry entry) =>
        new(
            entry.Id,
            entry.SessionId,
            entry.Role.ToString().ToLowerInvariant(),
            entry.Content,
            entry.ToolCallId,
            entry.ToolName,
            entry.CreatedAt,
            entry.IsPinned);

    public static SessionDetailDto ToDetailDto(Session session, int entryCount) =>
        new(
            session.Id,
            session.CampaignId,
            session.Title,
            session.Status,
            entryCount,
            session.CreatedAt,
            session.UpdatedAt,
            session.Summary,
            session.TotalTokensUsed,
            session.ForkedFromSessionId);

    public static SessionAttachmentDto ToAttachmentDto(SessionAttachmentRecord record) =>
        new(
            record.Id,
            record.LogicalKey,
            record.OriginalFileName,
            record.Version,
            record.RelativePath,
            record.MimeType,
            record.ByteLength,
            record.Kind,
            record.ContentSha256,
            record.CreatedAt);

}
