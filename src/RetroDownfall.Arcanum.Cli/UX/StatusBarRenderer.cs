using RetroDownfall.Arcanum.Cli.Services;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Single-line status readout rendered above the prompt between chat turns —
/// a compact echo of the startup banner once the session is underway.
/// </summary>
internal static class StatusBarRenderer
{

    internal static string RenderCompact(
        string? model,
        int mcpRunning,
        int mcpTotal,
        bool mcpUnavailable,
        ServeLaunchStatus status,
        IThemePalette theme)
    {

        ArgumentNullException.ThrowIfNull(theme);

        List<string> segments = new();

        segments.Add($"{theme.MutedMarkup(Markup.Escape("Server:"))} {FormatServerSegment(status, theme)}");

        segments.Add(
            $"{theme.MutedMarkup(Markup.Escape("Model:"))} {theme.HighlightMarkup(Markup.Escape(model ?? "(none)"))}");

        segments.Add($"{theme.MutedMarkup(Markup.Escape("MCP:"))} {FormatMcpSegment(mcpRunning, mcpTotal, mcpUnavailable, theme)}");

        return string.Join(theme.MutedMarkup(Markup.Escape("  |  ")), segments);

    }

    private static string FormatMcpSegment(int running, int total, bool unavailable, IThemePalette theme)
    {

        if (unavailable)
        {
            return theme.MutedMarkup(Markup.Escape("unavailable"));
        }

        return theme.HighlightMarkup(Markup.Escape($"{running}/{total}"));

    }

    private static string FormatServerSegment(ServeLaunchStatus status, IThemePalette theme) => status switch
    {
        ServeLaunchStatus.AlreadyRunning => "[green]●[/] " + theme.HighlightMarkup(Markup.Escape("running")),
        ServeLaunchStatus.Started => "[yellow]●[/] " + theme.HighlightMarkup(Markup.Escape("auto-started")),
        ServeLaunchStatus.AuthFailed => "[red]●[/] " + theme.ErrorMarkup(Markup.Escape("auth failed")),
        ServeLaunchStatus.LaunchDisabled => $"[{theme.Muted.ToMarkup()}]●[/] " + theme.MutedMarkup(Markup.Escape("disabled")),
        ServeLaunchStatus.Failed => "[red]●[/] " + theme.ErrorMarkup(Markup.Escape("unreachable")),
        _ => $"[{theme.Muted.ToMarkup()}]●[/] " + theme.MutedMarkup(Markup.Escape("unknown")),
    };

}
