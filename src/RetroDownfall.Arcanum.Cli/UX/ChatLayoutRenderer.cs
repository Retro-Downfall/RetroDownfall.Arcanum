using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Mcp;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Full-screen chat layout: a compact header, a streaming transcript/diagnostics
/// body, and a status sidebar (MCP servers, model, server health). Pure rendering —
/// callers own the live-refresh loop (e.g. via <see cref="LiveDisplay"/>) and call
/// <see cref="Build"/> on each frame with an updated <see cref="ChatLayoutContext"/>.
/// </summary>
internal static class ChatLayoutRenderer
{

    private const int MaxLiveDiagnosticLines = 6;

    private const int SidebarWidth = 30;

    internal static Layout Build(ChatLayoutContext ctx)
    {

        ArgumentNullException.ThrowIfNull(ctx);

        Layout header = new Layout("header").Update(new Markup(ctx.HeaderMarkup)).Size(1);

        Layout body = new Layout("body").Ratio(3).SplitRows(
            new Layout("assistant").Update(BuildAssistantRenderable(ctx)),
            new Layout("diagnostics")
                .Update(BuildDiagnosticsRenderable(ctx))
                .Size(MaxLiveDiagnosticLines + 2));

        Layout sidebar = new Layout("sidebar").Size(SidebarWidth).SplitRows(
            new Layout("mcp").Update(McpReadoutPanel.Render(ctx.McpServers, ctx.Theme)),
            new Layout("model").Update(BuildModelPanel(ctx)),
            new Layout("server").Update(BuildServerPanel(ctx)));

        Layout main = new Layout("main").SplitColumns(body, sidebar);

        return new Layout("root").SplitRows(header, main);

    }

    private static IRenderable BuildAssistantRenderable(ChatLayoutContext ctx)
    {

        List<IRenderable> rows = new(ctx.TranscriptTail);

        string assistantText = string.IsNullOrEmpty(ctx.AssistantText)
            ? ctx.Theme.MutedMarkup(Markup.Escape(ctx.Generating ? "(thinking…)" : "(no response yet)"))
            : Markup.Escape(ctx.AssistantText);

        rows.Add(new Markup(assistantText));

        Panel panel = new(new Rows(rows))
        {
            Header = new PanelHeader(ctx.Theme.HeadingBoldMarkup(Markup.Escape(ctx.Generating ? "Assistant (generating…)" : "Assistant"))),
            Border = BoxBorder.Rounded,
            BorderStyle = ctx.Theme.HeadingStyle(),
            Padding = new Padding(1, 0, 1, 0),
        };

        return panel;

    }

    private static IRenderable BuildDiagnosticsRenderable(ChatLayoutContext ctx)
    {

        IEnumerable<ToolDiagnosticLine> tail = ctx.LiveDiagnostics.Count > MaxLiveDiagnosticLines
            ? ctx.LiveDiagnostics.Skip(ctx.LiveDiagnostics.Count - MaxLiveDiagnosticLines)
            : ctx.LiveDiagnostics;

        List<IRenderable> lines = new();

        foreach (ToolDiagnosticLine line in tail)
        {
            string glyph = line.Outcome == ToolDiagnosticOutcome.Succeeded ? "[green]✓[/]" : "[red]✗[/]";

            string body = line.Outcome == ToolDiagnosticOutcome.Succeeded
                ? ctx.Theme.MutedMarkup(Markup.Escape(line.TruncatedPreview))
                : ctx.Theme.ErrorMarkup(Markup.Escape(line.TruncatedPreview));

            lines.Add(new Markup($"{glyph} {ctx.Theme.TextMarkup(Markup.Escape(line.ToolName))} — {body}"));
        }

        if (lines.Count == 0)
        {
            lines.Add(new Markup(ctx.Theme.MutedMarkup(Markup.Escape("(no tool activity)"))));
        }

        return new Panel(new Rows(lines))
        {
            Header = new PanelHeader(ctx.Theme.HeadingBoldMarkup(Markup.Escape("Tool activity"))),
            Border = BoxBorder.Rounded,
            BorderStyle = ctx.Theme.MutedStyle(),
            Padding = new Padding(1, 0, 1, 0),
        };

    }

    private static IRenderable BuildModelPanel(ChatLayoutContext ctx)
    {

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddRow(
            ctx.Theme.MutedMarkup(Markup.Escape("Model:")),
            ctx.Theme.HighlightMarkup(Markup.Escape(ctx.Model ?? "(none)")));

        table.AddRow(
            ctx.Theme.MutedMarkup(Markup.Escape("Mana:")),
            ctx.Theme.TextMarkup(Markup.Escape(ctx.ManaSummary ?? "(untracked)")));

        return new Panel(table)
        {
            Header = new PanelHeader(ctx.Theme.HeadingBoldMarkup(Markup.Escape("Model"))),
            Border = BoxBorder.Rounded,
            BorderStyle = ctx.Theme.MutedStyle(),
            Padding = new Padding(1, 0, 1, 0),
        };

    }

    private static IRenderable BuildServerPanel(ChatLayoutContext ctx)
    {

        string statusMarkup = ctx.ServerStatus switch
        {
            ServeLaunchStatus.AlreadyRunning => "[green]●[/] " + ctx.Theme.HighlightMarkup(Markup.Escape("running")),
            ServeLaunchStatus.Started => "[yellow]●[/] " + ctx.Theme.HighlightMarkup(Markup.Escape("auto-started")),
            ServeLaunchStatus.AuthFailed => "[red]●[/] " + ctx.Theme.ErrorMarkup(Markup.Escape("auth failed")),
            ServeLaunchStatus.LaunchDisabled =>
                $"[{ctx.Theme.Muted.ToMarkup()}]●[/] " + ctx.Theme.MutedMarkup(Markup.Escape("disabled")),
            ServeLaunchStatus.Failed => "[red]●[/] " + ctx.Theme.ErrorMarkup(Markup.Escape("unreachable")),
            _ => $"[{ctx.Theme.Muted.ToMarkup()}]●[/] " + ctx.Theme.MutedMarkup(Markup.Escape("unknown")),
        };

        return new Panel(new Markup(statusMarkup))
        {
            Header = new PanelHeader(ctx.Theme.HeadingBoldMarkup(Markup.Escape("Server"))),
            Border = BoxBorder.Rounded,
            BorderStyle = ctx.Theme.MutedStyle(),
            Padding = new Padding(1, 0, 1, 0),
        };

    }

}

/// <summary>
/// Per-frame snapshot fed to <see cref="ChatLayoutRenderer.Build"/>. Callers refresh
/// this on each token/tool event and re-render into a <see cref="LiveDisplay"/>.
/// </summary>
internal sealed record ChatLayoutContext(
    string HeaderMarkup,
    string AssistantText,
    IReadOnlyList<ToolDiagnosticLine> LiveDiagnostics,
    IReadOnlyList<IRenderable> TranscriptTail,
    IReadOnlyList<McpServerInfo> McpServers,
    string? Model,
    string? ManaSummary,
    ServeLaunchStatus ServerStatus,
    IThemePalette Theme,
    bool Generating);
