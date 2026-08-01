using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Non-focusable telemetry pane displaying real-time session aggregates
/// from the TelemetryService / IModelCallExecutor event stream.
/// </summary>
public sealed class TelemetryPane
{
    public bool CanFocus => false;

    public object? Width => null;

    public int Height => 10;

    public string Text { get; private set; } = "Context telemetry will appear after the first model call.";

    public TelemetryPane() { }

    public void UpdateMetrics(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Debounced rendering handled by host
    }

    /// <summary>
    /// Formats one immutable provider-call breakdown. Rendering is a single string replacement so
    /// token deltas never rebuild the surrounding Terminal.Gui view tree.
    /// </summary>
    public void UpdateContext(ContextTokenBreakdown? breakdown)
    {
        if (breakdown is null)
        {
            Text = "Context telemetry will appear after the first model call.";

            return;
        }

        (long displayedInput, string authority) = ResolveDisplayedInput(breakdown);

        List<string> lines =
        [
            Row("Chat history", breakdown.HistoryTokens),

            Row("Attachments", breakdown.ExplicitAttachmentTokens),

            Row("Refreshed", breakdown.RefreshedFileTokens),

            Row("Attachment RAG", breakdown.AttachmentRagTokens),

            Row("Workspace RAG", breakdown.WorkspaceRagTokens),

            $"Input {displayedInput:N0} {authority}",
        ];

        if (breakdown.HasSemanticRagPressure)
        {
            lines.Add(
                $"! RAG dropped {breakdown.DroppedSemanticRagChunks:N0} / "
                + $"{breakdown.DroppedSemanticRagTokens:N0} tokens");
        }

        Text = string.Join(Environment.NewLine, lines);
    }

    private static (long Tokens, string Authority) ResolveDisplayedInput(
        ContextTokenBreakdown breakdown) =>
        breakdown is
        {
            ProviderReportedInputValid: true,
            ProviderReportedInputTokens: >= 0 and var reported,
        }
            ? (reported, "billed")
            : (Math.Max(0, breakdown.InputTokens), "estimated");

    private static string Row(string label, int tokens) =>
        $"{label,-14} {Math.Max(0, tokens),8:N0}";
}
