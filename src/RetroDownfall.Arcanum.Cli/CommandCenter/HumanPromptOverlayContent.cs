namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>HumanPrompt hard-modal copy — hints stay on their own lines.</summary>
internal static class HumanPromptOverlayContent
{
    public static readonly string[] HintLines =
    [
        string.Empty,
        "Ctrl+Enter = submit answer",
        "Enter = newline",
        "Ctrl+C = cancel turn",
    ];

    public const int AnswerViewportRows = 5;
}
