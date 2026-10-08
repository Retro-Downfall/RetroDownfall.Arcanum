namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Compact square-block ASCII brand mark for the Command Center header.
/// Pure ASCII / box-drawing so it survives typical terminals and AOT.
/// </summary>
internal static class CommandCenterBrandBanner
{
    /// <summary>3-row square letterforms for ARCANUM (~35 cols).</summary>
    public static readonly string[] Lines =
    [
        "▄▀█ █▀█ █▀▀ ▄▀█ █▄ █ █ █ █▀▄▀█",
        "█▀█ █▀▄ █   █▀█ █ ▀█ █ █ █ ▀ █",
        "▀ █ ▀ ▀ ▀▀▀ ▀ █ ▀  ▀ ▀▀▀ ▀   ▀",
    ];

    public static string RightsBlurb =>
        $"All rights reserved by Retro Downfall, {DateTime.UtcNow.Year}.";

    /// <summary>
    /// The brand in the header's border title, where it costs no row of its own: what the header shows
    /// instead of the logo and the rights line when the terminal is too short for them.
    /// </summary>
    public static string CompactTitle => $"Arcanum · {RightsBlurb}";

    public const int RowCount = 3;

    /// <summary>Brand art rows + rights blurb line.</summary>
    public const int BrandedContentRows = RowCount + 1;

    /// <summary>
    /// The shortest terminal that gets the logo. The logo and the rights line cost four rows; at 80x24
    /// that left the transcript seven rows of text, so a shorter terminal spends them on the transcript
    /// and carries the brand in the header's border title instead (<see cref="CompactTitle"/>).
    /// </summary>
    public const int MinRowsForBanner = 30;

    public static int Width { get; } = Lines.Max(static l => l.Length);

    public static string AsText() => string.Join('\n', Lines);

    /// <summary>True when the viewport is wide/tall enough to show the brand mark.</summary>
    public static bool Fits(int cols, int rows) =>
        cols >= Width + 6 && rows >= MinRowsForBanner;
}
