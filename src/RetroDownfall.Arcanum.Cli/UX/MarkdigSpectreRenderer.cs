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

    // W4.1: cap the markdown size before parsing/rendering so a pathologically large assistant
    // response cannot drive an unbounded Markdig parse + Spectre render allocation.
    private const int MaxRenderChars = 256 * 1024;

    public IRenderable Render(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return new Text(string.Empty);
        }

        if (markdown.Length > MaxRenderChars)
        {
            markdown = markdown[..MaxRenderChars] + "\n\n_[output truncated for display]_";
        }

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

    private IRenderable RenderListBlock(ListBlock list)
    {
        StringBuilder sb = new();

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

            sb.Append("  - ");

            sb.Append(ListItemToMarkup(item));
        }

        return new Markup(sb.ToString());
    }

    private string ListItemToMarkup(ListItemBlock item)
    {
        StringBuilder sb = new();

        bool first = true;

        foreach (Block child in item)
        {
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

                case ContainerInline child:
                    AppendInlinesPlain(child, sb);
                    break;

                default:
                    string raw = inline.ToString() ?? string.Empty;

                    if (raw.Length > 0)
                    {
                        sb.Append(raw);
                    }

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

                case ContainerInline child:
                    AppendInlinesMarkup(child, sb);
                    break;

                default:
                    string raw = inline.ToString() ?? string.Empty;

                    sb.Append(Markup.Escape(raw));

                    break;
            }
        }
    }

    private static string BlockToFallbackText(Block block)
    {
        if (block is LeafBlock leaf && leaf.Lines.Count > 0)
        {
            return leaf.Lines.ToString();
        }

        return block.ToString() ?? string.Empty;
    }

}
