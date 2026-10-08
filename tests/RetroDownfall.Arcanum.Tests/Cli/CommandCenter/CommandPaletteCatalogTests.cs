using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// The command palette is the place an operator who does not know a key or a command name goes to
/// find one, so every row has to say what it does and run exactly that. Its slash-backed rows and
/// the slash menu read the slash registry rather than copying it, and every line they put in front
/// of the parser is replayed through it here.
/// </summary>
public sealed class CommandPaletteCatalogTests
{
    private const string SampleId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void The_palette_lists_the_curated_actions_in_order()
    {
        Assert.Equal(
            [
                "New Session",
                "Choose Model",
                "Open Sessions",
                "Slash Commands",
                "Refresh",
                "Provider List",
                "MCP Status",
                "Arsenal",
                "Campaign List",
                "Spell List",
                "Ward List",
                "Doctor",
                "Context",
                "Help",
                "Quit",
            ],
            CommandPaletteCatalog.Actions.Select(static entry => entry.Title));
    }

    /// <summary>
    /// "Model List" ran <c>/model list</c>, which the parser reads as selecting a model called
    /// <c>list</c>, and "Mana" ran <c>/mana</c>, a removed spelling that only prints its own error.
    /// </summary>
    [Fact]
    public void No_entry_keeps_a_title_that_ran_the_wrong_command()
    {
        Assert.DoesNotContain(CommandPaletteCatalog.Actions, static entry => entry.Title == "Model List");

        Assert.DoesNotContain(CommandPaletteCatalog.Actions, static entry => entry.Title == "Mana");
    }

    [Fact]
    public void Choose_Model_opens_the_model_drop_down_instead_of_running_a_slash_command()
    {
        CommandPaletteEntry choose = Assert.Single(
            CommandPaletteCatalog.Actions,
            static entry => entry.Title == "Choose Model");

        Assert.Equal(CommandPaletteTarget.ChooseModel, choose.Target);

        Assert.Null(choose.SlashText);
    }

    /// <summary>The rows that drive the window rather than a slash command name the key that does the same.</summary>
    [Fact]
    public void Interface_actions_say_what_they_do_and_which_key_does_it_too()
    {
        Assert.Equal(
            [
                ("New Session", "Start a fresh conversation (Ctrl+N)", CommandPaletteTarget.NewSession),
                ("Choose Model", "Pick the model for this session from the configured list", CommandPaletteTarget.ChooseModel),
                ("Open Sessions", "Browse and resume earlier sessions (Ctrl+O)", CommandPaletteTarget.OpenSessions),
                ("Slash Commands", "Browse every /command (same as typing / in an empty composer)", CommandPaletteTarget.BrowseSlashCommands),
                ("Refresh", "Reload the session list (Ctrl+R)", CommandPaletteTarget.Refresh),
                ("Help", "Keys and commands (F1)", CommandPaletteTarget.Help),
                ("Quit", "Leave Command Center (Ctrl+Q)", CommandPaletteTarget.Quit),
            ],
            CommandPaletteCatalog.Actions
                .Where(static entry => entry.Target != CommandPaletteTarget.RunSlashText)
                .Select(static entry => (entry.Title, entry.Description, entry.Target)));
    }

    [Fact]
    public void Every_slash_backed_entry_runs_a_spelling_the_parser_accepts()
    {
        ShellCommandParser parser = new();

        CommandPaletteEntry[] slashBacked =
        [
            .. CommandPaletteCatalog.Actions.Where(static entry => entry.Target == CommandPaletteTarget.RunSlashText),
        ];

        Assert.Equal(
            ["/provider list", "/mcp", "/arsenal", "/campaign list", "/spell list", "/ward list", "/doctor", "/context"],
            slashBacked.Select(static entry => entry.SlashText));

        foreach (CommandPaletteEntry entry in slashBacked)
        {
            ParsedShellCommand parsed = parser.Parse(entry.SlashText!);

            Assert.True(
                parsed.Kind is not (ShellCommandKind.Denied or ShellCommandKind.Unknown),
                $"`{entry.Title}` runs `{entry.SlashText}`, which the parser rejects: {parsed.DenialMessage}");
        }
    }

