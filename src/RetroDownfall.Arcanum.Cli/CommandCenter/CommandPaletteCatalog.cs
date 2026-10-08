namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>Which list the command palette is showing.</summary>
internal enum CommandPaletteMode
{
    /// <summary>The curated actions <c>Ctrl+K</c> opens.</summary>
    Actions,

    /// <summary>Every registered slash command, opened by typing <c>/</c> into an empty composer.</summary>
    Slash,
}

/// <summary>What running a palette entry does.</summary>
internal enum CommandPaletteTarget
{
    NewSession,
    ChooseModel,
    OpenSessions,
    BrowseSlashCommands,
    Refresh,
    Help,
    Quit,
    RunSlashText,
    SlashMenuCommand,
}

/// <summary>
/// One palette row: what it is called, what it does, and how it runs. A slash-menu row runs its
/// <see cref="SlashText"/> when <see cref="RunsImmediately"/>, and otherwise completes the composer
/// with <see cref="CompletionText"/> for the operator to finish.
/// </summary>
internal sealed record CommandPaletteEntry(
    string Title,
    string Description,
    CommandPaletteTarget Target,
    string? SlashText = null,
    string? CompletionText = null,
    bool RunsImmediately = false);

/// <summary>A key the slash menu gives its own meaning instead of leaving it to the filter field.</summary>
internal enum SlashMenuKey
{
    Space,
    Backspace,
    Escape,
    Enter,
}

internal enum SlashMenuStepKind
{
    /// <summary>Close the menu and leave the text in the composer, unsent.</summary>
    ReturnToComposer,

    /// <summary>Close the menu and put the command's fixed part in the composer, ready for its argument.</summary>
    Complete,

    /// <summary>Close the menu and send the text exactly as typing it into the composer would.</summary>
    Run,
}

/// <summary>What one slash-menu key does, with the composer text it leaves behind.</summary>
internal sealed record SlashMenuStep(SlashMenuStepKind Kind, string Text)
{
    public static SlashMenuStep ReturnToComposer(string text) => new(SlashMenuStepKind.ReturnToComposer, text);

    public static SlashMenuStep Complete(string text) => new(SlashMenuStepKind.Complete, text);

    public static SlashMenuStep Run(string text) => new(SlashMenuStepKind.Run, text);
}

/// <summary>
/// The pure part of the command palette and the slash menu: which rows exist, how typing narrows
/// them, how a row is drawn, and what each slash-menu key does.
/// </summary>
/// <remarks>
/// The palette is curated: a short list of actions, each saying what it does. The slash menu is the
/// whole <see cref="SlashCommandRegistry"/>, so the palette does not have to repeat it. Every
/// description a slash-backed row shows is read from the registry when this type loads, never
/// copied, so the palette, the slash menu and <c>/help</c> cannot describe one command two ways.
/// </remarks>
internal static class CommandPaletteCatalog
{
    /// <summary>Cells the title column takes in the palette: the longest title, <c>Slash Commands</c>.</summary>
    public const int ActionTitleCells = 14;

    /// <summary>Cells the usage column takes in the slash menu; a longer usage is cut with an ellipsis.</summary>
    public const int SlashUsageCells = 24;

    public const string NoMatchingAction = "No matching command";

    public const string NoMatchingSlashCommand = "No matching command — Enter sends what you typed";

    private const string ColumnGap = "  ";

    private const string Ellipsis = "…";

    private static readonly char[] ArgumentMarkers = ['<', '[', '|'];

    /// <summary>The <c>Ctrl+K</c> palette, in display order.</summary>
    public static IReadOnlyList<CommandPaletteEntry> Actions { get; } =
    [
        new("New Session", "Start a fresh conversation (Ctrl+N)", CommandPaletteTarget.NewSession),
        new("Choose Model", "Pick the model for this session from the configured list", CommandPaletteTarget.ChooseModel),
        new("Open Sessions", "Browse and resume earlier sessions (Ctrl+O)", CommandPaletteTarget.OpenSessions),
        new(
            "Slash Commands",
            "Browse every /command (same as typing / in an empty composer)",
            CommandPaletteTarget.BrowseSlashCommands),
        new("Refresh", "Reload the session list (Ctrl+R)", CommandPaletteTarget.Refresh),
        RunsSlashCommand("Provider List", "/provider list"),
        RunsSlashCommand("MCP Status", "/mcp"),
        RunsSlashCommand("Arsenal", "/arsenal"),
        RunsSlashCommand("Campaign List", "/campaign list"),
        RunsSlashCommand("Spell List", "/spell list"),
        RunsSlashCommand("Ward List", "/ward list"),
        RunsSlashCommand("Doctor", "/doctor"),
        RunsSlashCommand("Context", "/context"),
        new("Help", "Keys and commands (F1)", CommandPaletteTarget.Help),
        new("Quit", "Leave Command Center (Ctrl+Q)", CommandPaletteTarget.Quit),
    ];

