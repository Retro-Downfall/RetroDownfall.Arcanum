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
    /// Builds the diagnostic for an option spelling that a free-text prompt swallowed, or
    /// <c>null</c> when every token bound to the prompt is really prompt text.
    ///
    /// A prompt is a <c>ZeroOrMore</c> positional, so System.CommandLine binds every remaining
    /// token to it — a mistyped flag included. Nothing fails, nothing is unmatched, and
    /// <c>arcanum run --dryrun "Rewrite every file under src"</c> runs a live, billed turn with
    /// real tool calls at the exact moment the operator asked for a preview. A dash-led token
    /// before the <c>--</c> terminator is therefore refused; after the terminator it is deliberate
    /// prompt text and is left alone.
    /// </summary>
    public static string? DescribePromptOption(ParseResult parseResult, Argument prompt)
    {

        ArgumentNullException.ThrowIfNull(parseResult);

        ArgumentNullException.ThrowIfNull(prompt);

        if (parseResult.GetResult(prompt) is not ArgumentResult promptResult)
        {

            return null;

        }

        // Only tokens the prompt actually took: an option's own value can be dash-led (a `--stop`
        // sequence, say) and is not the operator's mistake.
        HashSet<string> promptTokens =
        [
            .. promptResult.Tokens.Select(static token => token.Value),
        ];

        string? offending = parseResult.Tokens
            .TakeWhile(static token => token.Type != TokenType.DoubleDash)
            .Select(static token => token.Value)
            .FirstOrDefault(value => IsOptionShaped(value) && promptTokens.Contains(value));

        if (offending is null)
        {

            return null;

        }

        string matched = MatchedPath(parseResult);

        string command = matched.Length == 0
            ? "arcanum"
            : $"arcanum {matched}";

        string? nearest = Nearest(offending, OptionSpellings(parseResult));

        string suggestion = nearest is null
            ? string.Empty
            : $" Did you mean `{nearest}`?";

        return $"`{offending}` is not an `{command}` option.{suggestion} "
            + "Put `--` before prompt text that begins with a dash.";

    }

    /// <summary>
    /// Reads as an option spelling rather than prompt text: one dash-led word whose first character
    /// after the dash is a letter or a second dash. A negative number (<c>-40 degrees …</c>), a bare
    /// <c>-</c>, and any multi-word quoted token stay prompt text.
    /// </summary>
    private static bool IsOptionShaped(string value) =>
        value.Length > 1
        && value[0] == '-'
        && (value[1] == '-' || char.IsLetter(value[1]))
        && !value.Any(char.IsWhiteSpace);

    /// <summary>
    /// Every option spelling legal where the operator typed: the resolved command's own options
    /// plus the recursive ones contributed by each ancestor up to the root.
    /// </summary>
    private static IReadOnlyList<string> OptionSpellings(ParseResult parseResult)
    {

        List<string> spellings = [];

        for (SymbolResult? current = parseResult.CommandResult;
            current is not null;
            current = current.Parent)
        {

            if (current is not CommandResult commandResult)
            {

                continue;

            }

            foreach (Option option in commandResult.Command.Options)
            {

                if (option.Hidden)
                {

                    continue;

                }

                spellings.Add(option.Name);

                spellings.AddRange(option.Aliases);

            }

        }

        return [.. spellings.Where(static spelling => spelling.StartsWith('-'))];

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
