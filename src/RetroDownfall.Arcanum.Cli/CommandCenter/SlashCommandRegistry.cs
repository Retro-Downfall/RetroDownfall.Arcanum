using System.Text;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

internal sealed record SlashCommandDescriptor(
    string Name,
    string Usage,
    string Description,
    IReadOnlyList<string> Aliases);

/// <summary>
/// The single slash-command contract. Parsing, help rendering, and "did you mean" all read this
/// table, so a command cannot be handled without being documented or documented without being
/// handled.
///
/// Names track Claude Code wherever a direct analog exists (<c>/clear</c>, <c>/compact</c>,
/// <c>/config</c>, <c>/context</c>, <c>/cost</c>, <c>/doctor</c>, <c>/memory</c>, <c>/model</c>,
/// <c>/resume</c>, <c>/status</c>, <c>/mcp</c>, <c>/help</c>). Thematic names survive only where the
/// capability has no Claude analog at all — <c>/arsenal</c>, <c>/look</c>, <c>/ward</c>,
/// <c>/spell</c>, <c>/campaign</c>. Arcanum ships no alias layer, so each removed spelling is
/// recorded once in <see cref="Removed"/> and fails with the canonical replacement named.
/// </summary>
internal static class SlashCommandRegistry
{

    private static readonly SlashCommandDescriptor[] Commands =
    [
        new("help", "/help", "Show this command list.", ["?"]),
        new("keys", "/keys", "Show keyboard shortcuts.", []),
        new("status", "/status", "Show session and host status.", []),
        new("doctor", "/doctor", "Run a compact health check.", []),
        new("clear", "/clear", "Start a fresh session thread and clear the transcript view.", []),
        new("compact", "/compact", "Queue memory consolidation for the current session.", []),
        new("context", "/context", "Show the effective turn context and token allocation.", []),
        new("cost", "/cost", "Show token and spend totals for the current session.", []),
        new("memory", "/memory", "Show the compressed Campaign Summary for this session.", []),
        new("config", "/config", "Show the effective configuration summary and its file path.", []),
        new("model", "/model [<name>]", "Show configured models, or select one for this session.", []),
        new("provider", "/provider list", "List configured providers.", []),
        new("mcp", "/mcp [reload]", "Show MCP server status, or reload MCP configuration.", []),
        new("tools", "/tools", "Show native tools.", []),
        new("arsenal", "/arsenal", "Show the effective workspace arsenal.", []),
        new("look", "/look", "Show an Eye of the World snapshot of the working directory.", []),
        new("resume", "/resume <id>", "Load a transcript and continue that session.", []),
        new("session", "/session list|new|archive <id>", "Manage sessions without leaving Command Center.", []),
        new("campaign", "/campaign list [offset]", "List a bounded display page of campaigns.", []),
        new("spell", "/spell list [cursor]", "List a bounded display page of spells.", []),
        new("ward", "/ward list|allow|deny [<id>]", "Review and resolve Ward approvals.", []),
        new("fork", "/fork [at|confirm|alternative] [<entry-id>]", "Fork the active session and open the branch.", []),
        new("branch", "/branch parent|child", "Open the visible parent or newest child branch.", []),
        new("attach", "/attach <path>", "Stage a local text file or image for the next turn.", []),
        new("attachments", "/attachments [add|reveal|refresh] <name> [vN]", "Inspect and stage bound session attachments.", []),
        new("pins", "/pins", "List persistent session context pins.", []),
        new("pin", "/pin <kind> <target>", "Pin a file, directory, symbol range, entry, attachment, URL, or diagnostic.", []),
        new("unpin", "/unpin <pin-id>", "Remove one context pin.", []),
        new("exit", "/exit", "Leave Command Center.", []),
        new("quit", "/quit", "Leave Command Center.", []),
    ];

    /// <summary>
    /// Removed spelling to the canonical command that replaced it. These exist to produce an
    /// actionable failure, never to keep the old spelling working.
    /// </summary>
    private static readonly Dictionary<string, string> Removed = new(StringComparer.Ordinal)
    {
        ["mana"] = "/context",
        ["new"] = "/clear",
        ["summary"] = "/memory",
        ["rest"] = "/compact",
        ["history"] = "/session list",
        ["delete"] = "/session archive",
        ["log"] = "/memory",
        ["keys-help"] = "/keys",
    };

