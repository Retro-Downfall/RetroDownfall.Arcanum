using System.CommandLine;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The command surface is projected from the live <see cref="RootCommand"/> rather than
/// re-declared, so help, completion, the committed command map, and the alias-absence tests all
/// observe the same tree the parser observes.
/// </summary>
public sealed class CliSurfaceTests
{

    [Fact]
    public void Surface_projects_the_live_tree_root()
    {

        CliSurfaceMap map = BuildMap();

        Assert.NotEmpty(map.Commands);

        Assert.Contains(map.Commands, command => command.Path == "run");

        Assert.Contains(map.Commands, command => command.Path == "doctor");

    }

    [Fact]
    public void Every_command_carries_help_text()
    {

        List<string> missing =
        [
            .. Walk(BuildMap())
                .Where(static command => string.IsNullOrWhiteSpace(command.Description))
                .Select(static command => command.Path),
        ];

        Assert.Empty(missing);

    }

    [Fact]
    public void Every_option_and_argument_carries_help_text()
    {

        List<string> missing = [];

        foreach (CliSurfaceCommand command in Walk(BuildMap()))
        {

            missing.AddRange(
                command.Options
                    .Where(static option => string.IsNullOrWhiteSpace(option.Description))
                    .Select(option => $"{command.Path} {option.Name}"));

            missing.AddRange(
                command.Arguments
                    .Where(static argument => string.IsNullOrWhiteSpace(argument.Description))
                    .Select(argument => $"{command.Path} <{argument.Name}>"));

        }

        Assert.Empty(missing);

    }

    /// <summary>
    /// Claude parity does not justify ambiguous parsing: a short flag means exactly one thing
    /// everywhere in the tree, so <c>-c</c> cannot be <c>--continue</c> here and <c>--campaign</c>
    /// there.
    /// </summary>
    [Fact]
    public void Short_options_have_exactly_one_meaning_across_the_whole_tree()
    {

        Dictionary<string, string> meanings = [];

        List<string> collisions = [];

        foreach (CliSurfaceCommand command in Walk(BuildMap()))
        {

            foreach (CliSurfaceOption option in command.Options)
            {

                foreach (string alias in option.Aliases.Where(IsShort))
                {

                    if (meanings.TryGetValue(alias, out string? existing)
                        && existing != option.Name)
                    {

                        collisions.Add($"{alias} means {existing} and {option.Name} ({command.Path})");

                        continue;

                    }

                    meanings[alias] = option.Name;

                }

            }

        }

        Assert.Empty(collisions);

    }

    [Fact]
    public void Short_option_table_publishes_the_claude_aligned_meanings()
    {

        Dictionary<string, string> table = BuildMap()
            .ShortOptions
            .ToDictionary(static entry => entry.Alias, static entry => entry.Option, StringComparer.Ordinal);

        Assert.Equal("--continue", table["-c"]);

        Assert.Equal("--campaign", table["-C"]);

        Assert.Equal("--resume", table["-r"]);

        Assert.Equal("--print", table["-p"]);

        Assert.Equal("--verbose", table["-v"]);

        Assert.Equal("--model", table["-m"]);

        Assert.Equal("--session", table["-s"]);

        Assert.Equal("--workspace", table["-w"]);

    }

