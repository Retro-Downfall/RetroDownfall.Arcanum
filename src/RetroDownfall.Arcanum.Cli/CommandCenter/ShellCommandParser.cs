namespace RetroDownfall.Arcanum.Cli.CommandCenter;

internal enum ShellCommandKind
{
    Help,
    Exit,
    Quit,
    Clear,
    Compact,
    Config,
    Context,
    Cost,
    Memory,
    Look,
    Status,
    Doctor,
    Model,
    ModelSelect,
    ProviderList,
    Mcp,
    McpReload,
    Arsenal,
    CampaignList,
    SessionList,
    SessionResume,
    SessionArchive,
    SessionFork,
    BranchParent,
    BranchChild,
    SpellList,
    Tools,
    WardList,
    WardAllow,
    WardDeny,
    Keys,
    Attach,
    AttachmentsList,
    AttachmentsAdd,
    AttachmentsReveal,

    AttachmentsRefresh,

    PinList,
    Pin,
    Unpin,
    Denied,
    Unknown,
}

internal sealed record ParsedShellCommand(
    ShellCommandKind Kind,
    string Raw,
    string? Argument = null,
    string? DenialMessage = null,
    int? Version = null,
    string? SecondaryArgument = null);

/// <summary>
/// Explicit slash-command grammar for Command Center v1. No reflection.
/// </summary>
internal sealed class ShellCommandParser
{
    private const string AttachmentsUsage =
        "Usage: /attachments | /attachments add <logicalName> [vN] | /attachments reveal <logicalName> [vN] | /attachments refresh <logicalName>";

