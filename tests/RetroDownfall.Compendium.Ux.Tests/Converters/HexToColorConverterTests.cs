using Microsoft.Maui.Graphics;
using RetroDownfall.Compendium.Ux.Converters;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Converters;

public sealed class HexToColorConverterTests
{

    [Theory]

    [InlineData("#000", 0, 0, 0)]

    [InlineData("#fff", 1, 1, 1)]

    [InlineData("#000000", 0, 0, 0)]

    [InlineData("#ffffff", 1, 1, 1)]

    [InlineData("#00ff84", 0, 1, 0.5176471)]

    [InlineData("00ff84", 0, 1, 0.5176471)]

    public void Convert_parses_rgb_hex_to_color(string hex, double r, double g, double b)
    {

        HexToColorConverter converter = new();

        object result = converter.Convert(hex, null, null, null);

        Assert.IsType<Color>(result);

        Color color = (Color)result;

        Assert.Equal(r, color.Red, 4);

        Assert.Equal(g, color.Green, 4);

        Assert.Equal(b, color.Blue, 4);

    }

    [Fact]

    public void Convert_parses_argb_hex_to_color()
    {

        HexToColorConverter converter = new();

        object result = converter.Convert("#80FF0000", null, null, null);

        Assert.IsType<Color>(result);

        Color color = (Color)result;

        Assert.Equal(1, color.Red, 4);

        Assert.Equal(0, color.Green, 4);

        Assert.Equal(0, color.Blue, 4);

        Assert.Equal(0.5019608, color.Alpha, 4);

    }

    [Theory]

    [InlineData("")]

    [InlineData("   ")]

    [InlineData("not-a-color")]

    [InlineData("#ZZZ")]

    [InlineData("#12")]

    [InlineData("#12345")]

    public void Convert_returns_transparent_for_invalid_input(string hex)
    {

        HexToColorConverter converter = new();

        object result = converter.Convert(hex, null, null, null);

        Assert.Equal(Colors.Transparent, result);

    }

    [Fact]

    public void Convert_returns_transparent_for_null()
    {

        HexToColorConverter converter = new();

        object result = converter.Convert(null, null, null, null);

        Assert.Equal(Colors.Transparent, result);

    }

    [Fact]

    public void ConvertBack_returns_DoNothing()
    {

        HexToColorConverter converter = new();

        Assert.Equal(Binding.DoNothing, converter.ConvertBack(Colors.Red, null, null, null));

    }

}
