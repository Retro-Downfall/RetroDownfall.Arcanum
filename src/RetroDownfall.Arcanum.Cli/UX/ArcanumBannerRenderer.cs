using System.Globalization;
using System.Text;
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

    /// <summary>
    /// Spectre default-font Figlet for <c>ARCANUM</c> (fixed glyph so we can paint a column gradient).
    /// </summary>
    private static readonly string[] FigletArcanumLines =
    [
        @"     _      ____     ____      _      _   _   _   _   __  __ ",
        @"    / \    |  _ \   / ___|    / \    | \ | | | | | | |  \/  |",
        @"   / _ \   | |_) | | |       / _ \   |  \| | | | | | | |\/| |",
        @"  / ___ \  |  _ <  | |___   / ___ \  | |\  | | |_| | | |  | |",
        @" /_/   \_\ |_| \_\  \____| /_/   \_\ |_| \_|  \___/  |_|  |_|",
    ];

    internal static IRenderable Render(BannerContext ctx)
    {

        ArgumentNullException.ThrowIfNull(ctx);

        Markup title = new(BuildGradientFigletMarkup(ctx.Theme.Heading));

        IRenderable centeredTitle = Align.Center(title);

        Markup subtitle = new(ctx.Theme.MutedMarkup(Markup.Escape($"the conversational grimoire  -  v{ctx.Version}")));

        Table table = BuildDetailsTable(ctx);

        Rows content = new(centeredTitle, subtitle, table);

        return new Panel(content)
        {
            Header = new PanelHeader(ctx.Theme.HeadingBoldMarkup(Markup.Escape("Arcanum"))),
            Border = BoxBorder.Heavy,
            BorderStyle = ctx.Theme.HeadingStyle(),
            Padding = new Padding(1, 0, 1, 0),
        };

    }

    /// <summary>
    /// Paints Figlet glyphs with a left→right color gradient from <paramref name="start"/>
    /// toward a lighter blend of the same hue.
    /// </summary>
    internal static string BuildGradientFigletMarkup(Color start)
    {

        Color end = Lighten(start, amount: 0.55);

        int width = FigletArcanumLines.Max(static l => l.Length);

        StringBuilder sb = new(FigletArcanumLines.Length * (width * 16));

        for (int row = 0; row < FigletArcanumLines.Length; row++)
        {

            string line = FigletArcanumLines[row].PadRight(width);

            for (int col = 0; col < width; col++)
            {

                char ch = line[col];

                if (ch == ' ')
                {
                    sb.Append(' ');
                    continue;
                }

                double t = width <= 1 ? 0.0 : col / (double)(width - 1);

                Color color = Lerp(start, end, t);

                sb.Append('[');
                sb.Append(color.ToMarkup());
                sb.Append(']');
                sb.Append(Markup.Escape(ch.ToString()));
                sb.Append("[/]");

            }

            if (row < FigletArcanumLines.Length - 1)
            {
                sb.Append('\n');
            }

        }

        return sb.ToString();

    }

    private static Color Lighten(Color color, double amount)
    {

        amount = Math.Clamp(amount, 0.0, 1.0);

        return Lerp(color, Color.White, amount);

    }

    private static Color Lerp(Color a, Color b, double t)
    {

        t = Math.Clamp(t, 0.0, 1.0);

        return new Color(
            LerpByte(a.R, b.R, t),
            LerpByte(a.G, b.G, t),
            LerpByte(a.B, b.B, t));

    }

    private static byte LerpByte(byte a, byte b, double t) =>
        (byte)Math.Round(a + ((b - a) * t), MidpointRounding.AwayFromZero);

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