    /// <summary>The slash menu: one row per registered command, in registry order.</summary>
    public static IReadOnlyList<CommandPaletteEntry> SlashCommands { get; } =
    [
        .. SlashCommandRegistry.All.Select(static command => ForSlashCommand(command)),
    ];

    /// <summary>
    /// The spellings each slash-menu row answers to — its name and its aliases — index-aligned with
    /// <see cref="SlashCommands"/>.
    /// </summary>
    private static readonly string[][] SlashSpellings =
    [
        .. SlashCommandRegistry.All.Select(static command => (string[])[command.Name, .. command.Aliases]),
    ];

    /// <summary>
    /// Narrows a list as the operator types. In the palette the needle is the trimmed text; in the
    /// slash menu it is the command name after the leading <c>/</c>, up to the first space. Matching
    /// ignores case and ranks in three bands, keeping catalog order within each: names that start
    /// with the needle, then names that contain it, then descriptions that contain it.
    /// </summary>
    public static IReadOnlyList<CommandPaletteEntry> Filter(CommandPaletteMode mode, string? text)
    {
        IReadOnlyList<CommandPaletteEntry> entries = mode == CommandPaletteMode.Slash ? SlashCommands : Actions;

        string needle = Needle(mode, text ?? string.Empty);

        if (needle.Length == 0)
        {
            return entries;
        }

        List<CommandPaletteEntry> namePrefix = [];

        List<CommandPaletteEntry> nameContains = [];

        List<CommandPaletteEntry> description = [];

        for (int i = 0; i < entries.Count; i++)
        {
            CommandPaletteEntry entry = entries[i];

            string[] names = mode == CommandPaletteMode.Slash ? SlashSpellings[i] : [entry.Title];

            if (names.Any(name => name.StartsWith(needle, StringComparison.OrdinalIgnoreCase)))
            {
                namePrefix.Add(entry);
            }
            else if (names.Any(name => name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                nameContains.Add(entry);
            }
            else if (entry.Description.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                description.Add(entry);
            }
        }

        return [.. namePrefix, .. nameContains, .. description];
    }

    /// <summary>
    /// Draws one row per entry in two columns: the title padded to its column (cut with an ellipsis
    /// when longer), two spaces, then the description cut with an ellipsis to fit. Every row is
    /// exactly <paramref name="rowWidth"/> cells and its last cell stays blank, so the list's
    /// scroll bar never covers text. An empty list draws one row saying nothing matched.
    /// </summary>
    public static IReadOnlyList<string> Render(
        IReadOnlyList<CommandPaletteEntry> entries,
        CommandPaletteMode mode,
        int rowWidth)
    {
        ArgumentNullException.ThrowIfNull(entries);

        int width = Math.Max(1, rowWidth);

        int contentCells = width - 1;

        if (entries.Count == 0)
        {
            string empty = mode == CommandPaletteMode.Slash ? NoMatchingSlashCommand : NoMatchingAction;

            return [PadToCells(FitToCells(empty, contentCells), width)];
        }

        int titleCells = TitleCells(mode);

        int descriptionCells = contentCells - titleCells - ColumnGap.Length;

        List<string> rows = new(entries.Count);

        foreach (CommandPaletteEntry entry in entries)
        {
            string title = PadToCells(FitToCells(entry.Title, titleCells), titleCells);

            string content = descriptionCells > 0
                ? title + ColumnGap + FitToCells(entry.Description, descriptionCells)
                : FitToCells(title.TrimEnd(), contentCells);

            rows.Add(PadToCells(content, width));
        }

        return rows;
    }

    /// <summary>The narrowest row that shows every description in <paramref name="mode"/> whole.</summary>
    public static int NaturalRowWidth(CommandPaletteMode mode)
    {
        IReadOnlyList<CommandPaletteEntry> entries = mode == CommandPaletteMode.Slash ? SlashCommands : Actions;

        return TitleCells(mode)
            + ColumnGap.Length
            + entries.Max(static entry => TerminalCellMetrics.MeasureWidth(entry.Description));
    }

    /// <summary>
    /// What a slash-menu key does. Space and <c>Esc</c> hand the typed line back to the composer,
    /// Backspace does too once only the <c>/</c> is left, and <c>Enter</c> runs a command that takes
    /// no argument, completes one that does, and sends the typed line when nothing matches so the
    /// parser's did-you-mean answers it. <see langword="null"/> leaves the key to the filter field.
    /// </summary>
    public static SlashMenuStep? DecideSlashKey(string filterText, SlashMenuKey key, CommandPaletteEntry? selected)
    {
        string filter = filterText ?? string.Empty;

        return key switch
        {
            SlashMenuKey.Space => SlashMenuStep.ReturnToComposer(filter + " "),
            SlashMenuKey.Backspace when filter == "/" => SlashMenuStep.ReturnToComposer(string.Empty),
            SlashMenuKey.Escape => SlashMenuStep.ReturnToComposer(filter),
            SlashMenuKey.Enter => selected switch
            {
                null => SlashMenuStep.Run(filter),
                { RunsImmediately: true, SlashText: { } slashText } => SlashMenuStep.Run(slashText),
                { CompletionText: { } completion } => SlashMenuStep.Complete(completion),
                _ => SlashMenuStep.Run(filter),
            },
            _ => null,
        };
    }

    /// <summary>
    /// The slash menu narrows a bare <c>/name</c>. When an edit the keys above did not see leaves
    /// anything else in the filter — a paste carrying an argument, or a <c>/</c> deleted from the
    /// front — the line goes back to the composer as it stands, exactly as a typed space or a
    /// Backspace past the <c>/</c> would send it, rather than being completed over or sent as chat.
    /// </summary>
    public static SlashMenuStep? DecideSlashFilterEdit(string filterText)
    {
        string filter = filterText ?? string.Empty;

        return filter.StartsWith('/') && !filter.Any(char.IsWhiteSpace)
            ? null
            : SlashMenuStep.ReturnToComposer(filter);
    }

    private static CommandPaletteEntry RunsSlashCommand(string title, string slashText)
    {
        string name = slashText.TrimStart('/').Split(' ')[0];

        if (!SlashCommandRegistry.TryResolve(name, out SlashCommandDescriptor? descriptor) || descriptor is null)
        {
            throw new InvalidOperationException(
                $"The command palette entry `{title}` runs `{slashText}`, which the slash registry does not define.");
        }

        return new CommandPaletteEntry(
            title,
            descriptor.Description,
            CommandPaletteTarget.RunSlashText,
            SlashText: slashText);
    }

    /// <summary>
    /// A usage with no argument marker runs as written — that is <c>/provider list</c>, not
    /// <c>/provider</c>. Any other usage completes to its words before the first argument, plus the
    /// space the argument follows.
    /// </summary>
    private static CommandPaletteEntry ForSlashCommand(SlashCommandDescriptor command)
    {
        bool runsImmediately = command.Usage.IndexOfAny(ArgumentMarkers) < 0;

        return new CommandPaletteEntry(
            command.Usage,
            command.Description,
            CommandPaletteTarget.SlashMenuCommand,
            SlashText: runsImmediately ? command.Usage : null,
            CompletionText: runsImmediately ? null : CompletionFor(command.Usage),
            RunsImmediately: runsImmediately);
    }

    private static string CompletionFor(string usage)
    {
        IEnumerable<string> fixedWords = usage
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(static word => !word.StartsWith('<') && !word.StartsWith('[') && !word.Contains('|'));

        return string.Join(' ', fixedWords).Trim() + " ";
    }

    private static string Needle(CommandPaletteMode mode, string text)
    {
        if (mode == CommandPaletteMode.Actions)
        {
            return text.Trim();
        }

        string name = text.StartsWith('/') ? text[1..] : text;

        int space = name.IndexOf(' ');

        return space < 0 ? name : name[..space];
    }

    private static int TitleCells(CommandPaletteMode mode) =>
        mode == CommandPaletteMode.Slash ? SlashUsageCells : ActionTitleCells;

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="cells"/> display cells on a grapheme
    /// boundary, ending in an ellipsis when anything was dropped. Whitespace left in front of the
    /// ellipsis by the cut is dropped too.
    /// </summary>
    private static string FitToCells(string text, int cells)
    {
        if (TerminalCellMetrics.MeasureWidth(text) <= cells)
        {
            return text;
        }

        if (cells <= 1)
        {
            return cells == 1 ? Ellipsis : string.Empty;
        }

        return TerminalCellMetrics.TruncateToCells(text, cells - 1).TrimEnd() + Ellipsis;
    }

    private static string PadToCells(string text, int cells)
    {
        int width = TerminalCellMetrics.MeasureWidth(text);

        return width >= cells ? text : text + new string(' ', cells - width);
    }
}

/// <summary>
/// The welcome a new session's transcript opens with: the keys an operator needs before they know
/// any, written as one entry so it reads as a block rather than as six separate messages.
/// </summary>
internal static class CommandCenterWelcome
{
    public static IReadOnlyList<string> Lines { get; } =
    [
        "New Session — your first message creates it.",
        "  Enter sends · Ctrl+J adds a new line",
        "  / lists commands · Ctrl+K opens the command palette",
        "  Ctrl+N new session · Ctrl+O past sessions",
        "  Shift+Tab reaches the model control (top right) to change model",
        "  F1 shows every key",
    ];
}
