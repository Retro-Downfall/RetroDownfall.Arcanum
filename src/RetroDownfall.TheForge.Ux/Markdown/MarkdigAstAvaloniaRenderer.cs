using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>
/// Renders a Markdig 1.2.0 AST into native Avalonia controls for The Illumination.
/// </summary>
public sealed class MarkdigAstAvaloniaRenderer
{

    public static readonly AttachedProperty<int> SourceLineProperty =
        AvaloniaProperty.RegisterAttached<MarkdigAstAvaloniaRenderer, Control, int>("SourceLine", -1);

    public static readonly AttachedProperty<string?> BlockIdProperty =
        AvaloniaProperty.RegisterAttached<MarkdigAstAvaloniaRenderer, Control, string?>("BlockId");

    private readonly IMarkdownCodeHighlighter _highlighter;

    private readonly IMarkdownImageResolver _images;

    private readonly IlluminationHyperlinkCommand _hyperlinkCommand;

    private readonly List<MarkdownSourceBlockAnchor> _anchors = [];

    private int _blockSequence;

    private IlluminationImageContext _imageContext = new();

    private CancellationToken _cancellationToken;

    public MarkdigAstAvaloniaRenderer(
        IMarkdownCodeHighlighter? highlighter = null,
        IMarkdownImageResolver? images = null,
        IlluminationHyperlinkCommand? hyperlinkCommand = null)
    {

        _highlighter = highlighter ?? new ColorCodeMarkdownCodeHighlighter();

        _images = images ?? new MarkdownImageResolver(new RemoteMarkdownImageLoader());

        _hyperlinkCommand = hyperlinkCommand ?? new IlluminationHyperlinkCommand();

    }

    public IReadOnlyList<MarkdownSourceBlockAnchor> Anchors => _anchors;

    public event EventHandler<int>? GoToSourceRequested;

    public Control Render(
        string markdown,
        IlluminationImageContext imageContext,
        CancellationToken cancellationToken) =>
        Render(IlluminationMarkdownPipeline.Parse(markdown), imageContext, cancellationToken);

    /// <summary>
    /// Renders a pre-parsed Markdig document. Prefer this when sanitize/parse ran off the UI thread.
    /// </summary>
    public Control Render(
        MarkdownDocument document,
        IlluminationImageContext imageContext,
        CancellationToken cancellationToken)
    {

        _anchors.Clear();

        _blockSequence = 0;

        _imageContext = imageContext;

        _cancellationToken = cancellationToken;

        StackPanel root = new()
        {

            Orientation = Orientation.Vertical,

            Spacing = 8,

        };

        foreach (Block block in document)
        {

            Control? control = RenderBlock(block);

            if (control is not null)
            {

                root.Children.Add(control);

            }

        }

        return root;

    }

    private Control? RenderBlock(Block block)
    {

        return block switch
        {
            HeadingBlock heading => AttachAnchor(CreateHeading(heading), heading.Line),
            ParagraphBlock paragraph => AttachAnchor(CreateParagraph(paragraph), paragraph.Line),
            QuoteBlock quote => AttachAnchor(CreateQuote(quote), quote.Line),
            ListBlock list => AttachAnchor(CreateList(list), list.Line),
            MathBlock math => AttachAnchor(
                CreateLabeledSourceBlock("Math", GetLeafText(math), "math"),
                math.Line),
            FencedCodeBlock fenced when IsMermaid(fenced) =>
                AttachAnchor(CreateLabeledSourceBlock("Mermaid diagram source", GetLeafText(fenced), "mermaid"), fenced.Line),
            FencedCodeBlock fenced => AttachAnchor(CreateCodeBlock(fenced.Info, GetLeafText(fenced)), fenced.Line),
            CodeBlock code => AttachAnchor(CreateCodeBlock(null, GetLeafText(code)), code.Line),
            ThematicBreakBlock hr => AttachAnchor(CreateRule(), hr.Line),
            Table table => AttachAnchor(CreateTable(table), table.Line),
            HtmlBlock => AttachAnchor(CreateMutedPlaceholder("[HTML omitted]"), block.Line),
            FootnoteGroup group => AttachAnchor(CreateFootnoteGroup(group), group.Line),
            Footnote footnote => AttachAnchor(CreateFootnote(footnote), footnote.Line),
            LinkReferenceDefinitionGroup => null,
            _ => AttachAnchor(CreateMutedPlaceholder($"[{block.GetType().Name}]"), block.Line),
        };

    }

