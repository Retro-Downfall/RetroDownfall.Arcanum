namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// The key guidance Command Center shows outside F1 and <c>/keys</c>. It lives in one place so the
/// titles and footers cannot teach different keys from each other, and so a test can pin that none of
/// them teaches Ctrl+Enter to send: Terminal.app, iTerm2's defaults, tmux and xterm.js send the same
/// CR for Ctrl+Enter as for Enter, so Enter is the send key and Ctrl+J, a line feed in every terminal,
/// is the newline key.
/// </summary>
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
}
