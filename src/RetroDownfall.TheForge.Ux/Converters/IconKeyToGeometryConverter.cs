using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace RetroDownfall.TheForge.Ux.Converters;

/// <summary>
/// Maps an Atelier icon key (e.g. <c>IconSpell</c> or <c>IconSpellGeometry</c>) to a
/// <see cref="StreamGeometry"/> from application resources.
/// </summary>
public sealed class IconKeyToGeometryConverter : IValueConverter
{

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {

        if (value is not string key || string.IsNullOrWhiteSpace(key))
        {

            key = "IconSpell";

        }

        Application? app = Application.Current;

        if (app is null)
        {

            return null;

        }

        if (TryFindGeometry(app, key, out StreamGeometry? geometry))
        {

            return geometry;

        }

        if (!key.EndsWith("Geometry", StringComparison.Ordinal)
            && TryFindGeometry(app, key + "Geometry", out geometry))
        {

            return geometry;

        }

        return TryFindGeometry(app, "IconSpellGeometry", out geometry) ? geometry : null;

    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool TryFindGeometry(Application app, string key, out StreamGeometry? geometry)
    {

        ThemeVariant? theme = app.ActualThemeVariant;

        if (app.TryGetResource(key, theme, out object? resource) && resource is StreamGeometry found)
        {

            geometry = found;

            return true;

        }

        geometry = null;

        return false;

    }

}