    private Control AttachAnchor(Control control, int sourceLine)
    {

        string blockId = $"b{_blockSequence++}";

        control.SetValue(SourceLineProperty, sourceLine);

        control.SetValue(BlockIdProperty, blockId);

        if (sourceLine >= 0)
        {

            _anchors.Add(new MarkdownSourceBlockAnchor(sourceLine, blockId));

        }

        control.PointerPressed += (_, e) =>
        {

            if (e.GetCurrentPoint(control).Properties.IsLeftButtonPressed && sourceLine >= 0)
            {

                GoToSourceRequested?.Invoke(this, sourceLine);

            }

        };

        return control;

    }

    private Control CreateHeading(HeadingBlock heading)
    {

        double size = heading.Level switch
        {
            1 => 22,
            2 => 18,
            3 => 16,
            4 => 14,
            _ => 13,
        };

        TextBlock text = new()
        {

            TextWrapping = TextWrapping.Wrap,

            FontSize = size,

            FontWeight = FontWeight.SemiBold,

            Foreground = ThemeBrush("ForgeTextBrush"),

        };

        text.Inlines?.Clear();

        AppendInlines(text.Inlines!, heading.Inline);

        return text;

    }

    private Control CreateParagraph(ParagraphBlock paragraph)
    {

        TextBlock text = new()
        {

            TextWrapping = TextWrapping.Wrap,

            Foreground = ThemeBrush("ForgeTextBrush"),

        };

        text.Inlines?.Clear();

        AppendInlines(text.Inlines!, paragraph.Inline);

        return text;

    }

    private Control CreateQuote(QuoteBlock quote)
    {

        StackPanel inner = new() { Spacing = 6 };

        foreach (Block child in quote)
        {

            Control? rendered = RenderBlock(child);

            if (rendered is not null)
            {

                inner.Children.Add(rendered);

            }

        }

        return new Border
        {

            BorderThickness = new Thickness(3, 0, 0, 0),

            BorderBrush = ThemeBrush("ForgeAccentBrush"),

            Padding = new Thickness(10, 4, 4, 4),

            Child = inner,

            Background = ThemeBrush("ForgeSurfaceAltBrush"),

        };

    }

    private Control CreateList(ListBlock list)
    {

        StackPanel panel = new() { Spacing = 4 };

        int index = 1;

        if (list.IsOrdered && int.TryParse(list.OrderedStart, out int start))
        {

            index = start;

        }

        foreach (Block item in list)
        {

            if (item is not ListItemBlock listItem)
            {

                continue;

            }

            DockPanel row = new();

            TextBlock bullet = new()
            {

                Text = list.IsOrdered ? $"{index++}." : "•",

                Width = 24,

                Margin = new Thickness(0, 0, 6, 0),

                Foreground = ThemeBrush("ForgeMutedTextBrush"),

            };

            DockPanel.SetDock(bullet, Dock.Left);

            StackPanel content = new() { Spacing = 4 };

            foreach (Block child in listItem)
            {

                Control? rendered = RenderBlock(child);

                if (rendered is not null)
                {

                    content.Children.Add(rendered);

                }

            }

            row.Children.Add(bullet);

            row.Children.Add(content);

            panel.Children.Add(row);

        }

        return panel;

    }

