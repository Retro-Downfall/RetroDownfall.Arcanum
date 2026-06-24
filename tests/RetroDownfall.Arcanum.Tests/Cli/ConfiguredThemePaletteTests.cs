using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ConfiguredThemePaletteTests
{

    [Fact]
    public void Constructor_parses_valid_hex_colors()
    {

        ThemeSemanticColors semantic = new()
        {
            Text = "#112233",
            Heading = "#AABBCC",
            Highlight = "#00FF00",
            Error = "#FF0000",
            Muted = "#808080",
        };

        ThemeSemanticColors fallback = new();

        ConfiguredThemePalette palette = new(semantic, fallback);

        Assert.Equal(new Color(0x11, 0x22, 0x33), palette.Text);

        Assert.Equal(new Color(0xAA, 0xBB, 0xCC), palette.Heading);

        Assert.Equal(new Color(0x00, 0xFF, 0x00), palette.Highlight);

        Assert.Equal(new Color(0xFF, 0x00, 0x00), palette.Error);

        Assert.Equal(new Color(0x80, 0x80, 0x80), palette.Muted);

    }

    [Fact]
    public void Constructor_accepts_hash_prefixed_hex()
    {

        ThemeSemanticColors semantic = new() { Text = "#ABCDEF" };

        ThemeSemanticColors fallback = new();

        ConfiguredThemePalette palette = new(semantic, fallback);

        Assert.Equal(new Color(0xAB, 0xCD, 0xEF), palette.Text);

    }

    [Fact]
    public void Constructor_falls_back_when_semantic_hex_is_invalid()
    {

        ThemeSemanticColors semantic = new() { Text = "not-a-color" };

        ThemeSemanticColors fallback = new() { Text = "#010203" };

        ConfiguredThemePalette palette = new(semantic, fallback);

        Assert.Equal(new Color(0x01, 0x02, 0x03), palette.Text);

    }

    [Fact]
    public void Constructor_uses_color_default_when_both_semantic_and_fallback_are_invalid()
    {

        ThemeSemanticColors semantic = new() { Text = "bad" };

        ThemeSemanticColors fallback = new() { Text = "also-bad" };

        ConfiguredThemePalette palette = new(semantic, fallback);

        Assert.Equal(Color.Default, palette.Text);

    }

    [Fact]
    public void Constructor_rejects_short_hex_strings()
    {

        ThemeSemanticColors semantic = new() { Heading = "#ABC" };

        ThemeSemanticColors fallback = new() { Heading = "#445566" };

        ConfiguredThemePalette palette = new(semantic, fallback);

        Assert.Equal(new Color(0x44, 0x55, 0x66), palette.Heading);

    }

}