    /// <summary>The palette and <c>/help</c> cannot describe one command two ways: the text is read, never copied.</summary>
    [Fact]
    public void Slash_backed_entries_describe_their_command_in_the_registry_s_words()
    {
        foreach (CommandPaletteEntry entry in CommandPaletteCatalog.Actions
            .Where(static entry => entry.Target == CommandPaletteTarget.RunSlashText))
        {
            string name = entry.SlashText!.TrimStart('/').Split(' ')[0];

            Assert.True(SlashCommandRegistry.TryResolve(name, out SlashCommandDescriptor? descriptor));

            Assert.Equal(descriptor!.Description, entry.Description);
        }
    }

    [Fact]
    public void The_slash_menu_lists_every_registry_command_in_registry_order()
    {
        Assert.Equal(SlashCommandRegistry.All.Count, CommandPaletteCatalog.SlashCommands.Count);

        Assert.Equal(
            SlashCommandRegistry.All.Select(static command => (command.Usage, command.Description)),
            CommandPaletteCatalog.SlashCommands.Select(static entry => (entry.Title, entry.Description)));

        Assert.All(
            CommandPaletteCatalog.SlashCommands,
            static entry => Assert.Equal(CommandPaletteTarget.SlashMenuCommand, entry.Target));
    }

    [Fact]
    public void A_command_runs_from_the_menu_exactly_when_its_usage_takes_no_argument()
    {
        foreach ((SlashCommandDescriptor command, CommandPaletteEntry entry) in SlashCommandRegistry.All
            .Zip(CommandPaletteCatalog.SlashCommands))
        {
            bool takesArgument = command.Usage.IndexOfAny(['<', '[', '|']) >= 0;

            Assert.Equal(!takesArgument, entry.RunsImmediately);
        }
    }

    /// <summary>
    /// <c>/provider list</c> takes no argument but is two words, so what the menu runs is the usage
    /// itself: <c>/provider</c> on its own is not a command.
    /// </summary>
    [Fact]
    public void Every_command_the_menu_runs_is_a_spelling_the_parser_accepts()
    {
        ShellCommandParser parser = new();

        foreach (CommandPaletteEntry entry in CommandPaletteCatalog.SlashCommands.Where(static entry => entry.RunsImmediately))
        {
            Assert.Equal(entry.Title, entry.SlashText);

            ParsedShellCommand parsed = parser.Parse(entry.SlashText!);

            Assert.True(
                parsed.Kind is not (ShellCommandKind.Denied or ShellCommandKind.Unknown),
                $"The slash menu runs `{entry.SlashText}`, which the parser rejects: {parsed.DenialMessage}");
        }
    }

