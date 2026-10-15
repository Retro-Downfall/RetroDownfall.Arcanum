using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;
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
///
/// <para>The construction pattern needs no opening parenthesis after <c>Create</c>, so a method group
/// (<c>Func&lt;AnsiConsoleSettings, IAnsiConsole&gt; make = AnsiConsole.Create;</c>) is a build site
/// like a call. A <c>using static</c> or a <c>using</c> alias of <c>AnsiConsole</c> would let a source
/// build a console under a name that pattern does not read, so the second pattern refuses both. The
/// default enrichers are applied by <c>AnsiConsole.Create(AnsiConsoleSettings)</c> alone: Spectre's
/// <c>AnsiConsoleFactory</c> is internal in the pinned 0.57.2, so it cannot be named from the CLI and
/// the patterns do not read it. The scan covers <c>.cs</c> sources, plus the CLI project file for a
/// project-level <c>Using</c> item that would import <c>AnsiConsole</c> statically.</para>
/// </remarks>
public sealed class CliConsoleConstructionInventoryTests
{

    private const string CliSourceRoot = "src/RetroDownfall.Arcanum.Cli/";

    private const string FactoryPath = "src/RetroDownfall.Arcanum.Cli/UX/CliConsoleFactory.cs";

    private const string CliProjectPath = "src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj";

    /// <summary>
    /// Any mention of the factory method on <c>AnsiConsole</c>, called or not: a call spelled across
    /// lines, with the member access on its own line, is still a call, and a method group is a call
    /// that has not happened yet.
    /// </summary>
    private static readonly Regex ConsoleConstruction = new(
        @"\bAnsiConsole\s*\.\s*Create\b",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// A <c>using</c> alias or <c>using static</c> of <c>AnsiConsole</c>, which would let a source name
    /// the factory method as a bare <c>Create(settings)</c> or under an alias that no scan above reads.
    /// </summary>
    private static readonly Regex ConsoleAlias = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+|\w+\s*=\s*)[\w.:]*\bAnsiConsole\s*;",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// A project-level <c>&lt;Using Include="…AnsiConsole" /&gt;</c> item, the same import as a
    /// <c>global using</c> declared outside any <c>.cs</c> file.
    /// </summary>
    private static readonly Regex ProjectConsoleUsing = new(
        @"<Using\b[^>]*\bInclude\s*=\s*""[^""]*\bAnsiConsole""",
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

    [Fact]
    public void No_cli_source_aliases_or_statically_imports_AnsiConsole()
    {

        string[] aliasing =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    source.RelativePath.StartsWith(CliSourceRoot, StringComparison.Ordinal)
                    && ConsoleAlias.IsMatch(source.Text))
                .Select(static source => source.RelativePath)
                .OrderBy(static path => path, StringComparer.Ordinal),
        ];

        Assert.Empty(aliasing);

    }

    [Fact]
    public void The_cli_project_does_not_import_AnsiConsole_for_every_source()
    {

        string project = File.ReadAllText(
            Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), CliProjectPath));

        Assert.DoesNotMatch(ProjectConsoleUsing, project);

    }

    /// <summary>
    /// Spectre's <c>AnsiConsoleFactory.Create</c> is deliberately absent: the type is internal in
    /// 0.57.2, so no CLI source can name it.
    /// </summary>
    [Theory]
    [InlineData("AnsiConsole.Create(settings)")]
    [InlineData("AnsiConsole\n    .Create(settings)")]
    [InlineData("AnsiConsole . Create (settings)")]
    [InlineData("Func<AnsiConsoleSettings, IAnsiConsole> make = AnsiConsole.Create;")]
    public void The_inventory_recognises_the_known_spellings_of_the_construction(string source)
    {

        Assert.Matches(ConsoleConstruction, source);

    }

    [Theory]
    [InlineData("CliConsoleFactory.Create(settings)")]
    [InlineData("AnsiConsole.Console = console;")]
    [InlineData("new AnsiConsoleSettings()")]
    [InlineData("AnsiConsole.CreateSettings(settings)")]
    public void The_inventory_leaves_the_factory_and_the_global_console_alone(string source)
    {

        Assert.DoesNotMatch(ConsoleConstruction, source);

    }

    [Theory]
    [InlineData("using static Spectre.Console.AnsiConsole;")]
    [InlineData("global using static global::Spectre.Console.AnsiConsole;")]
    [InlineData("using Con = Spectre.Console.AnsiConsole;")]
    [InlineData("using Con = AnsiConsole;")]
    [InlineData("namespace X;\n\nusing static Spectre.Console.AnsiConsole;\n")]
    public void The_inventory_recognises_the_aliases_and_static_imports(string source)
    {

        Assert.Matches(ConsoleAlias, source);

    }

    [Theory]
    [InlineData("using Spectre.Console;")]
    [InlineData("using static System.Console;")]
    [InlineData("using static Spectre.Console.AnsiConsoleSettings;")]
    [InlineData("using Settings = Spectre.Console.AnsiConsoleSettings;")]
    public void The_inventory_leaves_ordinary_imports_alone(string source)
    {

        Assert.DoesNotMatch(ConsoleAlias, source);

    }

    [Theory]
    [InlineData("<Using Include=\"Spectre.Console.AnsiConsole\" Static=\"true\" />")]
    [InlineData("<Using Static=\"true\" Include=\"Spectre.Console.AnsiConsole\" />")]
    public void The_inventory_recognises_a_project_level_import(string project)
    {

        Assert.Matches(ProjectConsoleUsing, project);

    }

    [Theory]
    [InlineData("<Using Include=\"Spectre.Console\" />")]
    [InlineData("<Using Include=\"System.Net.Http\" />")]
    public void The_inventory_leaves_other_project_imports_alone(string project)
    {

        Assert.DoesNotMatch(ProjectConsoleUsing, project);

    }

}
