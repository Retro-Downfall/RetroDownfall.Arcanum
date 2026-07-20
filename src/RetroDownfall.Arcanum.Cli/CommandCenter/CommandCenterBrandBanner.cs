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

    public const int RowCount = 3;

    /// <summary>Brand art rows + rights blurb line.</summary>
    public const int BrandedContentRows = RowCount + 1;

    public static int Width { get; } = Lines.Max(static l => l.Length);

    public static string AsText() => string.Join('\n', Lines);

    /// <summary>True when the viewport is wide/tall enough to show the brand mark.</summary>
    public static bool Fits(int cols, int rows) =>
        cols >= Width + 6 && rows >= 20;
}
