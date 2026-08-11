using System.Text;
using RetroDownfall.Arcanum.Cli.Infrastructure;

namespace RetroDownfall.Arcanum.Cli.Commands;

internal sealed record HelpTopic(
    string Name,
    string Summary,
    IReadOnlyList<string> Glossary,
    IReadOnlyList<string> Body,
    IReadOnlyList<string> Commands);

/// <summary>
/// Task-oriented help. Arcanum names its domain concepts thematically, which is good for coherence
/// and bad for discovery: an operator who wants "where did my conversation go" does not know to
/// look for a Session, and one who wants "stop that tool" does not know to look for a Ward. Every
/// topic therefore glosses its thematic terms in plain language before naming the commands.
/// </summary>
internal static class HelpTopics
{

    public static readonly string[] Names =
    [
        "sessions",
        "memory",
        "attachments",
        "security",
        "automation",
        "context",
        "output",
    ];

    private static readonly HelpTopic[] Topics =
    [
        new(
            "sessions",
            "Starting, continuing, branching, and finding conversations.",
            [
                "Session          one conversation thread, with its full transcript.",
                "Campaign         a project container that groups Sessions, Spells, and Prompts.",
                "Entry            one message in a Session — a prompt, an answer, or a tool result.",
                "Fork             a copy of a Session from a chosen Entry, so you can try another path.",
            ],
            [
                "Bare `arcanum` opens Command Center and continues where you left off.",
                "`arcanum run` is the one-shot form for scripts and pipes.",
                "Continuation works the same on both: -c resumes the most recent Session, -r resumes a",
                "named one. The `session` family is management only — it lists, renames, exports, and",
                "forks Sessions, but it never starts a turn.",
            ],
            [
                "arcanum run -c \"keep going\"",
                "arcanum run -r 8f14e45f \"pick this back up\"",
                "arcanum session list",
                "arcanum session show 8f14e45f-ceea-467a-9cbd-08c3c4a4c1f5",
                "arcanum session fork --title \"alternative approach\"",
                "arcanum watch session --reconnect",
            ]),

        new(
            "memory",
            "What Arcanum remembers between turns, and how to compress or inspect it.",
            [
                "Campaign Summary  a compressed summary of a Session, refreshed in the background.",
                "Loremaster        the background worker that writes those summaries.",
                "Lexicon           durable agent memory that survives across Sessions.",
                "Lore              an operator key-value store, not model-directed memory.",
            ],
            [
                "Long threads are compressed rather than truncated: the transcript stays in the",
                "Grimoire while a summary carries the older material forward. `session rest` queues",
                "that consolidation for a Session; in Command Center the same action is `/compact`.",
                "`/memory` shows the current summary.",
            ],
            [
                "arcanum session rest",
                "arcanum memory lexicon list",
                "arcanum lore list",
                "arcanum session compact",
            ]),

        new(
            "attachments",
            "Getting files, images, and pinned context into a turn.",
            [
                "Attachment       a file bound to a Session and reusable across turns.",
                "Scrying focus    an image sent to a vision-capable model.",
                "Context pin      a file, range, or URL kept in context until you unpin it.",
            ],
            [
                "There are three different lifetimes, and picking the wrong one is the usual mistake.",
                "`--with @path` stages a file for this turn only. `--attachment <guid>` reuses a file",
                "already bound to the Session. A context pin persists until removed. Redirected stdin",
                "is additional context, not a replacement for the prompt, so a pipe and an instruction",
                "compose rather than one overwriting the other.",
            ],
            [
                "arcanum run --with @notes.md \"Summarize these\"",
                "cat error.log | arcanum run \"What failed here?\"",
                "arcanum attachment list",
                "arcanum run --attachment 8f14e45f-ceea-467a-9cbd-08c3c4a4c1f5 \"Re-read that\"",
            ]),

        new(
            "security",
            "Approval gates, credentials, and filesystem boundaries.",
            [
                "Ward             an approval gate on a dangerous tool call.",
                "Forbidden Art    a tool class that always requires a Ward.",
                "Sanctum          a Campaign's trust boundary for tools and MCP servers.",
                "Workspace        the registered filesystem boundary a tool may read or write within.",
            ],
            [
                "Nothing dangerous runs unattended by default. A warded tool call pauses for approval;",
                "`--unattended` turns that pause into an automatic deny rather than an automatic allow,",
                "so an unattended run fails closed. Credentials live in the OS credential store and are",
                "never printed to stdout — `key show` writes to stderr precisely so a pipe cannot",
                "capture it.",
            ],
            [
                "arcanum ward list",
                "arcanum ward show 8f14e45f-ceea-467a-9cbd-08c3c4a4c1f5",
                "arcanum key list",
                "arcanum mcp list",
                "arcanum doctor --only credentials",
            ]),

        new(
            "automation",
            "Running Arcanum from scripts, CI, and background jobs.",
            [
                "Apprentice       a goal-driven agent that plans and executes over many steps.",
                "Unseen Servant   the background daemon that runs scheduled work.",
                "Trial            a repeatable evaluation with a pass/fail result.",
                "Spell            a named, versioned prompt program.",
            ],
            [
                "Scripted use has a fixed contract. `-p/--print` forces headless behavior even on a",
                "terminal, so nothing ever blocks on a picker. `--output-format json` emits exactly one",
                "document on stdout with diagnostics on stderr. `--yes` is the only automatic approval",
                "switch, and without it a redirected invocation that needs confirmation fails closed",
                "rather than guessing. Exit codes are closed: 0 success, 1 failure, 2 bad command line",
                "or configuration, 3 network, 130 cancelled.",
            ],
            [
                "arcanum run -p --output-format json \"List three risks\"",
                "arcanum trial run --target spell --target-value summarize",
                "arcanum apprentice list",
                "arcanum operation list",
                "arcanum watch daemons",
            ]),

        new(
            "context",
            "What Arcanum sends to the model, and how much it costs.",
            [
                "Effective context  the Campaign, Workspace, Model, and Session a command resolves to.",
                "Eye of the World   a local snapshot of the current directory's shape.",
                "Chronosync         what changed in the Workspace since the last turn.",
            ],
            [
                "Precedence is fixed: an explicit option beats saved context, which beats",
                "current-directory detection, which beats the server default. `use` is the only way to",
                "set saved context; `--no-context` skips that layer for one invocation without",
                "disabling directory detection. `context inspect` shows the resolved plan without",
                "spending anything, and `context cost` shows the token allocation.",
            ],
            [
                "arcanum context current",
                "arcanum context inspect \"Explain the Ward model\"",
                "arcanum context cost",
                "arcanum use campaign Arcanum",
                "arcanum run --dry-run \"Draft a release note\"",
            ]),

        new(
            "output",
            "Controlling what reaches stdout, stderr, and your terminal.",
            [
                "Payload          the thing you asked for; always stdout.",
                "Diagnostic       progress, warnings, and prompts; always stderr.",
            ],
            [
                "The split is absolute, so `arcanum ... --output-format json | jq` is always safe:",
                "structured output is never mixed with diagnostics, and ANSI is disabled while JSON is",
                "active so no control sequence can corrupt the pipe. `--plain` strips styling without",
                "changing payload content. `-v/--verbose` adds detail on stderr only.",
            ],
            [
                "arcanum session list --output-format json",
                "arcanum context current --json",
                "arcanum doctor --plain",
                "arcanum run -v \"Explain the Ward model\"",
            ]),
    ];

