using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;

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
            session.ForkedFromSessionId,
            session.TotalCostUsd);

    public static SessionAttachmentDto ToAttachmentDto(
        SessionAttachmentRecord record,
        SessionAttachmentIndexStatus indexingStatus = SessionAttachmentIndexStatus.NotEligible)
    {
        AttachmentSourceMetadata source = record.Source ?? AttachmentSourceMetadata.SnapshotOnly;

        return new(
            record.Id,
            record.LogicalKey,
            record.OriginalFileName,
            record.Version,
            record.RelativePath,
            record.MimeType,
            record.ByteLength,
            record.Kind,
            record.ContentSha256,
            record.CreatedAt,
            source.Kind,
            source.WorkspaceIdentity,
            source.WorkspaceRelativePath,
            source.IsRefreshable,
            source.Status,
            source.DiagnosticReason,
            source.LastObservedContentSha256,
            source.LastObservedWriteTime,
            source.LastObservedByteLength,
            indexingStatus,
            record.SessionId);
    }

}
