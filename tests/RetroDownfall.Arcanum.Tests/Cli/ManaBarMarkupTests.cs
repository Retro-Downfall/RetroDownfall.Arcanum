using Spectre.Console;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ManaBarMarkupTests
{

    [Fact]
    public void Colored_bar_with_literal_brackets_parses()
    {

        // Mirrors ChatCommand.RenderManaBarLine: "[[" / "]]" for literal [ ],
        // and "[/]]]" so the closing bar bracket is escaped after style close.
        const string line = "Mana: [[[green]████[/][grey]░░░░░░░░░░░░░░░░[/]]] 20% (1/5)";

        TestConsole console = new();

        Exception? error = Record.Exception(() => console.MarkupLine(line));

        Assert.Null(error);

        Assert.Contains("Mana:", console.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Unescaped_closing_bracket_throws()
    {

        // Pre-fix shape: single "]" after "[/]" — Spectre rejects this.
        const string line = "Mana: [[[green]████[/][grey]░░░░[/]] 20%";

        TestConsole console = new();

        Assert.Throws<InvalidOperationException>(() => console.MarkupLine(line));

    }

}