    public static bool TryGet(string name, out HelpTopic? topic)
    {

        topic = Array.Find(Topics, candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

        return topic is not null;

    }

    public static IReadOnlyList<HelpTopic> All => Topics;

}

internal sealed class HelpTopicCommands(IConsoleDispatcher dispatcher)
{

    public int Show(string? topicName)
    {

        if (string.IsNullOrWhiteSpace(topicName))
        {

            dispatcher.WritePayload(BuildIndex());

            return (int)CliExitCode.Success;

        }

        if (!HelpTopics.TryGet(topicName, out HelpTopic? topic))
        {

            dispatcher.WriteDiagnostic(
                $"`{topicName}` is not a help topic. Available: {string.Join(", ", HelpTopics.Names)}.");

            return (int)CliExitCode.ConfigurationError;

        }

        dispatcher.WritePayload(Render(topic!));

        return (int)CliExitCode.Success;

    }

    private static string BuildIndex()
    {

        StringBuilder builder = new("Help topics:");

        int width = HelpTopics.All.Max(static topic => topic.Name.Length);

        foreach (HelpTopic topic in HelpTopics.All)
        {

            builder
                .Append(Environment.NewLine)
                .Append("  ")
                .Append(topic.Name.PadRight(width))
                .Append("  ")
                .Append(topic.Summary);

        }

        return builder
            .Append(Environment.NewLine)
            .Append(Environment.NewLine)
            .Append("Run `arcanum help <topic>` for one topic, or `arcanum <command> --help` for a command.")
            .ToString();

    }

    private static string Render(HelpTopic topic)
    {

        StringBuilder builder = new();

        builder.Append(topic.Name).Append(" — ").Append(topic.Summary);

        builder.Append(Environment.NewLine).Append(Environment.NewLine).Append("Terms:");

        foreach (string term in topic.Glossary)
        {

            builder.Append(Environment.NewLine).Append("  ").Append(term);

        }

        builder.Append(Environment.NewLine).Append(Environment.NewLine);

        foreach (string line in topic.Body)
        {

            builder.Append(line).Append(Environment.NewLine);

        }

        builder.Append(Environment.NewLine).Append("Commands:");

        foreach (string command in topic.Commands)
        {

            builder.Append(Environment.NewLine).Append("  ").Append(command);

        }

        return builder.ToString();

    }

}
