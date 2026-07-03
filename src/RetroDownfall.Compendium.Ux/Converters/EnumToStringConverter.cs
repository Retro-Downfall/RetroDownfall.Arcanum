using System.Globalization;
using System.Text;

namespace RetroDownfall.Compendium.Ux.Converters;

public sealed class EnumToStringConverter : IValueConverter
{

    public object Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
    {

        if (value is null)

        {

            return string.Empty;

        }

        string name = value.ToString() ?? string.Empty;

        return ToHumanReadable(name);

    }

    public object ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
    {

        return Binding.DoNothing;

    }

    public static string ToHumanReadable(string name)
    {

        if (string.IsNullOrEmpty(name))

        {

            return name;

        }

        StringBuilder builder = new(name.Length + 8);

        builder.Append(name[0]);

        for (int i = 1; i < name.Length; i++)

        {

            char current = name[i];

            if (char.IsUpper(current) && i + 1 < name.Length && char.IsLower(name[i + 1]))

            {

                builder.Append(' ');

            }

            builder.Append(current);

        }

        return builder.ToString();

    }

}
