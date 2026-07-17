using Markdig.Syntax;

namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>
/// Pure (non-Avalonia) Illumination preparation: sanitize, Markdig parse, and source-line anchor
/// precompute. Safe to run off the UI thread.
/// </summary>
public static class IlluminationMarkdownPrepare
{

    public static IlluminationPreparedMarkdown Prepare(string? markdown)
    {

        string sanitized = MarkdownSafetySanitizer.Sanitize(markdown, out bool truncated);

        MarkdownDocument document = IlluminationMarkdownPipeline.Parse(sanitized);

        IReadOnlyList<MarkdownSourceBlockAnchor> anchors = CollectAnchors(document);

        return new IlluminationPreparedMarkdown(sanitized, truncated, document, anchors);

    }

    /// <summary>
    /// Mirrors <c>MarkdigAstAvaloniaRenderer</c>'s top-level block walk: every block except
    /// <see cref="LinkReferenceDefinitionGroup"/> receives a sequential <c>bN</c> id; anchors with
    /// <c>SourceLine &gt;= 0</c> are retained for sync-scroll.
    /// </summary>
    public static IReadOnlyList<MarkdownSourceBlockAnchor> CollectAnchors(MarkdownDocument document)
    {

        List<MarkdownSourceBlockAnchor> anchors = [];

        int blockSequence = 0;

        foreach (Block block in document)
        {

            if (block is LinkReferenceDefinitionGroup)
            {

                continue;

            }

            int sourceLine = block.Line;

            string blockId = $"b{blockSequence++}";

            if (sourceLine >= 0)
            {

                anchors.Add(new MarkdownSourceBlockAnchor(sourceLine, blockId));

            }

        }

        return anchors;

    }

}

/// <summary>Result of off-UI Illumination markdown preparation.</summary>
public sealed record IlluminationPreparedMarkdown(
    string SanitizedMarkdown,
    bool Truncated,
    MarkdownDocument Document,
    IReadOnlyList<MarkdownSourceBlockAnchor> Anchors);
