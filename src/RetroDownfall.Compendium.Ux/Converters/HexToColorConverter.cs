using System.Globalization;
using Microsoft.Maui.Graphics;

namespace RetroDownfall.Compendium.Ux.Converters;

public sealed class HexToColorConverter : IValueConverter
{

    public object Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
    {

        if (value is not string hex || string.IsNullOrWhiteSpace(hex))

        {

            return Colors.Transparent;

        }

        if (TryParse(hex, out Color? color) && color is not null)

        {

            return color;

        }

        return Colors.Transparent;

    }

    public object ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
    {

        return Binding.DoNothing;

    }

    public static bool TryParse(string hex, out Color? color)
    {

        color = null;

        if (string.IsNullOrWhiteSpace(hex))

        {

            return false;

        }

        string trimmed = hex.Trim();

        if (trimmed.StartsWith('#'))

        {

            trimmed = trimmed[1..];

        }

        if (trimmed.Length != 3

            && trimmed.Length != 4

            && trimmed.Length != 6

            && trimmed.Length != 8)

        {

            return false;

        }

        if (!trimmed.All(IsHexDigit))

        {

            return false;

        }

        try

        {

            color = trimmed.Length switch

            {

                3 => Color.FromRgb(

                    ExpandByte(trimmed[0]),

                    ExpandByte(trimmed[1]),

                    ExpandByte(trimmed[2])),

                4 => Color.FromRgba(

                    ExpandByte(trimmed[1]),

                    ExpandByte(trimmed[2]),

                    ExpandByte(trimmed[3]),

                    ExpandByte(trimmed[0])),

                6 => Color.FromRgb(

                    ParseByte(trimmed, 0),

                    ParseByte(trimmed, 2),

                    ParseByte(trimmed, 4)),

                8 => Color.FromRgba(

                    ParseByte(trimmed, 2),

                    ParseByte(trimmed, 4),

                    ParseByte(trimmed, 6),

                    ParseByte(trimmed, 0)),

                _ => null,

            };

        }
        catch

        {

            return false;

        }

        return color is not null;

    }

    private static bool IsHexDigit(char c)

        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static byte ExpandByte(char c)

    {

        int value = HexValue(c);

        return (byte)(value * 16 + value);

    }

    private static byte ParseByte(string hex, int offset)

    {

        int high = HexValue(hex[offset]);

        int low = HexValue(hex[offset + 1]);

        return (byte)(high * 16 + low);

    }

    private static int HexValue(char c)

    {

        if (c >= '0' && c <= '9')

        {

            return c - '0';

        }

        if (c >= 'a' && c <= 'f')

        {

            return c - 'a' + 10;

        }

        return c - 'A' + 10;

    }

}
