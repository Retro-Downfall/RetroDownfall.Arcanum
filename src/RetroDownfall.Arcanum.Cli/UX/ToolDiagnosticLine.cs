namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// One live tool-call diagnostic line surfaced while a chat turn is generating —
/// e.g. "spell_search: ok — 3 results" rendered under the streaming assistant text.
/// </summary>
internal sealed record ToolDiagnosticLine(string ToolName, ToolDiagnosticOutcome Outcome, string TruncatedPreview)
{

    private const int MaxPreviewLength = 80;

    internal static ToolDiagnosticLine Create(string toolName, ToolDiagnosticOutcome outcome, string preview) =>
        new(toolName, outcome, Truncate(preview));

    internal static string Truncate(string preview)
    {

        ArgumentNullException.ThrowIfNull(preview);

        string collapsed = preview.ReplaceLineEndings(" ");

        if (collapsed.Length <= MaxPreviewLength)
        {
            return collapsed;
        }

        return string.Concat(collapsed.AsSpan(0, MaxPreviewLength - 1), "…");

    }

}

internal enum ToolDiagnosticOutcome
{

    Succeeded,

    Failed,

}
