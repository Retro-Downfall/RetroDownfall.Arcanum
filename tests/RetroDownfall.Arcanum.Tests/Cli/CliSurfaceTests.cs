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
    [Theory]
    [InlineData("show")]
    [InlineData("correct")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("pin")]
    [InlineData("unpin")]
    public void Lexicon_curation_exposes_exact_campaign_and_safe_authored_content_options(string verb)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        using ServiceProvider provider = services.BuildServiceProvider();

        RootCommand root = CliCommandTree.Build(provider, out _);

        Command memory = Assert.Single(root.Subcommands, command => command.Name == "memory");

        Command lexicon = Assert.Single(memory.Subcommands, command => command.Name == "lexicon");

        Command command = Assert.Single(lexicon.Subcommands, command => command.Name == verb);

        Option<Guid?> campaign = Assert.IsType<Option<Guid?>>(Assert.Single(command.Options, option => option.Name == "--campaign"));

        Assert.Contains("-C", campaign.Aliases);

        Assert.Equal("name", Assert.Single(command.Arguments).Name);

        if (verb == "correct")
        {
            Option file = Assert.Single(command.Options, option => option.Name == "--file");

            Assert.True(file.Required);

            Assert.Contains("-f", file.Aliases);
        }

        Assert.Empty(root.Parse(["memory", "lexicon", verb, "Operator", "--json", "--plain", "--yes", "--no-context",
            .. verb == "correct" ? new[] { "--file", "correction.json" } : []]).Errors);
    }

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
    /// A command-reference row's options cell says what the command registers: "None beyond global or
    /// inherited family options" is true only for a command with no options of its own, and an option
    /// the cell names is one the parser accepts on that command.
    /// </summary>
    /// <remarks>
    /// The row test above proves a spelling exists and nothing about what it accepts, so a command
    /// that gained options after its row was written kept saying it had none. <c>open center</c> did
    /// exactly that: it registers <c>-c</c> and <c>-r</c>, and its row told readers there were none
    /// while the section below it documented them on the separate <c>center</c> command.
    /// </remarks>
    [Fact]
    public void Every_documented_command_row_states_the_options_the_command_registers()
    {
        CliSurfaceMap map = BuildMap();

        Dictionary<string, CliSurfaceCommand> registered = Walk(map)
            .ToDictionary(static command => command.Path, StringComparer.Ordinal);

        List<(int Number, string Command, string Options)> rows = [.. CommandReferenceOptionRows()];

        // Asserted before the contents, so a table the reader found no options column in does not pass
        // by checking nothing.
        Assert.True(rows.Count > 50, $"Only {rows.Count} command rows with an options column were read.");

        List<string> offenders = [];

        // A command with several modes has one row per mode, each naming the options of its own mode, so
        // what a command registers is compared with everything its rows name together.
        Dictionary<string, HashSet<string>> namedByCommand = new(StringComparer.Ordinal);

        foreach ((int number, string commandCell, string optionsCell) in rows)
        {
            Match match = DocumentedCommandCell.Match(commandCell);

            if (!match.Success
                || match.Groups["unregistered"].Success)
            {
                continue;
            }

            string path = VerbPath(match.Groups["spelling"].Value);

            if (!registered.TryGetValue(path, out CliSurfaceCommand? command))
            {
                continue;
            }

            string[] own =
            [
                .. command.Options
                    .Where(static option => !option.Recursive)
                    .Select(static option => option.Name)
                    .Order(StringComparer.Ordinal),
            ];

            // An option a command requires is written into its spelling, so the first cell names it too.
            HashSet<string> namedInSpelling = new(
                DocumentedOptionName.Matches(commandCell).Select(static match => match.Value),
                StringComparer.Ordinal);

            HashSet<string> named = new(
                namedInSpelling.Concat(DocumentedOptionName.Matches(optionsCell).Select(static match => match.Value)),
                StringComparer.Ordinal);

            if (!namedByCommand.TryGetValue(path, out HashSet<string>? commandNames))
            {
                commandNames = new HashSet<string>(StringComparer.Ordinal);

                namedByCommand[path] = commandNames;
            }

            // An option is documented by an entry of its own, which opens with the option's spelling; a
            // mention inside another option's description (or "global `--yes`") describes that option and
            // does not document this one, so only the spelling cell and the entry openers count here.
            commandNames.UnionWith(namedInSpelling);

            commandNames.UnionWith(OptionsOpeningAnEntry(optionsCell));

            if (optionsCell.StartsWith("None", StringComparison.Ordinal))
            {
                string[] unnamed = [.. own.Where(option => !namedInSpelling.Contains(option))];

                if (unnamed.Length > 0)
                {
                    offenders.Add($"line {number}: arcanum {path} says it has no options of its own but registers {string.Join(", ", unnamed)}");
                }

                continue;
            }

            HashSet<string> accepted = new(own, StringComparer.Ordinal);

            string[] segments = path.Split(' ');

            for (int depth = 1; depth < segments.Length; depth++)
            {
                accepted.UnionWith(
                    registered[string.Join(' ', segments[..depth])].Options
                        .Where(static option => option.Recursive)
                        .Select(static option => option.Name));
            }

            accepted.UnionWith(map.GlobalOptions.Select(static option => option.Name));

            foreach (string option in named.Where(option => !accepted.Contains(option)).Order(StringComparer.Ordinal))
            {
                offenders.Add($"line {number}: arcanum {path} names {option}, which it does not register");
            }
        }

        foreach ((string path, HashSet<string> names) in namedByCommand)
        {
            foreach (CliSurfaceOption option in registered[path].Options.Where(option => !option.Recursive && !names.Contains(option.Name)))
            {
                offenders.Add($"arcanum {path} registers {option.Name}, which none of its rows names");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A command-reference row disagrees with the options the command registers:"
                + global::System.Environment.NewLine
                + string.Join(global::System.Environment.NewLine, offenders));
    }

    /// <summary>
    /// A command registers an option, and its rows document it with an entry of its own. Mentioning the
    /// option inside another option's description, or as the shared global one, documents nothing.
    /// </summary>
    [Fact]
    public void An_option_is_documented_only_by_an_entry_that_opens_with_its_spelling()
    {
        const string cell = "`-c, --continue` — Reopen the most recent Session; conflicts with `--resume`.<br>"
            + "`--campaign`, `-C <id>` — A Campaign.<br>"
            + "`--limit <1..50>` and `--cursor <token>`.<br>"
            + "`--a <x>` / `--b <y>` — A pair.<br>"
            + "Declining discards nothing; the global `--yes` answers the prompt.";

        Assert.Equal(
            ["--continue", "--campaign", "--limit", "--cursor", "--a", "--b"],
            OptionsOpeningAnEntry(cell));
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
    public void The_covenant_heading_states_the_direct_verbs_and_review_branch_the_tree_registers()
    {
        CliSurfaceCommand covenant = Assert.Single(
            Walk(BuildMap()),
            static command => command.Path == "memory covenant");

        int directVerbs = covenant.Commands.Count(static command => command.Name != "review");

        CliSurfaceCommand review = Assert.Single(
            covenant.Commands,
            static command => command.Name == "review");

        string reference = File.ReadAllText(CommandReferencePath());

        Assert.Equal(12, directVerbs);

        Assert.Equal(
            ["apply", "list"],
            review.Commands.Select(static command => command.Name).Order(StringComparer.Ordinal));

        Assert.Contains(
            $"#### Dedicated Covenant management commands ({NumberWord(directVerbs)} direct verbs plus review, the rest contract-frozen)",
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

    private static readonly Regex DocumentedOptionName = new(
        @"(?<=`[^`]*)(?<![\w-])--[a-z][a-z0-9-]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The leading run of backticked spellings that opens an entry of an options cell, such as
    /// <c>`-c, --continue`</c>, <c>`--campaign`, `-C &lt;id&gt;`</c> or <c>`--a &lt;x&gt;` / `--b &lt;y&gt;`</c>.
    /// </summary>
    private static readonly Regex EntryOpener = new(
        @"^(?:`[^`]*`(?:\s*(?:,|/|\||or|and)\s*)?)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The long options an options cell documents with an entry of their own. Entries are separated by
    /// <c>&lt;br&gt;</c> and each opens with the spelling it documents, so an option mentioned only inside
    /// another entry's description is not among them.
    /// </summary>
    internal static IEnumerable<string> OptionsOpeningAnEntry(string optionsCell) =>
        optionsCell
            .Split("<br>", StringSplitOptions.None)
            .Select(static entry => EntryOpener.Match(entry.Trim()))
            .Where(static opener => opener.Success)
            .SelectMany(static opener => DocumentedOptionName.Matches(opener.Value).Select(static match => match.Value));

    /// <summary>
    /// The command cell and the options cell of every row, in the tables whose last column lists a
    /// command's options, above the removed-spellings section.
    /// </summary>
    private static IEnumerable<(int Number, string Command, string Options)> CommandReferenceOptionRows()
    {
        int number = 0;

        int optionsColumn = -1;

        bool previousWasRow = false;

        foreach (string line in File.ReadLines(CommandReferencePath()))
        {
            number++;

            if (line.StartsWith("## Removed spellings", StringComparison.Ordinal))
            {
                yield break;
            }

            if (!line.StartsWith('|'))
            {
                previousWasRow = false;

                continue;
            }

            string[] cells = UnescapedPipe.Split(line);

            if (!previousWasRow)
            {
                // The first row of a table is its header; the options column is its last one when named so.
                string last = cells.Length >= 3 ? cells[^2].Trim() : string.Empty;

                optionsColumn = last is "Additional command options" or "Options"
                    ? cells.Length - 2
                    : -1;
            }

            previousWasRow = true;

            if (optionsColumn < 2
                || cells.Length - 2 != optionsColumn)
            {
                continue;
            }

            yield return (
                number,
                cells[1].Replace("\\|", "|", StringComparison.Ordinal).Trim(),
                cells[optionsColumn].Replace("\\|", "|", StringComparison.Ordinal).Trim());
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

            11 => "eleven",

            12 => "twelve",

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
        return Path.Combine(
            global::RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot(),
            "docs",
            "Arcanum.CommandMap.json");
    }

    private static bool IsShort(string alias) =>
        alias.Length == 2 && alias[0] == '-' && alias[1] != '-';
}
