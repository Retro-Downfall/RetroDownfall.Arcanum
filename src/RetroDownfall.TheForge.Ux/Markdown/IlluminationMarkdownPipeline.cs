using Markdig;

namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>
/// Markdig 1.2.0 pipeline for The Illumination. Extensions are limited to APIs that compile
/// against the CLI-pinned Markdig package.
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
