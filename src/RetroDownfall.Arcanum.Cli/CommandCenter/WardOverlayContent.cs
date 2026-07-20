namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>Ward confirm modal copy — each choice is its own line (never joined).</summary>
internal static class WardOverlayContent
{
    public static readonly string[] ChoiceLines =
    [
        string.Empty,
        "Enter / A = always allow this tool (this session)",
        "O = allow once",
        "Esc / D = deny",
        "/ward allow",
        "/ward deny",
    ];
}
