namespace RetroDownfall.Compendium.Ux.ViewModels;

internal static class StringExtensions
{

    public static string[] SplitCsv(this string? value)
    {

        if (string.IsNullOrWhiteSpace(value))
        {

            return [];

        }

        return Deduplicate(
            value.Split([','], StringSplitOptions.RemoveEmptyEntries)
                .Select(static s => s.Trim())
                .Where(static s => !string.IsNullOrWhiteSpace(s)));

    }

    public static string JoinCsv(this IEnumerable<string>? values)
    {

        if (values is null)
        {

            return string.Empty;

        }

        return string.Join(", ", Deduplicate(values.Where(static s => !string.IsNullOrWhiteSpace(s))));

    }

    private static string[] Deduplicate(IEnumerable<string> values)
    {

        List<string> unique = [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string value in values)
        {

            if (seen.Add(value))
            {

                unique.Add(value);

            }

        }

        return unique.ToArray();

    }

}