    private Control CreateTable(Table table)
    {

        Grid grid = new()
        {

            ColumnDefinitions = BuildEqualColumns(CountColumns(table)),

        };

        int rowIndex = 0;

        foreach (Block rowBlock in table)
        {

            if (rowBlock is not TableRow row)
            {

                continue;

            }

            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            int colIndex = 0;

            foreach (Block cellBlock in row)
            {

                if (cellBlock is not TableCell cell)
                {

                    continue;

                }

                if (colIndex >= grid.ColumnDefinitions.Count)
                {

                    break;

                }

                StackPanel cellPanel = new() { Spacing = 2 };

                foreach (Block child in cell)
                {

                    Control? rendered = RenderBlock(child);

                    if (rendered is not null)
                    {

                        cellPanel.Children.Add(rendered);

                    }

                }

                Border border = new()
                {

                    BorderThickness = new Thickness(1),

                    BorderBrush = ThemeBrush("ForgeBorderBrush"),

                    Padding = new Thickness(6, 4),

                    Child = cellPanel,

                    Background = row.IsHeader
                        ? ThemeBrush("ForgeSurfaceAltBrush")
                        : ThemeBrush("ForgeSurfaceBrush"),

                };

                Grid.SetRow(border, rowIndex);

                Grid.SetColumn(border, colIndex);

                grid.Children.Add(border);

                colIndex++;

            }

            rowIndex++;

        }

        return grid;

    }

    private Control CreateCodeBlock(string? languageInfo, string code)
    {

        SelectableTextBlock text = new()
        {

            TextWrapping = TextWrapping.Wrap,

            FontFamily = ThemeFont("ForgeCodeFontFamily") ?? FontFamily.Default,

            FontSize = 12,

            Foreground = ThemeBrush("ForgeTextBrush"),

        };

        text.Inlines?.Clear();

        foreach (HighlightedSpan span in _highlighter.Highlight(code, languageInfo))
        {

            Run run = new(span.Text);

            if (!string.IsNullOrEmpty(span.ResourceBrushKey))
            {

                run.Foreground = ThemeBrush(span.ResourceBrushKey);

            }

            text.Inlines!.Add(run);

        }

        return new Border
        {

            Background = ThemeBrush("ForgeSurfaceAltBrush"),

            BorderBrush = ThemeBrush("ForgeBorderBrush"),

            BorderThickness = new Thickness(1),

            CornerRadius = new CornerRadius(4),

            Padding = new Thickness(8),

            Child = text,

        };

    }

    private Control CreateLabeledSourceBlock(string label, string source, string? languageHint)
    {

        StackPanel panel = new() { Spacing = 4 };

        panel.Children.Add(new TextBlock
        {

            Text = label,

            Opacity = 0.72,

            FontWeight = FontWeight.SemiBold,

            Foreground = ThemeBrush("ForgeMutedTextBrush"),

        });

        panel.Children.Add(CreateCodeBlock(languageHint, source));

        return panel;

    }

    private Control CreateFootnoteGroup(FootnoteGroup group)
    {

        StackPanel panel = new() { Spacing = 6 };

        panel.Children.Add(new TextBlock
        {

            Text = "Footnotes",

            FontWeight = FontWeight.SemiBold,

            Foreground = ThemeBrush("ForgeMutedTextBrush"),

        });

        foreach (Block child in group)
        {

            Control? rendered = RenderBlock(child);

            if (rendered is not null)
            {

                panel.Children.Add(rendered);

            }

        }

        return panel;

    }

    private Control CreateFootnote(Footnote footnote)
    {

        StackPanel panel = new() { Spacing = 4 };

        panel.Children.Add(new TextBlock
        {

            Text = $"[{footnote.Label}]",

            FontWeight = FontWeight.SemiBold,

            Foreground = ThemeBrush("ForgeMutedTextBrush"),

        });

        foreach (Block child in footnote)
        {

            Control? rendered = RenderBlock(child);

            if (rendered is not null)
            {

                panel.Children.Add(rendered);

            }

        }

        return panel;

    }

    private static Control CreateRule() =>
        new Border
        {

            Height = 1,

            Margin = new Thickness(0, 8),

            Background = ThemeBrush("ForgeBorderBrush"),

        };

    private static Control CreateMutedPlaceholder(string text) =>
        new TextBlock
        {

            Text = text,

            Opacity = 0.65,

            FontStyle = FontStyle.Italic,

            Foreground = ThemeBrush("ForgeMutedTextBrush"),

            TextWrapping = TextWrapping.Wrap,

        };

