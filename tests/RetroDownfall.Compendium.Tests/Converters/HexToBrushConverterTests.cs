using System.Globalization;

using Avalonia.Data;

using Avalonia.Media;

using RetroDownfall.Compendium.Ux.Converters;

using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Converters;

public sealed class HexToBrushConverterTests
{

    [Fact]

    public void Convert_parses_rgb_hex_to_brush()
    {

        HexToBrushConverter converter = new();

        object? result = converter.Convert("#00ff84", typeof(IBrush), null, CultureInfo.InvariantCulture);

        SolidColorBrush brush = Assert.IsType<SolidColorBrush>(result);

        Assert.Equal(0, brush.Color.R);

        Assert.Equal(255, brush.Color.G);

        Assert.Equal(132, brush.Color.B);

    }

    [Theory]

    [InlineData("")]

    [InlineData("not-a-color")]

    public void Convert_returns_transparent_for_invalid_input(string hex)
    {

        HexToBrushConverter converter = new();

        object? result = converter.Convert(hex, typeof(IBrush), null, CultureInfo.InvariantCulture);

        Assert.Equal(Brushes.Transparent, result);

    }

    [Fact]

    public void Convert_returns_transparent_for_null()
    {

        HexToBrushConverter converter = new();

        object? result = converter.Convert(null, typeof(IBrush), null, CultureInfo.InvariantCulture);

        Assert.Equal(Brushes.Transparent, result);

    }

    [Fact]

    public void ConvertBack_returns_UnsetValue()
    {

        HexToBrushConverter converter = new();

        Assert.Equal(Avalonia.AvaloniaProperty.UnsetValue, converter.ConvertBack(Brushes.Red, typeof(string), null!, null!));

    }

}