    [Fact]
    public void Each_completion_is_the_fixed_part_of_its_usage_followed_by_one_space()
    {
        foreach ((SlashCommandDescriptor command, CommandPaletteEntry entry) in SlashCommandRegistry.All
            .Zip(CommandPaletteCatalog.SlashCommands))
        {
            if (entry.RunsImmediately)
            {
                Assert.Null(entry.CompletionText);

                continue;
            }

            string completion = Assert.IsType<string>(entry.CompletionText);

            Assert.StartsWith("/" + command.Name, completion, StringComparison.Ordinal);

            Assert.EndsWith(" ", completion, StringComparison.Ordinal);

            Assert.False(completion.EndsWith("  ", StringComparison.Ordinal), $"`{completion}` ends in more than one space.");

            Assert.StartsWith(completion, command.Usage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_completions_stop_where_the_first_argument_starts()
    {
        Assert.Equal(
            [
                "/model ",
                "/mcp ",
                "/resume ",
                "/session ",
                "/campaign list ",
                "/spell list ",
                "/ward list ",
                "/fork ",
                "/branch ",
                "/attach ",
                "/attachments ",
                "/pin ",
                "/unpin ",
            ],
            CommandPaletteCatalog.SlashCommands
                .Where(static entry => entry.CompletionText is not null)
                .Select(static entry => entry.CompletionText));
    }

    public static TheoryData<string, string> CompletionExamples => new()
    {
        { "/model ", "gemma4:e4b" },
        { "/mcp ", "reload" },
        { "/resume ", SampleId },
        { "/session ", "archive " + SampleId },
        { "/campaign list ", "50" },
        { "/spell list ", "next-page-cursor" },
        { "/ward list ", "50" },
        { "/fork ", "confirm" },
        { "/branch ", "parent" },
        { "/attach ", "notes.txt" },
        { "/attachments ", "add notes v2" },
        { "/pin ", "file README.md" },
        { "/unpin ", SampleId },
    };

    /// <summary>
    /// A completion is a line the operator finishes rather than one that runs as it stands, so it is
    /// pinned by finishing it the way its usage documents and replaying the result through the parser.
    /// </summary>
    [Theory]
    [MemberData(nameof(CompletionExamples))]
    public void A_completion_finished_with_a_documented_argument_is_accepted_by_the_parser(
        string completion,
        string argument)
    {
        Assert.Contains(CommandPaletteCatalog.SlashCommands, entry => entry.CompletionText == completion);

        ParsedShellCommand parsed = new ShellCommandParser().Parse(completion + argument);

        Assert.True(
            parsed.Kind is not (ShellCommandKind.Denied or ShellCommandKind.Unknown),
            $"`{completion}{argument}` is rejected by the parser: {parsed.DenialMessage}");
    }

    /// <summary>A command added to the registry with an argument cannot skip the replay above.</summary>
    [Fact]
    public void Every_completion_has_a_replayed_example()
    {
        Assert.Equal(
            CompletionExamples.Select(static row => (string)row[0]).Order(StringComparer.Ordinal),
            CommandPaletteCatalog.SlashCommands
                .Select(static entry => entry.CompletionText)
                .OfType<string>()
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_empty_filter_keeps_every_entry_in_catalog_order()
    {
        Assert.Equal(CommandPaletteCatalog.Actions, CommandPaletteCatalog.Filter(CommandPaletteMode.Actions, string.Empty));

        Assert.Equal(CommandPaletteCatalog.Actions, CommandPaletteCatalog.Filter(CommandPaletteMode.Actions, "   "));

        Assert.Equal(CommandPaletteCatalog.SlashCommands, CommandPaletteCatalog.Filter(CommandPaletteMode.Slash, "/"));

        Assert.Equal(CommandPaletteCatalog.SlashCommands, CommandPaletteCatalog.Filter(CommandPaletteMode.Slash, string.Empty));
    }

    [Fact]
    public void A_slash_filter_ranks_the_command_whose_name_starts_with_it_first()
    {
        IReadOnlyList<CommandPaletteEntry> matches = CommandPaletteCatalog.Filter(CommandPaletteMode.Slash, "/he");

        Assert.Equal("/help", matches[0].Title);
    }

    [Fact]
    public void A_slash_filter_narrows_to_the_one_command_it_names()
    {
        CommandPaletteEntry only = Assert.Single(CommandPaletteCatalog.Filter(CommandPaletteMode.Slash, "/mod"));

        Assert.Equal("/model [<name>]", only.Title);
    }

    [Fact]
    public void A_palette_filter_narrows_to_the_one_action_it_names()
    {
        CommandPaletteEntry only = Assert.Single(CommandPaletteCatalog.Filter(CommandPaletteMode.Actions, "model"));

        Assert.Equal("Choose Model", only.Title);
    }

    [Fact]
    public void Filtering_ignores_case()
    {
        Assert.Equal(
            CommandPaletteCatalog.Filter(CommandPaletteMode.Actions, "model"),
            CommandPaletteCatalog.Filter(CommandPaletteMode.Actions, "MoDeL"));

        Assert.Equal(
            CommandPaletteCatalog.Filter(CommandPaletteMode.Slash, "/he"),
            CommandPaletteCatalog.Filter(CommandPaletteMode.Slash, "/HE"));
    }

    /// <summary>
    /// Names that start with the text come first, then names that merely contain it, then entries
    /// that only mention it in their description; catalog order holds inside each band and no entry
    /// appears twice.
    /// </summary>
    [Fact]
    public void Filtering_ranks_name_prefix_then_name_substring_then_description()
    {
        Assert.Equal(
            ["Provider List", "Campaign List", "Spell List", "Ward List", "Choose Model", "Refresh"],
            CommandPaletteCatalog.Filter(CommandPaletteMode.Actions, "list").Select(static entry => entry.Title));

        Assert.Equal(
            ["/pins", "/pin <kind> <target>", "/unpin <pin-id>"],
            CommandPaletteCatalog.Filter(CommandPaletteMode.Slash, "/pin").Select(static entry => entry.Title));

        IReadOnlyList<CommandPaletteEntry> at = CommandPaletteCatalog.Filter(CommandPaletteMode.Slash, "/at");

        Assert.Equal(
            ["/attach <path>", "/attachments [add|reveal|refresh] <name> [vN]", "/status"],
            at.Take(3).Select(static entry => entry.Title));

        Assert.Equal(at.Count, at.Distinct().Count());
    }

    /// <summary>The slash menu filters on the command name only: an argument typed after it narrows nothing.</summary>
    [Fact]
    public void A_slash_filter_reads_the_name_up_to_the_first_space()
    {
        Assert.Equal(
            CommandPaletteCatalog.Filter(CommandPaletteMode.Slash, "/mod"),
            CommandPaletteCatalog.Filter(CommandPaletteMode.Slash, "/mod gemma4"));
    }

    /// <summary>An alias finds its command as surely as the name does.</summary>
    [Fact]
    public void A_slash_filter_matches_an_alias()
    {
        Assert.Equal("/help", CommandPaletteCatalog.Filter(CommandPaletteMode.Slash, "/?")[0].Title);
    }

    [Theory]
    [InlineData(58)]
    [InlineData(74)]
    [InlineData(112)]
    public void Every_rendered_row_is_exactly_the_row_width_with_its_last_cell_blank(int rowWidth)
    {
        foreach (CommandPaletteMode mode in Enum.GetValues<CommandPaletteMode>())
        {
            IReadOnlyList<CommandPaletteEntry> entries = CommandPaletteCatalog.Filter(mode, string.Empty);

            IReadOnlyList<string> rows = CommandPaletteCatalog.Render(entries, mode, rowWidth);

            Assert.Equal(entries.Count, rows.Count);

            Assert.All(rows, row =>
            {
                Assert.Equal(rowWidth, TerminalCellMetrics.MeasureWidth(row));

                Assert.EndsWith(" ", row, StringComparison.Ordinal);
            });
        }
    }

    [Theory]
    [InlineData(CommandPaletteMode.Actions, 14)]
    [InlineData(CommandPaletteMode.Slash, 24)]
    internal void The_description_column_starts_at_the_same_cell_on_every_row(CommandPaletteMode mode, int titleCells)
    {
        IReadOnlyList<CommandPaletteEntry> entries = CommandPaletteCatalog.Filter(mode, string.Empty);

        IReadOnlyList<string> rows = CommandPaletteCatalog.Render(entries, mode, 74);

        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal("  ", rows[i].Substring(titleCells, 2));

            Assert.NotEqual(' ', rows[i][titleCells + 2]);

            Assert.StartsWith(entries[i].Description[..8], rows[i][(titleCells + 2)..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_description_that_does_not_fit_is_cut_with_an_ellipsis()
    {
        CommandPaletteEntry ward = Assert.Single(
            CommandPaletteCatalog.Actions,
            static entry => entry.Title == "Ward List");

        string row = Assert.Single(CommandPaletteCatalog.Render([ward], CommandPaletteMode.Actions, 58));

        string content = row.TrimEnd();

        Assert.EndsWith("…", content, StringComparison.Ordinal);

        Assert.False(content.EndsWith(" …", StringComparison.Ordinal), $"`{content}` leaves a space before the ellipsis.");

        Assert.InRange(TerminalCellMetrics.MeasureWidth(content), 50, 57);
    }

    [Fact]
    public void A_usage_wider_than_its_column_is_cut_with_an_ellipsis()
    {
        CommandPaletteEntry attachments = Assert.Single(
            CommandPaletteCatalog.SlashCommands,
            static entry => entry.Title.StartsWith("/attachments", StringComparison.Ordinal));

        string row = Assert.Single(CommandPaletteCatalog.Render([attachments], CommandPaletteMode.Slash, 112));

        Assert.Equal("/attachments [add|revea…  ", row[..26]);
    }

    [Theory]
    [InlineData(CommandPaletteMode.Actions, "No matching command")]
    [InlineData(CommandPaletteMode.Slash, "No matching command — Enter sends what you typed")]
    internal void An_empty_result_renders_one_row_saying_so(CommandPaletteMode mode, string expected)
    {
        string row = Assert.Single(CommandPaletteCatalog.Render([], mode, 74));

        Assert.Equal(expected, row.TrimEnd());

        Assert.Equal(74, TerminalCellMetrics.MeasureWidth(row));
    }

    /// <summary>At its natural width the palette shows every description whole.</summary>
    [Theory]
    [InlineData(CommandPaletteMode.Actions, 14)]
    [InlineData(CommandPaletteMode.Slash, 24)]
    internal void The_natural_row_width_fits_the_longest_description(CommandPaletteMode mode, int titleCells)
    {
        IReadOnlyList<CommandPaletteEntry> entries = CommandPaletteCatalog.Filter(mode, string.Empty);

        int natural = CommandPaletteCatalog.NaturalRowWidth(mode);

        Assert.Equal(
            titleCells + 2 + entries.Max(static entry => TerminalCellMetrics.MeasureWidth(entry.Description)),
            natural);

        IReadOnlyList<string> rows = CommandPaletteCatalog.Render(entries, mode, natural + 1);

        for (int i = 0; i < rows.Count; i++)
        {
            Assert.EndsWith(entries[i].Description, rows[i].TrimEnd(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Space_hands_the_line_to_the_composer_with_the_space()
    {
        Assert.Equal(
            SlashMenuStep.ReturnToComposer("/model "),
            CommandPaletteCatalog.DecideSlashKey("/model", SlashMenuKey.Space, Slash("model")));
    }

    [Fact]
    public void Backspace_past_the_slash_returns_to_an_empty_composer()
    {
        Assert.Equal(
            SlashMenuStep.ReturnToComposer(string.Empty),
            CommandPaletteCatalog.DecideSlashKey("/", SlashMenuKey.Backspace, Slash("help")));
    }

    [Fact]
    public void Backspace_inside_the_name_is_left_to_the_filter_field()
    {
        Assert.Null(CommandPaletteCatalog.DecideSlashKey("/mo", SlashMenuKey.Backspace, Slash("model")));
    }

    [Fact]
    public void Escape_returns_to_the_composer_with_what_was_typed()
    {
        Assert.Equal(
            SlashMenuStep.ReturnToComposer("/mo"),
            CommandPaletteCatalog.DecideSlashKey("/mo", SlashMenuKey.Escape, Slash("model")));
    }

    /// <summary>With nothing matching, the typed line is sent, so the parser's did-you-mean answers it.</summary>
    [Fact]
    public void Enter_with_no_match_sends_what_was_typed()
    {
        Assert.Equal(
            SlashMenuStep.Run("/modle"),
            CommandPaletteCatalog.DecideSlashKey("/modle", SlashMenuKey.Enter, selected: null));
    }

    [Fact]
    public void Enter_runs_a_command_that_takes_no_argument()
    {
        Assert.Equal(
            SlashMenuStep.Run("/help"),
            CommandPaletteCatalog.DecideSlashKey("/he", SlashMenuKey.Enter, Slash("help")));

        Assert.Equal(
            SlashMenuStep.Run("/provider list"),
            CommandPaletteCatalog.DecideSlashKey("/prov", SlashMenuKey.Enter, Slash("provider")));
    }

    [Fact]
    public void Enter_completes_a_command_that_takes_an_argument()
    {
        Assert.Equal(
            SlashMenuStep.Complete("/model "),
            CommandPaletteCatalog.DecideSlashKey("/mo", SlashMenuKey.Enter, Slash("model")));

        Assert.Equal(
            SlashMenuStep.Complete("/campaign list "),
            CommandPaletteCatalog.DecideSlashKey("/camp", SlashMenuKey.Enter, Slash("campaign")));
    }

    /// <summary>
    /// The menu narrows a bare <c>/name</c>. A paste that carries an argument, or a slash deleted from
    /// the front of the line, leaves something else in the filter: that line belongs in the composer.
    /// </summary>
    [Theory]
    [InlineData("/model gemma4:e4b")]
    [InlineData("mo")]
    [InlineData("")]
    public void A_filter_that_is_no_longer_a_bare_command_name_goes_back_to_the_composer(string filter)
    {
        Assert.Equal(SlashMenuStep.ReturnToComposer(filter), CommandPaletteCatalog.DecideSlashFilterEdit(filter));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/mo")]
    public void A_bare_command_name_keeps_the_menu_open(string filter)
    {
        Assert.Null(CommandPaletteCatalog.DecideSlashFilterEdit(filter));
    }

    private static CommandPaletteEntry Slash(string name) =>
        CommandPaletteCatalog.SlashCommands.Single(entry =>
            entry.Title == "/" + name || entry.Title.StartsWith("/" + name + " ", StringComparison.Ordinal));
}