    private void AppendInlines(InlineCollection target, ContainerInline? container)
    {

        if (container is null)
        {

            return;

        }

        for (MdInline? inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {

            AppendInline(target, inline);

        }

    }

    private void AppendInline(InlineCollection target, MdInline inline)
    {

        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;

            case CodeInline code:
                target.Add(new Run(code.Content)
                {
                    FontFamily = ThemeFont("ForgeCodeFontFamily") ?? FontFamily.Default,
                    Background = ThemeBrush("ForgeSurfaceAltBrush"),
                });
                break;

            case EmphasisInline emphasis:
                AppendEmphasis(target, emphasis);
                break;

            case LineBreakInline:
                target.Add(new LineBreak());
                break;

            case LinkInline { IsImage: true } image:
                target.Add(CreateInlineUi(CreateImageControl(image)));
                break;

            case LinkInline link:
                AppendLink(target, link.Url, link);
                break;

            case AutolinkInline autolink:
                AppendAutolink(target, autolink);
                break;

            case HtmlInline:
                target.Add(new Run("[HTML omitted]")
                {
                    FontStyle = FontStyle.Italic,
                    Foreground = ThemeBrush("ForgeMutedTextBrush"),
                });
                break;

            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;

            case MathInline math:
                target.Add(new Run($"⟦math: {math.Content}⟧")
                {
                    FontFamily = ThemeFont("ForgeCodeFontFamily") ?? FontFamily.Default,
                    Foreground = ThemeBrush("ForgeMutedTextBrush"),
                });
                break;

            case TaskList task:
                target.Add(new Run(task.Checked ? "☑ " : "☐ "));
                break;

            case FootnoteLink footnoteLink:
                target.Add(new Run($"[{footnoteLink.Index}]")
                {
                    Foreground = ThemeBrush("ForgeAccentBrush"),
                });
                break;

            case ContainerInline container:
                AppendInlines(target, container);
                break;

            default:
                if (inline is ContainerInline unknownContainer)
                {

                    AppendInlines(target, unknownContainer);

                }
                break;
        }

    }

    private void AppendEmphasis(InlineCollection target, EmphasisInline emphasis)
    {

        Span span = new();

        if (emphasis.DelimiterChar is '~')
        {

            span.TextDecorations = TextDecorations.Strikethrough;

        }
        else if (emphasis.DelimiterCount >= 2)
        {

            span.FontWeight = FontWeight.Bold;

        }
        else
        {

            span.FontStyle = FontStyle.Italic;

        }

        InlineCollection nested = span.Inlines ?? [];

        AppendInlines(nested, emphasis);

        target.Add(span);

    }

    private void AppendAutolink(InlineCollection target, AutolinkInline autolink)
    {

        string url = autolink.IsEmail ? $"mailto:{autolink.Url}" : autolink.Url;

        if (!MarkdownLinkPolicy.ShouldOpen(url))
        {

            target.Add(new Run(autolink.Url ?? string.Empty));

            return;

        }

        Button button = new()
        {

            Content = autolink.Url,

            Padding = new Thickness(0),

            Background = Brushes.Transparent,

            BorderThickness = new Thickness(0),

            Cursor = new Cursor(StandardCursorType.Hand),

            Foreground = ThemeBrush("ForgeAccentBrush"),

            Command = _hyperlinkCommand,

            CommandParameter = url,

        };

        target.Add(new InlineUIContainer(button));

    }

    private void AppendLink(InlineCollection target, string? url, ContainerInline content)
    {

        if (!MarkdownLinkPolicy.ShouldOpen(url))
        {

            AppendInlines(target, content);

            return;

        }

        string label = CollectText(content);

        if (string.IsNullOrEmpty(label))
        {

            label = url ?? "link";

        }

        Button button = new()
        {

            Content = label,

            Padding = new Thickness(0),

            Background = Brushes.Transparent,

            BorderThickness = new Thickness(0),

            Cursor = new Cursor(StandardCursorType.Hand),

            Foreground = ThemeBrush("ForgeAccentBrush"),

            Command = _hyperlinkCommand,

            CommandParameter = url,

        };

        target.Add(new InlineUIContainer(button));

    }

