using System.Text;

namespace RetroDownfall.Compendium.Ux.ViewModels;

internal static class StringExtensions
{

    public static string[] SplitCsv(this string? value)
    {

        if (string.IsNullOrWhiteSpace(value))
        {

            return [];

        }

        // Use Span<char> to minimize allocations during parsing
        ReadOnlySpan<char> span = value.AsSpan();

        List<string> results = new();

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        int start = 0;

        for (int i = 0; i <= span.Length; i++)
        {

            if (i == span.Length || span[i] == ',')
            {

                ReadOnlySpan<char> segment = span.Slice(start, i - start);

                ReadOnlySpan<char> trimmed = segment.Trim();

                if (!trimmed.IsEmpty && !trimmed.IsWhiteSpace())
                {

                    string item = trimmed.ToString();

                    if (seen.Add(item))
                    {

                        results.Add(item);

                    }

                }

                start = i + 1;

            }

        }

        return [.. results];

    }

    public static string JoinCsv(this IEnumerable<string>? values)
    {

        if (values is null)
        {

            return string.Empty;

        }

        // Pre-allocate StringBuilder with estimated capacity
        StringBuilder builder = new(256);

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        bool first = true;

        foreach (string value in values)
        {

            if (string.IsNullOrWhiteSpace(value))
            {

                continue;

            }

            if (!seen.Add(value))
            {

                continue;

            }

            if (!first)
            {

                builder.Append(", ");

            }

            builder.Append(value);

            first = false;

        }

        return builder.ToString();

    }

}
