using System.Globalization;
using RetroDownfall.Arcanum.Core.Configuration;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

public sealed class ConfiguredThemePalette : IThemePalette
{

    public ConfiguredThemePalette(ThemeSemanticColors semantic, ThemeSemanticColors roleFallback)
    {
        Text = ParseColor(semantic.Text, roleFallback.Text);

        Heading = ParseColor(semantic.Heading, roleFallback.Heading);

        Highlight = ParseColor(semantic.Highlight, roleFallback.Highlight);

        Error = ParseColor(semantic.Error, roleFallback.Error);

        Muted = ParseColor(semantic.Muted, roleFallback.Muted);
    }

    public Color Text { get; }

    public Color Heading { get; }

    public Color Highlight { get; }

    public Color Error { get; }

    public Color Muted { get; }

    private static Color ParseColor(string? hex, string fallbackHex)
    {
        if (TryParseHexRgb(hex ?? string.Empty, out byte r, out byte g, out byte b))
        {
            return new Color(r, g, b);
        }

        if (TryParseHexRgb(fallbackHex, out r, out g, out b))
        {
            return new Color(r, g, b);
        }

        return Color.Default;
    }

    private static bool TryParseHexRgb(ReadOnlySpan<char> raw, out byte r, out byte g, out byte b)
    {
        r = 0;

        g = 0;

        b = 0;

        ReadOnlySpan<char> s = raw.Trim();

        if (s.StartsWith("#", StringComparison.Ordinal))
        {
            s = s[1..];
        }

        if (s.Length != 6)
        {
            return false;
        }

        if (!byte.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r))
        {
            return false;
        }

        if (!byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g))
        {
            return false;
        }

        if (!byte.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
        {
            return false;
        }

        return true;
    }

}
