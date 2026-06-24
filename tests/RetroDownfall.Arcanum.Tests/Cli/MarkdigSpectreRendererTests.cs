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

    private static MarkdigSpectreRenderer CreateRenderer()
    {

        ThemeSemanticColors semantic = new();

        ThemeSemanticColors fallback = new();

        ConfiguredThemePalette palette = new(semantic, fallback);

        return new MarkdigSpectreRenderer(palette);

    }

}