    public ParsedShellCommand Parse(string input)
    {
        string raw = (input ?? string.Empty).Trim();

        if (raw.Length == 0 || raw[0] != '/')
        {
            return new ParsedShellCommand(ShellCommandKind.Unknown, raw, DenialMessage: "Not a slash command.");
        }

        string body = raw[1..].Trim();
        if (body.Length == 0)
        {
            return Unknown(raw);
        }

        string[] parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string head = parts[0].ToLowerInvariant();

        // Must run before attach* denial — "attachments" starts with "attach".
        if (head is "attachments")
        {
            return ParseAttachments(raw, parts);
        }

        if (head is "attach")
        {
            string? arg = parts.Length >= 2
                ? string.Join(' ', parts.Skip(1))
                : null;
            return new ParsedShellCommand(ShellCommandKind.Attach, raw, Argument: arg);
        }

        // /context is the Claude-aligned token/context-window view. Persistent pins are a distinct
        // Arcanum capability and get their own verbs rather than overloading the same name.
        if (head is "pins")
        {
            return parts.Length == 1
                ? new ParsedShellCommand(ShellCommandKind.PinList, raw)
                : Denied(raw, "Usage: /pins");
        }

        if (head is "pin")
        {
            return parts.Length >= 3
                ? new ParsedShellCommand(
                    ShellCommandKind.Pin,
                    raw,
                    Argument: parts[1],
                    SecondaryArgument: string.Join(' ', parts.Skip(2)))
                : Denied(raw, "Usage: /pin <kind> <target>");
        }

        if (head is "unpin")
        {
            return parts.Length == 2
                ? new ParsedShellCommand(ShellCommandKind.Unpin, raw, Argument: parts[1])
                : Denied(raw, "Usage: /unpin <pin-id>");
        }

        if (head.StartsWith("attach", StringComparison.Ordinal))
        {
            return Denied(
                raw,
                "Unknown attach form. Use `/attach <path>`, `/attachments …`, or `@path` in a message.");
        }

        if (head is "serve")
        {
            return Denied(raw, "Host lifecycle commands are not available in Command Center. Run `arcanum serve` outside.");
        }

        if (head is "daemon")
        {
            return Denied(raw, "Daemon commands are not available in Command Center v1. Run `arcanum daemon …` outside.");
        }

        if (head is "key")
        {
            return Denied(raw, "Key commands are not available in Command Center (secrets). Run `arcanum key …` outside.");
        }

        if (head is "campaign" or "spell" or "ward"
            && parts.Length >= 2
            && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {

            ShellCommandKind kind = head switch
            {
                "campaign" => ShellCommandKind.CampaignList,
                "spell" => ShellCommandKind.SpellList,
                _ => ShellCommandKind.WardList,
            };

            if (parts.Length == 2)
            {
                return new ParsedShellCommand(kind, raw);
            }

            if (parts.Length == 3
                && kind == ShellCommandKind.SpellList)
            {

                return new ParsedShellCommand(
                    kind,
                    raw,
                    Argument: parts[2]);

            }

            if (parts.Length == 3
                && int.TryParse(parts[2], out int offset)
                && offset >= 0)
            {
                return new ParsedShellCommand(kind, raw, Argument: offset.ToString());
            }

            return Denied(
                raw,
                kind == ShellCommandKind.SpellList
                    ? "Usage: /spell list [opaque-cursor]."
                    : $"Usage: /{head} list [nonnegative-offset].");

        }

        return head switch
        {
            "help" or "?" => new ParsedShellCommand(ShellCommandKind.Help, raw),
            "exit" => new ParsedShellCommand(ShellCommandKind.Exit, raw),
            "quit" => new ParsedShellCommand(ShellCommandKind.Quit, raw),
            "clear" => new ParsedShellCommand(ShellCommandKind.Clear, raw),
            "compact" => new ParsedShellCommand(ShellCommandKind.Compact, raw),
            "config" => new ParsedShellCommand(ShellCommandKind.Config, raw),
            "context" => new ParsedShellCommand(ShellCommandKind.Context, raw),
            "cost" => new ParsedShellCommand(ShellCommandKind.Cost, raw),
            "memory" => new ParsedShellCommand(ShellCommandKind.Memory, raw),
            "look" => new ParsedShellCommand(ShellCommandKind.Look, raw),
            "status" => new ParsedShellCommand(ShellCommandKind.Status, raw),
            "doctor" => new ParsedShellCommand(ShellCommandKind.Doctor, raw),
            "mcp" when parts.Length == 1 => new ParsedShellCommand(ShellCommandKind.Mcp, raw),
            "mcp" when parts.Length == 2 && parts[1].Equals("reload", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.McpReload, raw),
            "arsenal" => new ParsedShellCommand(ShellCommandKind.Arsenal, raw),
            "tools" => new ParsedShellCommand(ShellCommandKind.Tools, raw),
            "keys" => new ParsedShellCommand(ShellCommandKind.Keys, raw),
            "model" when parts.Length == 1 => new ParsedShellCommand(ShellCommandKind.Model, raw),
            "model" => new ParsedShellCommand(
                ShellCommandKind.ModelSelect,
                raw,
                Argument: string.Join(' ', parts.Skip(1))),
            "provider" when parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.ProviderList, raw),
            "resume" when parts.Length == 2
                => new ParsedShellCommand(ShellCommandKind.SessionResume, raw, Argument: parts[1]),
            "session" when parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.SessionList, raw),
            "session" when parts.Length >= 2 && parts[1].Equals("new", StringComparison.OrdinalIgnoreCase)
                => Denied(raw, "`/session new` was removed. Use `/clear` to start a fresh thread."),
            "session" when parts.Length == 3 && parts[1].Equals("archive", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.SessionArchive, raw, Argument: parts[2]),
            "session" when parts.Length >= 3 && parts[1].Equals("resume", StringComparison.OrdinalIgnoreCase)
                => Denied(raw, "`/session resume` was removed. Use `/resume <id>` instead."),
            "fork" when parts.Length == 1
                => new ParsedShellCommand(ShellCommandKind.SessionFork, raw),
            "fork" when parts.Length == 2 && parts[1].Equals("at", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.SessionFork, raw, SecondaryArgument: "selected"),
            "fork" when parts.Length == 2 && parts[1].Equals("confirm", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.SessionFork, raw, SecondaryArgument: "confirm"),
            "fork" when parts.Length == 2 && parts[1].Equals("alternative", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.SessionFork, raw, SecondaryArgument: "alternative"),
            "fork" when parts.Length == 3 && parts[1].Equals("at", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.SessionFork, raw, Argument: parts[2]),
            "branch" when parts.Length == 2 && parts[1].Equals("parent", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.BranchParent, raw),
            "branch" when parts.Length == 2 && parts[1].Equals("child", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.BranchChild, raw),
            "ward" when parts.Length >= 2 && parts[1].Equals("allow", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(
                    ShellCommandKind.WardAllow,
                    raw,
                    Argument: parts.Length >= 3 ? parts[2] : null),
            "ward" when parts.Length >= 2 && parts[1].Equals("deny", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(
                    ShellCommandKind.WardDeny,
                    raw,
                    Argument: parts.Length >= 3 ? parts[2] : null),
            _ => Unknown(raw),
        };
    }

    public static bool IsSlash(string input) =>
        !string.IsNullOrWhiteSpace(input) && input.TrimStart().StartsWith('/');

    private static ParsedShellCommand ParseAttachments(string raw, string[] parts)
    {
        if (parts.Length == 1)
        {
            return new ParsedShellCommand(ShellCommandKind.AttachmentsList, raw);
        }

        string sub = parts[1].ToLowerInvariant();
        if (sub is "refresh")

        {

            return parts.Length == 3

                ? new ParsedShellCommand(

                    ShellCommandKind.AttachmentsRefresh,

                    raw,

                    Argument: parts[2])

                : Denied(raw, AttachmentsUsage);

        }

        if (sub is "add" or "reveal")
        {
            if (parts.Length is < 3 or > 4)
            {
                return Denied(raw, AttachmentsUsage);
            }

            string logicalName = parts[2];
            int? version = null;
            if (parts.Length == 4)
            {
                if (!TryParseVersionToken(parts[3], out int parsedVersion))
                {
                    return Denied(raw, AttachmentsUsage);
                }

                version = parsedVersion;
            }

            ShellCommandKind kind = sub is "add"
                ? ShellCommandKind.AttachmentsAdd
                : ShellCommandKind.AttachmentsReveal;
            return new ParsedShellCommand(kind, raw, Argument: logicalName, Version: version);
        }

        return Denied(raw, AttachmentsUsage);
    }

    private static bool TryParseVersionToken(string token, out int version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        ReadOnlySpan<char> span = token.AsSpan().Trim();
        if (span.Length > 1 && (span[0] is 'v' or 'V'))
        {
            span = span[1..];
        }

        return int.TryParse(span, out version) && version > 0;
    }

    /// <summary>
    /// Every unrecognized name goes through the registry, so a removed spelling names its exact
    /// replacement and a near miss suggests the closest canonical command instead of dead-ending.
    /// </summary>
    private static ParsedShellCommand Unknown(string raw)
    {

        string spelling = raw.TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries) is [string head, ..]
            ? head.ToLowerInvariant()
            : string.Empty;

        return new ParsedShellCommand(
            ShellCommandKind.Unknown,
            raw,
            DenialMessage: SlashCommandRegistry.DescribeUnknown(raw, spelling));

    }

    private static ParsedShellCommand Denied(string raw, string message) =>
        new(ShellCommandKind.Denied, raw, DenialMessage: message);
}
