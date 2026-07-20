using System.Text;

namespace RetroDownfall.TheForge.Ux.Services.Git;

/// <summary>Parses <c>git status --porcelain=v1</c> output into structured entries.</summary>
public static class GitPorcelainParser
{

    public static IReadOnlyList<GitPorcelainEntry> Parse(string? porcelain)
    {

        if (string.IsNullOrWhiteSpace(porcelain))
        {

            return [];

        }

        List<GitPorcelainEntry> entries = [];

        using StringReader reader = new(porcelain);

        while (true)
        {

            string? line = reader.ReadLine();

            if (line is null)
            {

                break;

            }

            if (line.Length < 3)
            {

                continue;

            }

            char indexStatus = line[0];

            char workTreeStatus = line[1];

            // Porcelain v1: "XY PATH" or "XY ORIG -> PATH" (third char is a space for normal entries).
            string remainder = line.Length > 3 && line[2] == ' '
                ? line[3..]
                : line[2..].TrimStart();

            if (string.IsNullOrWhiteSpace(remainder))
            {

                continue;

            }

            if (!TrySplitRename(remainder, out string path, out string? originalPath))
            {

                path = UnquotePath(remainder);

                originalPath = null;

            }

            if (string.IsNullOrWhiteSpace(path))
            {

                continue;

            }

            entries.Add(new GitPorcelainEntry(indexStatus, workTreeStatus, path, originalPath));

        }

        return entries;

    }

    private static bool TrySplitRename(string remainder, out string path, out string? originalPath)
    {

        // Renames/copies: "old -> new" (paths may be quoted).
        const string arrow = " -> ";

        int arrowIndex = IndexOfUnquoted(remainder, arrow);

        if (arrowIndex < 0)
        {

            path = string.Empty;

            originalPath = null;

            return false;

        }

        originalPath = UnquotePath(remainder[..arrowIndex]);

        path = UnquotePath(remainder[(arrowIndex + arrow.Length)..]);

        return !string.IsNullOrWhiteSpace(path);

    }

    private static int IndexOfUnquoted(string text, string value)
    {

        bool inQuotes = false;

        for (int i = 0; i <= text.Length - value.Length; i++)
        {

            char c = text[i];

            if (c == '"')
            {

                inQuotes = !inQuotes;

                continue;

            }

            if (inQuotes)
            {

                continue;

            }

            if (text.AsSpan(i).StartsWith(value, StringComparison.Ordinal))
            {

                return i;

            }

        }

        return -1;

    }

    private static string UnquotePath(string raw)
    {

        string trimmed = raw.Trim();

        if (trimmed.Length < 2 || trimmed[0] != '"')
        {

            return trimmed;

        }

        StringBuilder builder = new(trimmed.Length);

        for (int i = 1; i < trimmed.Length; i++)
        {

            char c = trimmed[i];

            if (c == '"' && i == trimmed.Length - 1)
            {

                break;

            }

            if (c == '\\' && i + 1 < trimmed.Length)
            {

                char next = trimmed[++i];

                builder.Append(next switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    _ => next,
                });

                continue;

            }

            builder.Append(c);

        }

        return builder.ToString();

    }

}
