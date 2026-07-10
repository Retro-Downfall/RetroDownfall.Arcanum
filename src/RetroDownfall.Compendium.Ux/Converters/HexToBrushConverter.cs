using System.Globalization;

using Avalonia.Data.Converters;

using Avalonia.Media;

namespace RetroDownfall.Compendium.Ux.Converters;

public sealed class HexToBrushConverter : IValueConverter
{

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {

        if (value is not string hex || string.IsNullOrWhiteSpace(hex))

        {

            return Brushes.Transparent;

        }

        if (HexColorParser.TryParse(hex, out byte a, out byte r, out byte g, out byte b))

        {

            return new SolidColorBrush(Color.FromArgb(a, r, g, b));

        }

        return Brushes.Transparent;

    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {

        return Avalonia.AvaloniaProperty.UnsetValue;

    }

}