    [Fact]
    public void Diagnostic_mcp_invoke_help_names_master_pipeline_reservation_not_a_ward_gate()
    {

        CliSurfaceCommand invoke = Walk(BuildMap()).Single(
            static command => command.Path == "mcp invoke");

        Assert.Equal(
            "Invoke one external MCP tool diagnostically; internal tool names are reserved for the Master execution pipeline.",
            invoke.Description);

        Assert.DoesNotContain("Forbidden Art", invoke.Description, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("blocked server-side", invoke.Description, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Ward_resolve_help_describes_retained_record_resolution_not_tool_admission()
    {

        CliSurfaceCommand resolve = Walk(BuildMap()).Single(
            static command => command.Path == "ward resolve");

        CliSurfaceOption allow = resolve.Options.Single(
            static option => option.Name == "--allow");

        CliSurfaceOption deny = resolve.Options.Single(
            static option => option.Name == "--deny");

        Assert.Equal("Record an allowed resolution.", allow.Description);

        Assert.Equal("Record a denied resolution.", deny.Description);

        Assert.DoesNotContain("proceed", allow.Description, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("tool call", deny.Description, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// <c>docs/Arcanum.CommandMap.json</c> is the committed, machine-readable command contract.
    /// Regenerate it with <c>ARCANUM_UPDATE_COMMAND_MAP=1 dotnet test --filter
    /// Committed_command_map_matches_the_live_tree</c> and review the diff: an unintended entry in
    /// that diff is an unintended change to the public CLI surface.
    /// </summary>
    [Fact]
    public void Committed_command_map_matches_the_live_tree()
    {

        string path = CommandMapPath();

        string actual = CliSurfaceWriter.ToJson(BuildMap()).ReplaceLineEndings("\n");

        if (global::System.Environment.GetEnvironmentVariable("ARCANUM_UPDATE_COMMAND_MAP") == "1")
        {

            File.WriteAllText(path, actual);

        }

        string expected = File.ReadAllText(path).ReplaceLineEndings("\n");

        Assert.Equal(expected, actual);

    }

    [Fact]
    public void Command_map_generation_is_byte_for_byte_stable()
    {

        Assert.Equal(
            CliSurfaceWriter.ToJson(BuildMap()),
            CliSurfaceWriter.ToJson(BuildMap()));

    }

    /// <summary>
    /// Every command spelling the reference prints as a table row resolves in the live tree, unless
    /// the row says out loud that it does not.
    /// </summary>
    /// <remarks>
    /// <para>The reference is the document the agent orientation file calls the complete CLI surface,
    /// so a row that reads like every other row reads as a shipped verb. Six were not: a
    /// <c>doctor</c> branch, two Campaign-path rows, two Session-binding rows, and a
    /// <c>security host-process-tools enable</c> row a startup failure used to send operators to.
    /// One prose sentence above the table named some of them, and a reader who scans a table does not
    /// read the prose above it, which is why the marker belongs in the row.</para>
    /// <para>The "Removed spellings" section is skipped wholesale: every spelling there is one that
    /// must fail to parse, and the section says so in its own heading.</para>
    /// </remarks>
    [Fact]
    public void Every_documented_command_row_resolves_or_declares_itself_unregistered()
    {

        HashSet<string> registered = new(
            Walk(BuildMap()).Select(static command => command.Path),
            StringComparer.Ordinal);

        List<(int Number, string Cell)> rows = [.. CommandReferenceRows()];

        // A table the reader found no rows in satisfies the loop below vacuously - every documented
        // row resolves when there are none - and reports green having checked nothing. A moved file,
        // a renamed "Removed spellings" heading that now matches the first line, or a table rewritten
        // without pipes would all land here, so the count is asserted before the contents are.
        Assert.NotEmpty(rows);

        List<string> offenders = [];

        foreach ((int Number, string Cell) row in rows)
        {

            Match match = DocumentedCommandCell.Match(row.Cell);

            if (!match.Success
                || match.Groups["unregistered"].Success)
            {

                continue;

            }

            string path = VerbPath(match.Groups["spelling"].Value);

            if (path.Length == 0
                || registered.Contains(path))
            {

                continue;

            }

            offenders.Add($"line {row.Number}: arcanum {path}");

        }

        Assert.True(
            offenders.Count == 0,
            "A command-reference row names a verb the command tree does not register. Register it, or"
                + " mark the row unregistered:"
                + global::System.Environment.NewLine
                + string.Join(global::System.Environment.NewLine, offenders));

    }

    /// <summary>
    /// The dedicated-Covenant heading states a count, and a count is a claim.
    /// </summary>
    /// <remarks>
    /// An operator who scans headings and reads "four registered" never tries <c>pin</c>,
    /// <c>unpin</c>, <c>mask</c>, or <c>unmask</c>. The heading's count is taken from the tree rather
    /// than written as a literal, so registering a tenth verb reds this instead of leaving the heading
    /// one behind. The separate pin on nine is deliberate and not a duplicate of that: without it the
    /// two halves could drift together and still agree, and the point is that a change to this family
    /// is read by someone rather than absorbed.
    /// </remarks>
    [Fact]
    public void The_covenant_heading_states_the_number_of_verbs_the_tree_registers()
    {

        int registered = Walk(BuildMap())
            .Count(static command =>
                command.Path.StartsWith("memory covenant ", StringComparison.Ordinal));

        string reference = File.ReadAllText(CommandReferencePath());

        Assert.Equal(9, registered);

        Assert.Contains(
            $"#### Dedicated Covenant management commands ({NumberWord(registered)} registered, the rest contract-frozen)",
            reference,
            StringComparison.Ordinal);

    }

    private static readonly Regex DocumentedCommandCell = new(
        @"^(?<unregistered>\*\*\(not registered\)\*\* )?`arcanum (?<spelling>[^`]*)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Splits a markdown row on its real cell boundaries, keeping escaped pipes inside a cell.</summary>
    private static readonly Regex UnescapedPipe = new(
        @"(?<!\\)\|",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The first cell of every table row above the removed-spellings section.</summary>
    private static IEnumerable<(int Number, string Cell)> CommandReferenceRows()
    {

        int number = 0;

        foreach (string line in File.ReadLines(CommandReferencePath()))
        {

            number++;

            if (line.StartsWith("## Removed spellings", StringComparison.Ordinal))
            {

                yield break;

            }

            if (!line.StartsWith('|'))
            {

                continue;

            }

            string[] cells = UnescapedPipe.Split(line);

            if (cells.Length < 2)
            {

                continue;

            }

            yield return (number, cells[1].Replace("\\|", "|", StringComparison.Ordinal).Trim());

        }

    }

    /// <summary>The verb path of a spelling, stopping at its first argument, option, or alternation.</summary>
    private static string VerbPath(string spelling) =>
        string.Join(
            ' ',
            spelling
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .TakeWhile(static token => !"<[-(|".Contains(token[0])));

    private static string NumberWord(int value) =>
        value switch
        {
            4 => "four",

            9 => "nine",

            10 => "ten",

            _ => value.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
        };

    private static string CommandReferencePath() =>
        Path.Combine(
            Path.GetDirectoryName(CommandMapPath())!,
            "Arcanum.Command.Reference.md");

    internal static CliSurfaceMap BuildMap()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        RootCommand root = CliCommandTree.Build(provider, out _);

        return CliSurfaceBuilder.Build(root);

    }

    internal static IEnumerable<CliSurfaceCommand> Walk(CliSurfaceMap map) =>
        map.Commands.SelectMany(Walk);

    internal static IEnumerable<CliSurfaceCommand> Walk(CliSurfaceCommand command)
    {

        yield return command;

        foreach (CliSurfaceCommand child in command.Commands.SelectMany(Walk))
        {

            yield return child;

        }

    }

    internal static string CommandMapPath()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
        {

            directory = directory.Parent;

        }

        Assert.NotNull(directory);

        return Path.Combine(directory.FullName, "docs", "Arcanum.CommandMap.json");

    }

    private static bool IsShort(string alias) =>
        alias.Length == 2 && alias[0] == '-' && alias[1] != '-';

}
