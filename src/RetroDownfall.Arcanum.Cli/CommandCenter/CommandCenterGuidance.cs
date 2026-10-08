namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// The key guidance Command Center shows outside F1 and <c>/keys</c>: the footer hint list for each
/// focus region and overlay, the focused composer's title and the ask_human footer. It lives in one
/// place so the titles and footers cannot teach different keys from each other, and so a test can pin
/// that none of them teaches Ctrl+Enter to send: Terminal.app, iTerm2's defaults, tmux and xterm.js
/// send the same CR for Ctrl+Enter as for Enter, so Enter is the send key and Ctrl+J, a line feed in
/// every terminal, is the newline key.
/// </summary>
/// <remarks>
/// Every list is in priority order, because <see cref="CommandCenterHintBar.Fit"/> keeps the longest
/// prefix that fits the footer. Every list ends with <see cref="CommandCenterHintBar.HelpHint"/> except
/// where F1 does not apply (Help itself, the confirmations and the ask_human prompt): F1 lists every key,
/// so it stands in for the hints that did not fit.
/// </remarks>
internal static class CommandCenterGuidance
{
    /// <summary>
    /// The focused composer's title. It carries the composer's own two keys, so the footer below it
    /// can spend its one row on where to go next instead of repeating them.
    /// </summary>
    public const string ComposerFocusedTitle = "Composer ●  Enter send · Ctrl+J newline";

    /// <summary>
    /// The footer while an ask_human prompt owns the keyboard. The answer follows the composer's keys,
    /// and Esc does not dismiss the hard modal, so Ctrl+C is the only way out it names.
    /// </summary>
    public const string HumanPromptFooter = "Enter submit · Ctrl+J newline · Ctrl+C cancel turn";

    private const string ModelControlHint = "Shift+Tab model";

    private static readonly IReadOnlyList<string> ComposerHintsWithModel =
    [
        "/ commands",
        "Ctrl+K palette",
        "Ctrl+N new session",
        ModelControlHint,
        "Ctrl+O sessions",
        "Ctrl+C cancel",
        "Ctrl+Q quit",
        CommandCenterHintBar.HelpHint,
    ];

    private static readonly IReadOnlyList<string> ComposerHintsWithoutModel =
        [.. ComposerHintsWithModel.Where(static hint => hint != ModelControlHint)];

    /// <summary>
    /// Sessions, in the sidebar or the Ctrl+O picker. Ctrl+PgDn walks back to older sessions and
    /// Ctrl+PgUp toward recent ones.
    /// </summary>
    public static IReadOnlyList<string> SessionHints { get; } =
    [
        "↑↓ select",
        "Enter resume",
        "type to filter",
        "Esc composer",
        "Ctrl+PgDn/PgUp older/newer",
        "Ctrl+R refresh",
        CommandCenterHintBar.HelpHint,
    ];

    /// <summary>The transcript, where Ctrl+PgUp loads older entries and Ctrl+PgDn returns toward the latest.</summary>
    public static IReadOnlyList<string> TranscriptHints { get; } =
    [
        "↑↓ scroll",
        "PgUp/PgDn page",
        "Home/End",
        "Ctrl+PgUp/PgDn older/newer",
        "Esc composer",
        "Tab next pane",
        CommandCenterHintBar.HelpHint,
    ];

    public static IReadOnlyList<string> IncantationsHints { get; } =
    [
        "↑↓ scroll",
        "PgUp/PgDn page",
        "Home/End",
        "Esc composer",
        "Tab next pane",
        CommandCenterHintBar.HelpHint,
    ];

    /// <summary>The header model control, which opens the model drop-down on Enter.</summary>
    public static IReadOnlyList<string> ModelControlHints { get; } =
    [
        "Enter open model list",
        "Esc composer",
        "Tab next pane",
        "/model <name>",
        CommandCenterHintBar.HelpHint,
    ];

    public static IReadOnlyList<string> ModelPickerHints { get; } =
    [
        "↑↓ select",
        "Enter use model",
        "type to filter",
        "Esc cancel",
        CommandCenterHintBar.HelpHint,
    ];

    /// <summary>The Ctrl+K palette and the slash-command menu, which share the palette overlay.</summary>
    public static IReadOnlyList<string> CommandPaletteHints { get; } =
    [
        "↑↓ select",
        "Enter choose",
        "type to filter",
        "Esc close",
        CommandCenterHintBar.HelpHint,
    ];

    /// <summary>F1 itself, which needs no pointer back to F1.</summary>
    public static IReadOnlyList<string> HelpOverlayHints { get; } =
    [
        "↑↓ scroll",
        "PgUp/PgDn page",
        "Esc close",
    ];

    /// <summary>
    /// The quit and discard confirmations. They name only their two keys, because F1 there would
    /// replace the confirmation with Help.
    /// </summary>
    public static IReadOnlyList<string> ConfirmHints { get; } =
    [
        "Enter confirm",
        "Esc cancel",
    ];

    /// <summary>
    /// The ask_human prompt: <see cref="HumanPromptFooter"/> as a list. The hard modal blocks F1, so it
    /// does not offer it.
    /// </summary>
    public static IReadOnlyList<string> HumanPromptHints { get; } =
    [
        "Enter submit",
        "Ctrl+J newline",
        "Ctrl+C cancel turn",
    ];

    /// <summary>
    /// The composer's hints. They lead with the ways to find everything else: the slash commands, the
    /// palette, a new session and the model control. The model control is only offered when it is on
    /// screen, since a narrow terminal does not render it.
    /// </summary>
    public static IReadOnlyList<string> ComposerHints(bool modelSelectorVisible) =>
        modelSelectorVisible ? ComposerHintsWithModel : ComposerHintsWithoutModel;
}
