namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Ward confirm modal copy — each choice is its own line (never joined). Only keys the modal
/// itself handles may appear here: while it is displayed it swallows every non-Ctrl, non-Alt,
/// non-Tab key, so a slash command listed as a choice cannot be typed at all — and the first
/// letter that does reach a binding decides the Forbidden Art on the operator's behalf.
/// </summary>
internal static class WardOverlayContent
{
    public static readonly string[] ChoiceLines =
    [
        string.Empty,
        "Enter / A = always allow this tool (this session)",
        "O = allow once",
        "Esc / D = deny",
    ];
}
