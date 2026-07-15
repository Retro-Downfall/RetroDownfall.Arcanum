using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using RetroDownfall.TheForge.Ux.Services.Whispers;

namespace RetroDownfall.TheForge.Ux.Converters;

/// <summary>Maps <see cref="WhisperSeverity"/> to a themed border brush resource key.</summary>
public sealed class WhisperSeverityToBorderBrushConverter : IValueConverter
{

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {

        if (value is not WhisperSeverity severity)
        {

            return Brushes.Transparent;

        }

        string resourceKey = severity switch
        {
            WhisperSeverity.Success => "ForgeSuccessBrush",
            WhisperSeverity.Warning => "ForgeWarningBrush",
            WhisperSeverity.Error => "ForgeErrorBrush",
            _ => "ForgeAccentBrush",
        };

        if (Application.Current?.TryGetResource(resourceKey, Application.Current.ActualThemeVariant, out object? resource) == true
            && resource is IBrush brush)
        {

            return brush;

        }

        return Brushes.Transparent;

    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

}
