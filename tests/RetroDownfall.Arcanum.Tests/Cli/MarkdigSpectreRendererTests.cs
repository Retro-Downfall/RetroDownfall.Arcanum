using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class MarkdigSpectreRendererTests
{

    [Fact]
    public void Render_empty_markdown_returns_empty_text()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        IRenderable renderable = renderer.Render(string.Empty);

        TestConsole console = new();

        console.Write(renderable);

        Assert.Equal(string.Empty, console.Output.Trim());

    }

    [Fact]
    public void Render_heading_uses_bold_markup()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        IRenderable renderable = renderer.Render("# Title");

        TestConsole console = new();

        console.Write(renderable);

        Assert.Contains("Title", console.Output);

    }

    [Fact]
    public void Render_paragraph_preserves_literal_text()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        IRenderable renderable = renderer.Render("Hello **world**");

        TestConsole console = new();

        console.Write(renderable);

        Assert.Contains("Hello", console.Output);

        Assert.Contains("world", console.Output);

    }

    [Fact]
    public void Render_list_includes_bullet_prefix()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        string markdown = "- first\n- second";

        IRenderable renderable = renderer.Render(markdown);

        TestConsole console = new();

        console.Write(renderable);

        Assert.Contains("- first", console.Output);

        Assert.Contains("- second", console.Output);

    }

    [Fact]
    public void Render_fenced_code_block_emits_panel()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        string markdown = "```csharp\nvar x = 1;\n```";

        IRenderable renderable = renderer.Render(markdown);

        TestConsole console = new();

        console.Write(renderable);

        Assert.Contains("var x = 1;", console.Output);

        Assert.Contains("csharp", console.Output);

    }

    [Fact]
    public void Render_multiple_blocks_returns_rows()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        string markdown = "Line one\n\nLine two";

        IRenderable renderable = renderer.Render(markdown);

        Assert.IsType<Rows>(renderable);

    }

    [Fact]
    public void Render_oversized_markdown_preserves_late_content_through_chunked_rendering()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        string markdown = new string('a', (256 * 1024) + 5000)
            + "\n\nLATE-CONTENT-MUST-RENDER";

        IRenderable renderable = renderer.Render(markdown);

        TestConsole console = new();

        console.Write(renderable);

        Assert.Contains("LATE-CONTENT-MUST-RENDER", console.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("output truncated for display", console.Output, StringComparison.Ordinal);

    }

    /// <summary>
    /// An ordered list carries its own numbering, and that numbering is content: a set of steps
    /// rendered as undifferentiated bullets no longer tells the operator what order to do them in.
    /// </summary>
    [Fact]
    public void Render_ordered_list_keeps_its_numbering()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        TestConsole console = new();

        console.Write(renderer.Render("1. unplug it\n2. wait\n3. plug it back in"));

        Assert.Contains("1. unplug it", console.Output, StringComparison.Ordinal);

        Assert.Contains("2. wait", console.Output, StringComparison.Ordinal);

        Assert.Contains("3. plug it back in", console.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("- unplug it", console.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Render_ordered_list_honours_a_start_value_other_than_one()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        TestConsole console = new();

        console.Write(renderer.Render("4. fourth\n5. fifth"));

        Assert.Contains("4. fourth", console.Output, StringComparison.Ordinal);

        Assert.Contains("5. fifth", console.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Render_bulleted_list_still_uses_a_bullet()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        TestConsole console = new();

        console.Write(renderer.Render("- alpha\n- beta"));

        Assert.Contains("- alpha", console.Output, StringComparison.Ordinal);

        Assert.Contains("- beta", console.Output, StringComparison.Ordinal);

    }

    /// <summary>
    /// A terminal cannot be clicked, so a link whose destination is dropped leaves the operator with
    /// link text and nothing to act on.
    /// </summary>
    [Fact]
    public void Render_link_keeps_the_destination_url()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        TestConsole console = new();

        console.Write(renderer.Render("See [the guide](https://example.test/g)."));

        Assert.Contains("the guide", console.Output, StringComparison.Ordinal);

        Assert.Contains("https://example.test/g", console.Output, StringComparison.Ordinal);

    }

    /// <summary>
    /// Markdig models an image as a link, so the same omission drops the image source and leaves only
    /// the alt text.
    /// </summary>
    [Fact]
    public void Render_image_keeps_its_source()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        TestConsole console = new();

        console.Write(renderer.Render("![a chart](https://example.test/c.png)"));

        Assert.Contains("a chart", console.Output, StringComparison.Ordinal);

        Assert.Contains("https://example.test/c.png", console.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Render_link_whose_text_is_its_url_does_not_repeat_it()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        TestConsole console = new();

        console.Write(renderer.Render("[https://example.test/g](https://example.test/g)"));

        Assert.Equal(
            1,
            console.Output.Split("https://example.test/g", StringSplitOptions.None).Length - 1);

    }

    private static MarkdigSpectreRenderer CreateRenderer()
    {

        ThemeSemanticColors semantic = new();

        ThemeSemanticColors fallback = new();

        ConfiguredThemePalette palette = new(semantic, fallback);

        return new MarkdigSpectreRenderer(palette);

    }

    [Fact]
    public void Render_blockquote_shows_the_quoted_text_not_a_type_name()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        TestConsole console = new();

        console.Write(renderer.Render("> Note: back up first"));

        Assert.DoesNotContain("Markdig", console.Output, StringComparison.Ordinal);

        Assert.Contains("Note: back up first", console.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Render_thematic_break_does_not_emit_a_type_name()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        TestConsole console = new();

        console.Write(renderer.Render("before\n\n---\n\nafter"));

        Assert.DoesNotContain("Markdig", console.Output, StringComparison.Ordinal);

        Assert.Contains("before", console.Output, StringComparison.Ordinal);

        Assert.Contains("after", console.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Render_nested_list_shows_the_nested_items()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        TestConsole console = new();

        console.Write(renderer.Render("- outer\n  - nested\n"));

        Assert.DoesNotContain("Markdig", console.Output, StringComparison.Ordinal);

        Assert.Contains("outer", console.Output, StringComparison.Ordinal);

        Assert.Contains("nested", console.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Render_inline_html_does_not_emit_a_type_name()
    {

        MarkdigSpectreRenderer renderer = CreateRenderer();

        TestConsole console = new();

        console.Write(renderer.Render("See <br> here."));

        Assert.DoesNotContain("Markdig", console.Output, StringComparison.Ordinal);

        Assert.Contains("See", console.Output, StringComparison.Ordinal);

        Assert.Contains("here.", console.Output, StringComparison.Ordinal);

    }

}
