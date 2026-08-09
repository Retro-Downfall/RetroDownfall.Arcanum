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

        // The parser decides which token failed, not a scan of the raw arguments. Reading argv
        // directly cannot tell a verb from an option's value, and any dash-prefixed token ends the
        // scan — so `arcanum --json campain` produced no suggestion at all, and taking the *last*
        // verb meant `arcanum campain list` asked whether `list` was a root command instead of
        // whether `campain` was. Both fell through to System.CommandLine's full help dump, which
        // this diagnostic exists to replace.
        // An unknown option is not a naming problem this engine can answer, so only bare tokens are
        // considered; if nothing but options went unmatched there is no command to suggest.
        string? unrecognized = parseResult.UnmatchedTokens
            .FirstOrDefault(static token => !token.StartsWith('-'));

        if (unrecognized is null)
        {

            // Every verb the operator typed is real. The parse failed for some other reason — a
            // missing argument, a rejected value — and System.CommandLine's own message names it
            // better than a spelling guess could.
            return null;

        }

        string matched = MatchedPath(parseResult);

        string typed = matched.Length == 0
            ? unrecognized
            : $"{matched} {unrecognized}";

        if (Removed.TryGetValue(typed, out string? replacement))
        {

            return $"`arcanum {typed}` was removed. Use {replacement} instead.";

        }

        // Past the removed-spelling table, a parse failure is only a naming problem when the
        // failing token is genuinely unknown. `--help` on a command with a required argument also
        // parses with errors, and answering that with a spelling suggestion would replace the help
        // the operator asked for.
        if (RequestsHelp(arguments))
        {

            return null;

        }

        // The resolved command is the deepest one that parsed, so its children are exactly the
        // candidates that were valid where the operator's token failed. A closed positional value
        // set is deliberately not added: a value rejected by AcceptOnlyFromAmong never reaches here
        // as an unmatched token, and System.CommandLine's own error already lists every legal
        // value — a better answer than one nearest guess.
        string? suggestion = Nearest(unrecognized, Candidates(parseResult.CommandResult.Command));

        string prefix = matched.Length == 0
            ? "arcanum"
            : $"arcanum {matched}";

        return suggestion is null
            ? null
            : $"`{unrecognized}` is not an {prefix} command. Did you mean `{prefix} {suggestion}`?";

    }

    /// <summary>
    /// The canonical path of the deepest command that actually parsed, which is also the prefix the
    /// operator typed correctly. Built by walking up from the resolved command; the root's own
    /// result is the one with no parent and its name is the executable, not part of a command path.
    /// </summary>
    private static string MatchedPath(ParseResult parseResult)
    {

        List<string> names = [];

        for (SymbolResult? current = parseResult.CommandResult;
            current is not null;
            current = current.Parent)
        {

            if (current is CommandResult commandResult && commandResult.Parent is not null)
            {

                names.Add(commandResult.Command.Name);

            }

        }

        names.Reverse();

        return string.Join(' ', names);

    }

    /// <summary>
    /// What could legitimately have stood where the operator's token failed. Hidden commands are
    /// excluded so completion-plumbing verbs such as <c>completion resolve</c> are never suggested
    /// to a human.
    /// </summary>
    private static IReadOnlyList<string> Candidates(Command command) =>
        [
            .. command.Subcommands
                .Where(static child => !child.Hidden)
                .Select(static child => child.Name),
        ];

    /// <summary>
    /// Removed spellings are still named when help is requested — the replacement is the answer to
    /// "how do I use this", and help for a command that no longer exists is not. Every other
    /// suggestion defers to help.
    /// </summary>
    private static bool RequestsHelp(IReadOnlyList<string> arguments) =>
        arguments.Any(static argument =>
            argument is "--help" or "-h" or "-?" or "/?" or "/h");

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
