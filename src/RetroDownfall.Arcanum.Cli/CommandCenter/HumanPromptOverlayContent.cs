namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>HumanPrompt hard-modal copy — hints stay on their own lines.</summary>
internal static class HumanPromptOverlayContent
{
    /// <summary>
    /// The answer follows the composer's keys: Enter submits, and Ctrl+J, a line feed in every terminal,
    /// inserts a line break. Alt+Enter and Shift+Enter also do where the terminal sends them distinctly.
    /// </summary>
    public static readonly string[] HintLines =
    [
        string.Empty,
        "Enter = submit answer",
        "Ctrl+J = new line (also Alt+Enter, Shift+Enter)",
        "Ctrl+C = cancel turn",
    ];

    public const int AnswerViewportRows = 5;
}
