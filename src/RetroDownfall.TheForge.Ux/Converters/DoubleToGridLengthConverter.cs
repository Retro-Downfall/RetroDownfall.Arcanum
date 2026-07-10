using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace RetroDownfall.TheForge.Ux.Converters;

/// <summary>Converts a double pixel size to a <see cref="GridLength"/>; non-positive values become 0.</summary>
public sealed class DoubleToGridLengthConverter : IValueConverter
{

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {

        if (value is double d && d > 0 && !double.IsNaN(d) && !double.IsInfinity(d))
        {

            return new GridLength(d);

        }

        return new GridLength(0);

    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {

        if (value is GridLength length && length.IsAbsolute)
        {

            return length.Value;

        }

        return 0d;

    }

}