    private static readonly Dictionary<string, SlashCommandDescriptor> BySpelling = BuildIndex();

    public static IReadOnlyList<SlashCommandDescriptor> All => Commands;

    public static IReadOnlyCollection<string> RemovedNames => Removed.Keys;

    public static bool TryResolve(string spelling, out SlashCommandDescriptor? descriptor) =>
        BySpelling.TryGetValue(spelling, out descriptor);

    public static bool TryResolveRemoved(string spelling, out string? canonical) =>
        Removed.TryGetValue(spelling, out canonical);

    private const int PrefixMinimumLength = 3;

    /// <summary>
    /// Deterministic suggestion over the registered spellings. An abbreviation the operator clearly
    /// meant (<c>stat</c> → <c>status</c>) is matched as a prefix and always outranks an edit-
    /// distance hit; everything else uses bounded Damerau-Levenshtein so a single transposition
    /// (<c>hlep</c>) still resolves. The ceiling scales with the typed word so a short word cannot
    /// match an unrelated long one, and every tie breaks on shortest-then-alphabetical, so the same
    /// input always yields the same suggestion.
    /// </summary>
    public static string? Suggest(string spelling)
    {

        if (string.IsNullOrWhiteSpace(spelling))
        {

            return null;

        }

        string[] candidates =
        [
            .. BySpelling.Keys
                .OrderBy(static key => key.Length)
                .ThenBy(static key => key, StringComparer.Ordinal),
        ];

        if (spelling.Length >= PrefixMinimumLength)
        {

            foreach (string candidate in candidates)
            {

                if (candidate.StartsWith(spelling, StringComparison.Ordinal))
                {

                    return $"/{BySpelling[candidate].Name}";

                }

            }

        }

        int ceiling = spelling.Length <= 4 ? 1 : 2;

        string? best = null;

        int bestDistance = int.MaxValue;

        foreach (string candidate in candidates)
        {

            int distance = Distance(spelling, candidate, ceiling);

            if (distance > ceiling || distance >= bestDistance)
            {

                continue;

            }

            bestDistance = distance;

            best = candidate;

        }

        return best is null
            ? null
            : $"/{BySpelling[best].Name}";

    }

    /// <summary>
    /// The message shown for an unrecognized slash command. A removed spelling names its exact
    /// replacement; anything else falls back to the nearest registered name, then to <c>/help</c>.
    /// </summary>
    public static string DescribeUnknown(string raw, string spelling)
    {

        if (Removed.TryGetValue(spelling, out string? canonical))
        {

            return $"`/{spelling}` was removed. Use `{canonical}` instead.";

        }

        string? suggestion = Suggest(spelling);

        return suggestion is null
            ? $"Unknown command `{raw}`. Type `/help` for available commands."
            : $"Unknown command `{raw}`. Did you mean `{suggestion}`? Type `/help` for the full list.";

    }

    public static string BuildHelpText()
    {

        StringBuilder builder = new("Command Center commands:");

        int width = Commands.Max(static command => command.Usage.Length);

        foreach (SlashCommandDescriptor command in Commands)
        {

            builder
                .Append(Environment.NewLine)
                .Append("  ")
                .Append(command.Usage.PadRight(width))
                .Append("  ")
                .Append(command.Description);

        }

        builder
            .Append(Environment.NewLine)
            .Append("  ")
            .Append("@path".PadRight(width))
            .Append("  ")
            .Append("Inline-stage a text file or Scrying image inside a message.");

        return builder.ToString();

    }

    private static Dictionary<string, SlashCommandDescriptor> BuildIndex()
    {

        Dictionary<string, SlashCommandDescriptor> index = new(StringComparer.Ordinal);

        foreach (SlashCommandDescriptor command in Commands)
        {

            index.Add(command.Name, command);

            foreach (string alias in command.Aliases)
            {

                index.Add(alias, command);

            }

        }

        return index;

    }

    /// <summary>
    /// Damerau-Levenshtein distance (adjacent transposition counts as one edit, so <c>hlep</c> is
    /// one edit from <c>help</c>) with early exit once the ceiling is exceeded. Transposition needs
    /// the row before last, so this keeps three rows rather than a full matrix.
    /// </summary>
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
