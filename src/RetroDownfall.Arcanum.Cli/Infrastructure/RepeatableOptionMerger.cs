using System.Text;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

/// <summary>
/// ConsoleAppFramework binds a repeated named flag (e.g. <c>--tag a --tag b</c>) by
/// overwriting the value on each occurrence rather than accumulating it — unlike
/// Spectre.Console.Cli, which the CLI used before this migration. Additionally, for a
/// <em>single</em> occurrence, CAF's array binding falls back to splitting the raw value on
/// every comma unless the value is JSON-array-bracketed — which silently corrupts any
/// single-occurrence value that itself contains a comma (e.g. a JSON object passed to
/// <c>--inquisitor</c>, such as <c>{"kind":"regex","pattern":"Hello"}</c>).
///
/// To keep every repeatable-flag invocation byte-for-byte compatible with pre-migration
/// behavior (including single-occurrence JSON/CSV-bearing values), this rewrites every
/// occurrence — one or many — of a known repeatable flag into a single occurrence using
/// ConsoleAppFramework's native JSON-array argument syntax before the generated parser ever
/// sees the tokens. Scanning stops at a literal "--" escape token so escaped/raw arguments
/// (e.g. the tail of <c>spell execute -- ...</c>) are never rewritten.
/// </summary>
internal static class RepeatableOptionMerger
{

    // "--tag" is intentionally excluded here: it is overloaded across commands as both a
    // repeatable array (spell/prompt create/update) and a singular scalar filter (spell
    // search, campaign spells/prompts, prompt list). It is handled separately below, scoped
    // to only the command paths where it is genuinely array-typed, so the scalar usages are
    // never wrapped/misparsed.
    private static readonly HashSet<string> UnconditionalRepeatableFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--stop",
        "--image",
        "--declared-tool",
        "--dependency",
        "--param",
        "--inquisitor",
        "--var",
    };

    private static readonly string[][] TagArrayCommandPaths =
    [
        ["spell", "create"],
        ["spell", "update"],
        ["prompt", "create"],
        ["prompt", "update"],
    ];

    public static string[] Merge(string[] args)
    {

        HashSet<string> repeatableFlags = MatchesAnyCommandPath(args, TagArrayCommandPaths)
            ? new(UnconditionalRepeatableFlags, StringComparer.OrdinalIgnoreCase) { "--tag" }
            : UnconditionalRepeatableFlags;

        int escapeIndex = Array.IndexOf(args, "--");

        int scanLength = escapeIndex < 0 ? args.Length : escapeIndex;

        Dictionary<string, List<string>>? occurrences = null;

        for (int i = 0; i < scanLength - 1; i++)
        {

            string token = args[i];

            if (!repeatableFlags.Contains(token))
            {
                continue;
            }

            occurrences ??= new(StringComparer.OrdinalIgnoreCase);

            if (!occurrences.TryGetValue(token, out List<string>? values))
            {
                values = [];

                occurrences[token] = values;
            }

            values.Add(args[i + 1]);

            i++;

        }

        if (occurrences is null)
        {
            return args;
        }

        List<string> rewritten = new(args.Length);

        HashSet<string> mergedAlready = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {

            string token = args[i];

            if (escapeIndex >= 0 && i >= escapeIndex)
            {
                rewritten.Add(token);

                continue;

            }

            if (repeatableFlags.Contains(token) && occurrences.TryGetValue(token, out List<string>? values))
            {

                if (mergedAlready.Add(token))
                {
                    rewritten.Add(token);

                    rewritten.Add(ToJsonStringArray(values));
                }

                // Drop this occurrence's value token; every occurrence of this flag
                // (including the first) contributed to the single merged value already
                // emitted above, so subsequent repeats are removed entirely.
                i++;

                continue;

            }

            rewritten.Add(token);

        }

        return rewritten.ToArray();

    }

    private static bool MatchesAnyCommandPath(string[] args, string[][] paths)
    {

        foreach (string[] path in paths)
        {

            if (args.Length < path.Length)
            {
                continue;
            }

            bool matches = true;

            for (int i = 0; i < path.Length; i++)
            {
                if (!string.Equals(args[i], path[i], StringComparison.Ordinal))
                {
                    matches = false;

                    break;
                }
            }

            if (matches)
            {
                return true;
            }

        }

        return false;

    }

    private static string ToJsonStringArray(List<string> values)
    {

        StringBuilder sb = new();

        sb.Append('[');

        for (int i = 0; i < values.Count; i++)
        {

            if (i > 0)
            {
                sb.Append(',');
            }

            AppendJsonString(sb, values[i]);

        }

        sb.Append(']');

        return sb.ToString();

    }

    private static void AppendJsonString(StringBuilder sb, string value)
    {

        sb.Append('"');

        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;

                case '\\':
                    sb.Append("\\\\");
                    break;

                case '\n':
                    sb.Append("\\n");
                    break;

                case '\r':
                    sb.Append("\\r");
                    break;

                case '\t':
                    sb.Append("\\t");
                    break;

                default:

                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');

    }

}
