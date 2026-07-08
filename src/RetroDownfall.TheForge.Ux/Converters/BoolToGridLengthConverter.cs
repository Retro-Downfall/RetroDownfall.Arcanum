using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace RetroDownfall.TheForge.Ux.Converters;

/// <summary>
/// Converts a panel-visibility boolean into a <see cref="GridLength"/>. The converter parameter is
/// the visible length (for example <c>260</c>, <c>5</c>, <c>320</c>, or <c>*</c>); false returns
/// <c>0</c>. Used by the Phase 3 shell so collapsed panels release their grid space rather than only
/// hiding their contents.
/// </summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {

        bool visible = value is true;

        if (!visible)
        {

            return new GridLength(0);

        }

        string text = parameter?.ToString() ?? "*";

        return GridLength.Parse(text);

    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

}
