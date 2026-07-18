using System.Globalization;
using RetroDownfall.Arcanum.Cli.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Startup banner for interactive CLI sessions (<c>arcanum chat</c> et al.). Pure
/// rendering — callers gather state (serve launch result, health probe, MCP counts,
/// inference overrides) into a <see cref="BannerContext"/> and hand it here.
/// </summary>
internal static class ArcanumBannerRenderer
{

    private const string TipText = "/help for slash commands  -  /exit to quit  -  Ctrl+C to cancel a turn";

    internal static IRenderable Render(BannerContext ctx)
    {

        ArgumentNullException.ThrowIfNull(ctx);

        FigletText title = new FigletText(FigletFont.Default, "ARCANUM").Color(ctx.Theme.Heading).Centered();

        Markup subtitle = new(ctx.Theme.MutedMarkup(Markup.Escape($"the conversational grimoire  -  v{ctx.Version}")));

        Table table = BuildDetailsTable(ctx);

        Rows content = new(title, subtitle, table);

        return new Panel(content)
        {
            Header = new PanelHeader(ctx.Theme.HeadingBoldMarkup(Markup.Escape("Arcanum"))),
            Border = BoxBorder.Heavy,
            BorderStyle = ctx.Theme.HeadingStyle(),
            Padding = new Padding(1, 0, 1, 0),
        };

    }

    private static Table BuildDetailsTable(BannerContext ctx)
    {

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddRow(
            ctx.Theme.MutedMarkup(Markup.Escape("Server:")),
            FormatServerStatus(ctx));

        table.AddRow(
            ctx.Theme.MutedMarkup(Markup.Escape("Model:")),
            ctx.Theme.HighlightMarkup(Markup.Escape(ctx.Model ?? "(first configured)")));

        table.AddRow(
            ctx.Theme.MutedMarkup(Markup.Escape("MCP:")),
            FormatMcpSummary(ctx));

        if (ctx.CampaignId is { } campaignId)
        {
            table.AddRow(
                ctx.Theme.MutedMarkup(Markup.Escape("Campaign:")),
                ctx.Theme.HighlightMarkup(Markup.Escape(campaignId.ToString("D"))));
        }

        if (ctx.Unattended)
        {
            table.AddRow(
                ctx.Theme.MutedMarkup(Markup.Escape("Mode:")),
                ctx.Theme.HighlightMarkup(Markup.Escape("unattended (ask_human auto-replies)")));
        }

        if (ctx.ToolsDisabled)
        {
            table.AddRow(
                ctx.Theme.MutedMarkup(Markup.Escape("Tools:")),
                ctx.Theme.TextMarkup(Markup.Escape("disabled (--no-tools)")));
        }

        if (ctx.InferenceOverrides.Count > 0)
        {
            table.AddRow(
                ctx.Theme.MutedMarkup(Markup.Escape("Inference:")),
                ctx.Theme.TextMarkup(Markup.Escape(string.Join("  ", ctx.InferenceOverrides))));
        }

        table.AddRow(
            ctx.Theme.MutedMarkup(Markup.Escape("Tip:")),
            ctx.Theme.MutedMarkup(Markup.Escape(TipText)));

        return table;

    }

    private static string FormatMcpSummary(BannerContext ctx)
    {

        if (ctx.McpUnavailable)
        {
            return ctx.Theme.MutedMarkup(Markup.Escape("unavailable"));
        }

        string counts = string.Create(
            CultureInfo.InvariantCulture,
            $"{ctx.McpRunning}/{ctx.McpTotal} running");

        return ctx.Theme.HighlightMarkup(Markup.Escape(counts));

    }

    private static string FormatServerStatus(BannerContext ctx)
    {

        return ctx.Status switch
        {
            ServeLaunchStatus.AlreadyRunning =>
                FormatDot("green", ctx.Theme.HighlightMarkup(Markup.Escape($"running — {ctx.BaseUrl}"))),

            ServeLaunchStatus.Started =>
                FormatDot("yellow", ctx.Theme.HighlightMarkup(Markup.Escape($"auto-started — {ctx.BaseUrl}"))),

            ServeLaunchStatus.AuthFailed =>
                FormatDot("red", ctx.Theme.ErrorMarkup(Markup.Escape("auth failed — run arcanum key show"))),

            ServeLaunchStatus.LaunchDisabled =>
                FormatDot(ctx.Theme.Muted.ToMarkup(), ctx.Theme.MutedMarkup(Markup.Escape("auto-start disabled"))),

            ServeLaunchStatus.Failed => FormatFailedStatus(ctx),

            _ => FormatDot(ctx.Theme.Muted.ToMarkup(), ctx.Theme.MutedMarkup(Markup.Escape("unknown"))),
        };

    }

    private static string FormatFailedStatus(BannerContext ctx)
    {

        string message = ctx.Health switch
        {
            HealthProbeState.TlsFailure => "TLS/cert problem — run arcanum doctor",
            HealthProbeState.Timeout => "probe timed out — run arcanum doctor",
            _ => "unreachable (see log path hint)",
        };

        return FormatDot("red", ctx.Theme.ErrorMarkup(Markup.Escape(message)));

    }

    private static string FormatDot(string colorMarkup, string labelMarkup) => $"[{colorMarkup}]●[/] {labelMarkup}";

}

/// <summary>
/// Immutable input to <see cref="ArcanumBannerRenderer.Render"/>. Gathered once at
/// session start from the serve launcher result, health probe, session config, and
/// MCP connection manager counts.
/// </summary>
internal sealed record BannerContext(
    ServeLaunchStatus Status,
    HealthProbeState Health,
    string BaseUrl,
    string? Model,
    Guid? CampaignId,
    bool Unattended,
    bool ToolsDisabled,
    IReadOnlyList<string> InferenceOverrides,
    int McpRunning,
    int McpTotal,
    bool McpUnavailable,
    string Version,
    IThemePalette Theme);
