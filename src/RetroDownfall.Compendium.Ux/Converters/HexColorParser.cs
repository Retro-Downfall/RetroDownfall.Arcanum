namespace RetroDownfall.Compendium.Ux.Converters;

public static class HexColorParser
{

    public static bool TryParse(string hex, out byte a, out byte r, out byte g, out byte b)
    {

        a = 255;

        r = 0;

        g = 0;

        b = 0;

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

            switch (trimmed.Length)

            {

                case 3:

                    r = ExpandByte(trimmed[0]);

                    g = ExpandByte(trimmed[1]);

                    b = ExpandByte(trimmed[2]);

                    return true;

                case 4:

                    a = ExpandByte(trimmed[0]);

                    r = ExpandByte(trimmed[1]);

                    g = ExpandByte(trimmed[2]);

                    b = ExpandByte(trimmed[3]);

                    return true;

                case 6:

                    r = ParseByte(trimmed, 0);

                    g = ParseByte(trimmed, 2);

                    b = ParseByte(trimmed, 4);

                    return true;

                case 8:

                    a = ParseByte(trimmed, 0);

                    r = ParseByte(trimmed, 2);

                    g = ParseByte(trimmed, 4);

                    b = ParseByte(trimmed, 6);

                    return true;

                default:

                    return false;

            }

        }
        catch

        {

            return false;

        }

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
