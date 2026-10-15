using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Pins who may build a Spectre console in the CLI. A console built with <c>AnsiConsole.Create</c>
/// beside <c>CliConsoleFactory</c> is one that a CI runner's provider detector can switch ANSI back
/// on for, after <c>--plain</c>, <c>--json</c>, <c>NO_COLOR</c> or a redirected stdout declined it.
/// </summary>
/// <remarks>
/// An inventory over source rather than a behaviour test because the failure it prevents is a new call
/// site, not a wrong result: the call site that bypasses the factory renders correctly on every
/// machine except the runner it matters on.
/// </remarks>
public sealed class CliConsoleConstructionInventoryTests
{

    private const string CliSourceRoot = "src/RetroDownfall.Arcanum.Cli/";

    private const string FactoryPath = "src/RetroDownfall.Arcanum.Cli/UX/CliConsoleFactory.cs";

    /// <summary>
    /// A call spelled across lines, with the member access on its own line, is still a call.
    /// </summary>
    private static readonly Regex ConsoleConstruction = new(
        @"\bAnsiConsole(?:Factory)?\s*\.\s*Create\s*\(",
        RegexOptions.CultureInvariant);

    [Fact]
    public void The_console_factory_is_the_only_cli_source_that_builds_a_spectre_console()
    {

        string[] builders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    source.RelativePath.StartsWith(CliSourceRoot, StringComparison.Ordinal)
                    && ConsoleConstruction.IsMatch(source.Text))
                .Select(static source => source.RelativePath)
                .OrderBy(static path => path, StringComparer.Ordinal),
        ];

        Assert.Equal([FactoryPath], builders);

    }

    [Theory]
    [InlineData("AnsiConsole.Create(settings)")]
    [InlineData("AnsiConsole\n    .Create(settings)")]
    [InlineData("AnsiConsole . Create (settings)")]
    [InlineData("AnsiConsoleFactory.Create(settings)")]
    public void The_inventory_recognises_every_spelling_of_the_call(string source)
    {

        Assert.Matches(ConsoleConstruction, source);

    }

    [Theory]
    [InlineData("CliConsoleFactory.Create(settings)")]
    [InlineData("AnsiConsole.Console = console;")]
    [InlineData("new AnsiConsoleSettings()")]
    public void The_inventory_leaves_the_factory_and_the_global_console_alone(string source)
    {

        Assert.DoesNotMatch(ConsoleConstruction, source);

    }

}
