namespace RetroDownfall.Compendium.Ux.ViewModels;

internal static class StringExtensions
{

    public static string[] SplitCsv(this string? value)
    {

        if (string.IsNullOrWhiteSpace(value))
        {

            return [];

        }

        return value.Split([','], StringSplitOptions.RemoveEmptyEntries)

            .Select(static s => s.Trim())

            .Where(static s => !string.IsNullOrWhiteSpace(s))

            .ToArray();

    }

    public static string JoinCsv(this IEnumerable<string>? values)
    {

        if (values is null)
        {

            return string.Empty;

        }

        return string.Join(", ", values.Where(static s => !string.IsNullOrWhiteSpace(s)));

    }

}
