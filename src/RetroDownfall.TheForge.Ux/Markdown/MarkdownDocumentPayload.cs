namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>Transient payload for opening a standalone The Illumination markdown Workbench tab.</summary>
public sealed record MarkdownDocumentPayload(
    string Id,
    string Title,
    string Content,
    string? WorkspaceId = null,
    string? RelativePath = null,
    string? BaseRelativeDirectory = null);
