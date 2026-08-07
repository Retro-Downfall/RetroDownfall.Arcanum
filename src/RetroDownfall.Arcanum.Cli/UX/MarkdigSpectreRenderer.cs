using System.Diagnostics.CodeAnalysis;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RetroDownfall.Arcanum.Cli.UX;

[ExcludeFromCodeCoverage] // Reason: Spectre.Console markdown rendering adapter; covered via MarkdigSpectreRendererTests asserting renderables without console IO.
public sealed class MarkdigSpectreRenderer(IThemePalette palette)
{

    // Physical per-parse allocation boundary. Larger responses are rendered through lazy chunks;
    // the complete answer remains visible without giving Markdig one unbounded input allocation.
    private const int MaxRenderChars = 256 * 1024;

    public IRenderable Render(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return new Text(string.Empty);
        }

        return markdown.Length <= MaxRenderChars
            ? RenderChunk(markdown)
            : new ChunkedMarkdownRenderable(markdown, MaxRenderChars, RenderChunk);

    }

    private IRenderable RenderChunk(string markdown)
    {

        MarkdownDocument doc;

        try
        {
            doc = Markdown.Parse(markdown);
        }
        catch
        {
            return new Markup(Markup.Escape(markdown));
        }

        List<IRenderable> rows = new(doc.Count);

        foreach (Block block in doc)
        {
            rows.Add(RenderBlock(block));
        }

        return rows.Count switch
        {
            0 => new Text(string.Empty),
            1 => rows[0],
            _ => new Rows(rows),
        };
    }

    private sealed class ChunkedMarkdownRenderable(
        string markdown,
        int maxChunkChars,
        Func<string, IRenderable> renderChunk) : IRenderable
    {

        public Measurement Measure(RenderOptions options, int maxWidth)
        {

            int min = 0;

            int max = 0;

            foreach (IRenderable chunk in EnumerateRenderableChunks())
            {

                Measurement measurement = chunk.Measure(options, maxWidth);

                min = Math.Max(min, measurement.Min);

                max = Math.Max(max, measurement.Max);

            }

            return new Measurement(min, max);

        }

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {

            foreach (IRenderable chunk in EnumerateRenderableChunks())
            {

                foreach (Segment segment in chunk.Render(options, maxWidth))
                {

                    yield return segment;

                }

            }

        }

        private IEnumerable<IRenderable> EnumerateRenderableChunks()
        {

            int offset = 0;

            while (offset < markdown.Length)
            {

                int end = markdown.Length - offset <= maxChunkChars
                    ? markdown.Length
                    : offset + maxChunkChars;

                if (end < markdown.Length)
                {

                    int newline = markdown.LastIndexOf('\n', end - 1, end - offset);

                    if (newline >= offset)
                    {

                        end = newline + 1;

                    }
                    else if (end > offset && char.IsHighSurrogate(markdown[end - 1]))
                    {

                        end--;

                    }

                }

                yield return renderChunk(markdown[offset..end]);

                offset = end;

            }

        }

    }

    private IRenderable RenderBlock(Block block)
    {
        try
        {
            return block switch
            {
                HeadingBlock h => RenderHeadingBlock(h),
                FencedCodeBlock f => RenderFencedCodeBlock(f),
                CodeBlock c => RenderCodeBlock(c),
                ParagraphBlock p => new Markup(InlineToMarkup(p.Inline)),
                ListBlock l => RenderListBlock(l),
                QuoteBlock q => RenderQuoteBlock(q),
                ThematicBreakBlock => new Rule { Style = new Style(foreground: palette.Muted) },
                _ => new Markup(Markup.Escape(BlockToFallbackText(block))),
            };
        }
        catch
        {
            return new Markup(Markup.Escape(BlockToFallbackText(block)));
        }
    }

    private IRenderable RenderHeadingBlock(HeadingBlock heading)
    {
        string text = InlineToPlain(heading.Inline);

        if (string.IsNullOrEmpty(text))
        {
            return new Text(string.Empty);
        }

        return new Markup(palette.HeadingBoldMarkup(Markup.Escape(text)));
    }

    private IRenderable RenderFencedCodeBlock(FencedCodeBlock fence)
    {
        string code = fence.Lines.ToString();

        Panel panel = new(new Text(code))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: palette.Muted),
        };

        string? info = fence.Info;

        if (!string.IsNullOrWhiteSpace(info))
        {
            panel.Header = new PanelHeader(palette.HighlightMarkup(Markup.Escape(info)));
        }

        return panel;
    }

    private IRenderable RenderCodeBlock(CodeBlock code)
    {
        string content = code.Lines.ToString();

        return new Panel(new Text(content))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: palette.Muted),
        };
    }

    private IRenderable RenderQuoteBlock(QuoteBlock quote)
    {
        StringBuilder body = new();

        foreach (Block child in quote)
        {
            string markup = child is ParagraphBlock p
                ? InlineToMarkup(p.Inline)
                : Markup.Escape(BlockToFallbackText(child));

            if (markup.Length == 0)
            {
                continue;
            }

            if (body.Length > 0)
            {
                body.Append('\n');
            }

            body.Append(markup);
        }

        if (body.Length == 0)
        {
            return new Text(string.Empty);
        }

        string bar = palette.MutedMarkup("▌");

        StringBuilder quoted = new();

        foreach (string line in body.ToString().Split('\n'))
        {
            if (quoted.Length > 0)
            {
                quoted.Append('\n');
            }

            quoted.Append(bar).Append(' ').Append(line);
        }

        return new Markup(quoted.ToString());
    }

    private IRenderable RenderListBlock(ListBlock list) =>
        new Markup(ListBlockToMarkup(list, depth: 0));

    private string ListBlockToMarkup(ListBlock list, int depth)
    {
        StringBuilder sb = new();

        string indent = new(' ', (depth + 1) * 2);

        bool first = true;

        foreach (Block child in list)
        {
            if (child is not ListItemBlock item)
            {
                continue;
            }

            if (!first)
            {
                sb.Append('\n');
            }

            first = false;

            sb.Append(indent);

            sb.Append("- ");

            sb.Append(ListItemToMarkup(item, depth));
        }

        return sb.ToString();
    }

    private string ListItemToMarkup(ListItemBlock item, int depth)
    {
        StringBuilder sb = new();

        bool first = true;

        foreach (Block child in item)
        {
            if (child is ListBlock nested)
            {
                sb.Append('\n');

                sb.Append(ListBlockToMarkup(nested, depth + 1));

                first = false;

                continue;
            }

            if (!first)
            {
                sb.Append(' ');
            }

            first = false;

            if (child is ParagraphBlock p)
            {
                sb.Append(InlineToMarkup(p.Inline));
            }
            else
            {
                sb.Append(Markup.Escape(BlockToFallbackText(child)));
            }
        }

        return sb.ToString();
    }

    private static string InlineToPlain(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        StringBuilder sb = new();

        AppendInlinesPlain(container, sb);

        return sb.ToString();
    }

    private static void AppendInlinesPlain(ContainerInline container, StringBuilder sb)
    {
        foreach (Inline inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sb.Append(lit.Content.ToString());
                    break;

                case CodeInline code:
                    sb.Append(code.Content);
                    break;

                case LineBreakInline:
                    sb.Append(' ');
                    break;

                case HtmlInline html:
                    sb.Append(html.Tag);
                    break;

                case HtmlEntityInline entity:
                    sb.Append(entity.Transcoded.ToString());
                    break;

                case AutolinkInline autolink:
                    sb.Append(autolink.Url);
                    break;

                case ContainerInline child:
                    AppendInlinesPlain(child, sb);
                    break;

                default:
                    // Markdig does not override ToString on inline nodes; emitting nothing is the
                    // only safe fallback, because ToString would print the CLR type name.
                    break;
            }
        }
    }

    private string InlineToMarkup(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        StringBuilder sb = new();

        AppendInlinesMarkup(container, sb);

        return sb.ToString();
    }

    private void AppendInlinesMarkup(ContainerInline container, StringBuilder sb)
    {
        foreach (Inline inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sb.Append(Markup.Escape(lit.Content.ToString()));
                    break;

                case EmphasisInline em:
                    string tag = em.DelimiterCount >= 2 ? "bold" : "italic";

                    sb.Append('[').Append(tag).Append(']');

                    AppendInlinesMarkup(em, sb);

                    sb.Append("[/]");

                    break;

                case CodeInline code:
                    sb.Append(Markup.Escape($"`{code.Content}`"));
                    break;

                case LineBreakInline:
                    sb.Append('\n');
                    break;

                case HtmlInline html:
                    sb.Append(Markup.Escape(html.Tag));
                    break;

                case HtmlEntityInline entity:
                    sb.Append(Markup.Escape(entity.Transcoded.ToString()));
                    break;

                case AutolinkInline autolink:
                    sb.Append(Markup.Escape(autolink.Url));
                    break;

                case ContainerInline child:
                    AppendInlinesMarkup(child, sb);
                    break;

                default:
                    // Markdig does not override ToString on inline nodes; emitting nothing is the
                    // only safe fallback, because ToString would print the CLR type name.
                    break;
            }
        }
    }

    /// <summary>
    /// Plain text for a block the renderer has no dedicated arm for. Markdig does not override
    /// <see cref="object.ToString"/> on its syntax nodes, so falling through to it would print CLR
    /// type names ("Markdig.Syntax.QuoteBlock") into the operator's answer.
    /// </summary>
    private static string BlockToFallbackText(Block block)
    {
        if (block is LeafBlock leaf)
        {
            return leaf.Lines.Count > 0
                ? leaf.Lines.ToString()
                : InlineToPlain(leaf.Inline);
        }

        if (block is ContainerBlock container)
        {
            StringBuilder sb = new();

            foreach (Block child in container)
            {
                string text = BlockToFallbackText(child);

                if (text.Length == 0)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(text);
            }

            return sb.ToString();
        }

        return string.Empty;
    }

}
