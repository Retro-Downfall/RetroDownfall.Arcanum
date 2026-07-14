namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>Shared visibility helpers for Source / Split / Preview chrome.</summary>
public static class MarkdownViewModeHelper
{

    public static bool IsSourceVisible(MarkdownViewMode mode) =>
        mode is MarkdownViewMode.Source or MarkdownViewMode.Split;

    public static bool IsPreviewVisible(MarkdownViewMode mode) =>
        mode is MarkdownViewMode.Preview or MarkdownViewMode.Split;

    public static bool IsSplitterVisible(MarkdownViewMode mode) =>
        mode is MarkdownViewMode.Split;

}