    private Control CreateImageControl(LinkInline image)
    {

        string alt = CollectText(image);

        string url = image.Url ?? string.Empty;

        MarkdownImageReference reference = _images.Classify(url) with { AltText = alt };

        TextBlock placeholder = new()
        {

            Text = MarkdownImagePolicy.FormatPlaceholder(alt, url),

            Opacity = 0.65,

            FontStyle = FontStyle.Italic,

            Foreground = ThemeBrush("ForgeMutedTextBrush"),

            TextWrapping = TextWrapping.Wrap,

        };

        Border host = new()
        {

            Child = placeholder,

            Margin = new Thickness(0, 4),

        };

        _ = LoadImageIntoHostAsync(host, reference, placeholder);

        return host;

    }

    private async Task LoadImageIntoHostAsync(
        Border host,
        MarkdownImageReference reference,
        TextBlock placeholder)
    {

        try
        {

            MarkdownImageResolveResult result = await _images
                .ResolveAsync(reference, _imageContext, _cancellationToken)
                .ConfigureAwait(true);

            if (result.Status != MarkdownImageResolveStatus.Success || result.Bytes is null)
            {

                string reason = string.IsNullOrWhiteSpace(result.PlaceholderReason)
                    ? MarkdownImagePolicy.FormatPlaceholder(reference.AltText, reference.RawUrl)
                    : $"[Image: {reference.AltText ?? "image"} — {result.PlaceholderReason}]";

                await Dispatcher.UIThread.InvokeAsync(() => placeholder.Text = reason);

                return;

            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {

                try
                {

                    using MemoryStream stream = new(result.Bytes);

                    Bitmap bitmap = new(stream);

                    host.Child = new Image
                    {

                        Source = bitmap,

                        MaxHeight = 480,

                        Stretch = Stretch.Uniform,

                        HorizontalAlignment = HorizontalAlignment.Left,

                    };

                }
                catch
                {

                    placeholder.Text = MarkdownImagePolicy.FormatPlaceholder(
                        reference.AltText,
                        reference.RawUrl);

                    host.Child = placeholder;

                }

            });

        }
        catch (OperationCanceledException)
        {

            // Preview disposed or superseded.
        }
        catch
        {

            await Dispatcher.UIThread.InvokeAsync(() =>
            {

                placeholder.Text = MarkdownImagePolicy.FormatPlaceholder(
                    reference.AltText,
                    reference.RawUrl);

                host.Child = placeholder;

            });

        }

    }

    private static InlineUIContainer CreateInlineUi(Control control) => new(control);

    private static string CollectText(ContainerInline container)
    {

        System.Text.StringBuilder builder = new();

        for (MdInline? inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {

            if (inline is LiteralInline literal)
            {

                builder.Append(literal.Content);

            }
            else if (inline is ContainerInline nested)
            {

                builder.Append(CollectText(nested));

            }

        }

        return builder.ToString();

    }

    private static string GetLeafText(LeafBlock block) => block.Lines.ToString() ?? string.Empty;

    private static bool IsMermaid(FencedCodeBlock block) =>
        string.Equals(block.Info?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);

    private static int CountColumns(Table table)
    {

        int max = table.ColumnDefinitions?.Count ?? 0;

        foreach (Block rowBlock in table)
        {

            if (rowBlock is TableRow row)
            {

                max = Math.Max(max, row.Count);

            }

        }

        return Math.Max(max, 1);

    }

    private static ColumnDefinitions BuildEqualColumns(int count)
    {

        ColumnDefinitions definitions = new();

        for (int i = 0; i < count; i++)
        {

            definitions.Add(new ColumnDefinition(GridLength.Star));

        }

        return definitions;

    }

    private static IBrush? ThemeBrush(string key)
    {

        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out object? value) == true
            && value is IBrush brush)
        {

            return brush;

        }

        return null;

    }

    private static FontFamily? ThemeFont(string key)
    {

        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out object? value) == true
            && value is FontFamily font)
        {

            return font;

        }

        return null;

    }

}
