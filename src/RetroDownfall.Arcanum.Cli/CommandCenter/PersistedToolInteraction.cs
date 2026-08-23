using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Grimoire persists tool turns as Assistant <c>[ToolCall: name(args)]</c> + System
/// <c>[ToolResult: …]</c> (see <c>GrimoireRepository.AppendToolInteractionAsync</c>), not as
/// Role=tool. Resume must detect those and route them to Incantations.
/// </summary>
internal static class PersistedToolInteraction
{
    public static bool IsToolInteraction(EntryDto entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!string.IsNullOrWhiteSpace(entry.ToolName))
        {
            return true;
        }

        SessionLogEntryKind kind = SessionLogBuffer.MapEntryRole(entry.Role);
        if (kind == SessionLogEntryKind.Tool)
        {
            return true;
        }

        string content = (entry.Content ?? string.Empty).TrimStart();
        return content.StartsWith("[ToolCall:", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith("[ToolResult:", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseToolCall(string? content, out string toolName, out string? arguments)
    {
        toolName = string.Empty;
        arguments = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        string trimmed = content.Trim();
        const string prefix = "[ToolCall:";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !trimmed.EndsWith(']'))
        {
            return false;
        }

        string inner = trimmed[prefix.Length..^1].Trim();
        if (inner.Length == 0)
        {
            return false;
        }

        int open = inner.IndexOf('(');
        if (open > 0 && inner.EndsWith(')'))
        {
            toolName = inner[..open].Trim();
            arguments = inner[(open + 1)..^1];
            return toolName.Length > 0;
        }

        int colon = inner.IndexOf(':');
        if (colon > 0)
        {
            toolName = inner[..colon].Trim();
            arguments = inner[(colon + 1)..].Trim();
            return toolName.Length > 0;
        }

        toolName = inner;
        return true;
    }

    public static bool TryParseToolResult(string? content, out string result)
    {
        result = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        string trimmed = content.Trim();
        const string prefix = "[ToolResult:";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !trimmed.EndsWith(']'))
        {
            return false;
        }

        result = trimmed[prefix.Length..^1].Trim();
        return true;
    }
}
