namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>Existing attachment lineage constraints, composed with a separate live Guild scope.</summary>
internal static class SessionAttachmentGuildReadPolicy
{
    // Workspace snapshots require their physical and protected-source adapter. An attachment memory
    // grant alone cannot authenticate a stored path or turn it into source-release authority.
    internal static string Eligible(string attachment) => $"""
        ({attachment}.State='Bound' AND {attachment}.SourceKind='SnapshotOnly'
          AND ({attachment}.EntryId IS NULL OR EXISTS(
            SELECT 1 FROM Entries guild_source_entry
            WHERE guild_source_entry.Id={attachment}.EntryId COLLATE NOCASE
              AND guild_source_entry.SessionId={attachment}.SessionId COLLATE NOCASE))
          AND NOT EXISTS(SELECT 1 FROM artifact_sensitivity guild_source_label
            WHERE guild_source_label.ArtifactKindCode=1
              AND lower(replace(guild_source_label.ArtifactId, '-', ''))=lower(replace({attachment}.EntryId, '-', ''))))
        """;

    internal static string Indexed(string chunk) => $"""
        EXISTS(SELECT 1 FROM SessionAttachments guild_source
          JOIN session_attachment_index_state guild_index ON guild_index.AttachmentId=guild_source.Id
          WHERE guild_source.Id={chunk}.AttachmentId COLLATE NOCASE
            AND guild_source.SessionId={chunk}.SessionId COLLATE NOCASE
            AND {Eligible("guild_source")}
            AND guild_source.ContentSha256={chunk}.ContentSha256
            AND guild_source.Version={chunk}.Version
            AND guild_source.MimeType={chunk}.MimeType
            AND guild_index.Status='Indexed'
            AND guild_index.PublishedGenerationId={chunk}.GenerationId)
        """;
}
