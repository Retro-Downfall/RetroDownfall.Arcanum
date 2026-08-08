using System.CommandLine;
using System.CommandLine.Parsing;

namespace RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

/// <summary>
/// Turns a failed parse into an actionable message.
///
/// Arcanum ships no compatibility aliases, so for anyone carrying muscle memory from the old
/// surface this is the entire migration path: <see cref="Removed"/> maps each deleted spelling to
/// the exact command that replaced it. Anything not on that list falls back to a bounded,
/// deterministic distance over the sibling commands valid at the failing level.
///
/// A suggestion is only ever printed. Nothing here runs a command on the operator's behalf.
/// </summary>
internal static class CliSuggestionEngine
{

    /// <summary>
    /// Removed spelling (as a full command path) to its canonical replacement.
    /// </summary>
    private static readonly Dictionary<string, string> Removed = new(StringComparer.Ordinal)
    {
        ["ask"] = "arcanum run",
        ["chat"] = "arcanum (bare, for the interactive Command Center) or arcanum run",
        ["mana"] = "arcanum context cost",
        ["session get"] = "arcanum session show",
        ["session chat"] = "arcanum run -c, arcanum run -r <id>, or arcanum run --session <id>",
        ["session watch"] = "arcanum watch session",
        ["session new"] = "arcanum run --new",
        ["workspace get"] = "arcanum workspace show",
        ["mcp get"] = "arcanum mcp show",
        ["campaign get"] = "arcanum campaign show",
        ["campaign use"] = "arcanum use campaign",
        ["spell get"] = "arcanum spell show",
        ["prompt get"] = "arcanum prompt show",
        ["model get"] = "arcanum model show",
        ["provider get"] = "arcanum provider show",
        ["ward get"] = "arcanum ward show",
        ["apprentice get"] = "arcanum apprentice show",
        ["apprentice chronicle"] = "arcanum watch apprentice",
        ["batch watch"] = "arcanum batch wait",
    };

    /// <summary>
    /// Builds the diagnostic for a failed parse, or <c>null</c> when the failure is not an
    /// unrecognized command and System.CommandLine's own message is already the better one.
    /// </summary>
    public static string? Describe(ParseResult parseResult, IReadOnlyList<string> arguments)
    {

        ArgumentNullException.ThrowIfNull(parseResult);

        ArgumentNullException.ThrowIfNull(arguments);

        string[] verbs =
        [
            .. arguments.TakeWhile(static argument => !argument.StartsWith('-')),
        ];

        if (verbs.Length == 0)
        {

            return null;

        }

        // Longest path first: `session get` must win over a bare `get` lookup.
        for (int length = verbs.Length; length > 0; length--)
        {

            string path = string.Join(' ', verbs.Take(length));

            if (Removed.TryGetValue(path, out string? replacement))
            {

                return $"`arcanum {path}` was removed. Use {replacement} instead.";

            }

        }

        Command resolved = parseResult.CommandResult.Command;

        string unrecognized = verbs[^1];

        // The resolved command is the deepest one that parsed, so its children are exactly the
        // candidates that were valid where the operator's token failed.
        string? suggestion = Nearest(
            unrecognized,
            [.. resolved.Subcommands.Where(static command => !command.Hidden).Select(static command => command.Name)]);

        string prefix = resolved.Name == RootName(parseResult)
            ? "arcanum"
            : $"arcanum {string.Join(' ', verbs.Take(verbs.Length - 1))}";

        return suggestion is null
            ? null
            : $"`{unrecognized}` is not an {prefix} command. Did you mean `{prefix} {suggestion}`?";

    }

    private static string RootName(ParseResult parseResult) =>
        parseResult.RootCommandResult.Command.Name;

    /// <summary>
    /// Bounded Damerau-Levenshtein over the candidates valid at this level. The ceiling scales with
    /// the typed word, ties break shortest-then-alphabetical, and an unrelated word yields nothing
    /// rather than a confidently wrong guess.
    /// </summary>
    internal static string? Nearest(string typed, IReadOnlyList<string> candidates)
    {

        if (string.IsNullOrWhiteSpace(typed) || candidates.Count == 0)
        {

            return null;

        }

        int ceiling = typed.Length <= 4 ? 1 : 2;

        string? best = null;

        int bestDistance = int.MaxValue;

        foreach (string candidate in candidates
            .OrderBy(static candidate => candidate.Length)
            .ThenBy(static candidate => candidate, StringComparer.Ordinal))
        {

            if (typed.Length >= 3
                && candidate.StartsWith(typed, StringComparison.Ordinal))
            {

                return candidate;

            }

            int distance = Distance(typed, candidate, ceiling);

            if (distance > ceiling || distance >= bestDistance)
            {

                continue;

            }

            bestDistance = distance;

            best = candidate;

        }

        return best;

    }

    private static int Distance(string left, string right, int ceiling)
    {

        if (Math.Abs(left.Length - right.Length) > ceiling)
        {

            return ceiling + 1;

        }

        int[] beforePrevious = new int[right.Length + 1];

        int[] previous = new int[right.Length + 1];

        int[] current = new int[right.Length + 1];

        for (int column = 0; column <= right.Length; column++)
        {

            previous[column] = column;

        }

        for (int row = 1; row <= left.Length; row++)
        {

            current[0] = row;

            int rowBest = current[0];

            for (int column = 1; column <= right.Length; column++)
            {

                int substitution = previous[column - 1]
                    + (left[row - 1] == right[column - 1] ? 0 : 1);

                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    substitution);

                if (row > 1
                    && column > 1
                    && left[row - 1] == right[column - 2]
                    && left[row - 2] == right[column - 1])
                {

                    current[column] = Math.Min(current[column], beforePrevious[column - 2] + 1);

                }

                rowBest = Math.Min(rowBest, current[column]);

            }

            if (rowBest > ceiling)
            {

                return ceiling + 1;

            }

            (beforePrevious, previous, current) = (previous, current, beforePrevious);

        }

        return previous[right.Length];

    }

}
