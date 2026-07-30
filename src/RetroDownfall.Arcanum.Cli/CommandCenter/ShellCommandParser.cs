namespace RetroDownfall.Arcanum.Cli.CommandCenter;

internal enum ShellCommandKind
{
    Help,
    Exit,
    Quit,
    Clear,
    Status,
    Doctor,
    ModelList,
    ProviderList,
    Mcp,
    Arsenal,
    CampaignList,
    SessionList,
    SessionResume,
    SessionNew,
    SessionFork,
    BranchParent,
    BranchChild,
    SpellList,
    Tools,
    Mana,
    WardList,
    WardAllow,
    WardDeny,
    Keys,
    Attach,
    AttachmentsList,
    AttachmentsAdd,
    AttachmentsReveal,
    ContextList,
    ContextPin,
    ContextUnpin,
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
        "Usage: /attachments | /attachments add <logicalName> [vN] | /attachments reveal <logicalName> [vN]";

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

        if (head is "context")
        {
            if (parts.Length == 1 || (parts.Length == 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase)))
            {
                return new ParsedShellCommand(ShellCommandKind.ContextList, raw);
            }
            if (parts.Length >= 4 && parts[1].Equals("pin", StringComparison.OrdinalIgnoreCase))
            {
                return new ParsedShellCommand(
                    ShellCommandKind.ContextPin,
                    raw,
                    Argument: parts[2],
                    SecondaryArgument: string.Join(' ', parts.Skip(3)));
            }
            if (parts.Length == 3 && parts[1].Equals("unpin", StringComparison.OrdinalIgnoreCase))
            {
                return new ParsedShellCommand(ShellCommandKind.ContextUnpin, raw, Argument: parts[2]);
            }
            return Denied(raw, "Usage: /context [list] | /context pin <kind> <target> | /context unpin <pin-id>");
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

        return head switch
        {
            "help" or "?" => new ParsedShellCommand(ShellCommandKind.Help, raw),
            "exit" => new ParsedShellCommand(ShellCommandKind.Exit, raw),
            "quit" => new ParsedShellCommand(ShellCommandKind.Quit, raw),
            "clear" => new ParsedShellCommand(ShellCommandKind.Clear, raw),
            "status" => new ParsedShellCommand(ShellCommandKind.Status, raw),
            "doctor" => new ParsedShellCommand(ShellCommandKind.Doctor, raw),
            "mcp" => new ParsedShellCommand(ShellCommandKind.Mcp, raw),
            "arsenal" => new ParsedShellCommand(ShellCommandKind.Arsenal, raw),
            "tools" => new ParsedShellCommand(ShellCommandKind.Tools, raw),
            "mana" => new ParsedShellCommand(ShellCommandKind.Mana, raw),
            "keys" => new ParsedShellCommand(ShellCommandKind.Keys, raw),
            "model" when parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.ModelList, raw),
            "provider" when parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.ProviderList, raw),
            "campaign" when parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.CampaignList, raw),
            "session" when parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.SessionList, raw),
            "session" when parts.Length >= 2 && parts[1].Equals("new", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.SessionNew, raw),
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
            "session" when parts.Length >= 3 && parts[1].Equals("resume", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.SessionResume, raw, Argument: parts[2]),
            "spell" when parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.SpellList, raw),
            "ward" when parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase)
                => new ParsedShellCommand(ShellCommandKind.WardList, raw),
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

    private static ParsedShellCommand Unknown(string raw) =>
        new(
            ShellCommandKind.Unknown,
            raw,
            DenialMessage: $"Unknown command `{raw}`. Type `/help` for available commands.");

    private static ParsedShellCommand Denied(string raw, string message) =>
        new(ShellCommandKind.Denied, raw, DenialMessage: message);
}
