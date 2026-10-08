using Markdig;

namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>
/// Markdig pipeline for The Illumination. Extensions are limited to APIs that compile against the
/// Markdig package this project references.
/// </summary>
public static class IlluminationMarkdownPipeline
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseFootnotes()
        .UseMathematics()
        .Build();

    public static MarkdownPipeline Shared => Pipeline;

    public static Markdig.Syntax.MarkdownDocument Parse(string markdown) =>
        Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);
}
