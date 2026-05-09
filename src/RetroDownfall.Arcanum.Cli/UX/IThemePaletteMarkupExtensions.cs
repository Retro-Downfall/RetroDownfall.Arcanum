using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

public static class IThemePaletteMarkupExtensions
{

    public static Style TextStyle(this IThemePalette palette) => new(foreground: palette.Text);

    public static Style HeadingStyle(this IThemePalette palette) => new(foreground: palette.Heading, decoration: Decoration.Bold);

    public static Style HighlightStyle(this IThemePalette palette) => new(foreground: palette.Highlight);

    public static Style ErrorStyle(this IThemePalette palette) => new(foreground: palette.Error);

    public static Style MutedStyle(this IThemePalette palette) => new(foreground: palette.Muted, decoration: Decoration.Dim);

    public static string TextMarkup(this IThemePalette palette, string escapedContent) =>
        $"[{palette.Text.ToMarkup()}]{escapedContent}[/]";

    public static string HeadingBoldMarkup(this IThemePalette palette, string escapedContent) =>
        $"[bold {palette.Heading.ToMarkup()}]{escapedContent}[/]";

    public static string HighlightMarkup(this IThemePalette palette, string escapedContent) =>
        $"[{palette.Highlight.ToMarkup()}]{escapedContent}[/]";

    public static string ErrorMarkup(this IThemePalette palette, string escapedContent) =>
        $"[{palette.Error.ToMarkup()}]{escapedContent}[/]";

    public static string MutedMarkup(this IThemePalette palette, string escapedContent) =>
        $"[dim {palette.Muted.ToMarkup()}]{escapedContent}[/]";

    public static string MutedLabelMarkup(this IThemePalette palette, string escapedLabel, string escapedBody) =>
        $"[dim {palette.Muted.ToMarkup()}]{escapedLabel}[/] [{palette.Text.ToMarkup()}]{escapedBody}[/]";

    public static string ErrorLabelMarkup(this IThemePalette palette, string escapedLabel, string escapedBody) =>
        $"[{palette.Error.ToMarkup()}]{escapedLabel}[/] {palette.TextMarkup(escapedBody)}";

    public static string HighlightLabelMarkup(this IThemePalette palette, string escapedLabel, string escapedBody) =>
        $"[{palette.Highlight.ToMarkup()}]{escapedLabel}[/] {palette.TextMarkup(escapedBody)}";

    public static string HeadingTableColumn(this IThemePalette palette, string escapedHeader) =>
        $"[bold {palette.Heading.ToMarkup()}]{escapedHeader}[/]";

}
