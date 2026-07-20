namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Prepares composer text for slash dispatch or chat turn without joining or collapsing embedded blank lines.
/// </summary>
internal static class CommandCenterSubmitText
{
    /// <summary>
    /// Returns false when the buffer is empty/whitespace-only.
    /// Chat payloads keep the original string (embedded blank lines exact).
    /// Slash payloads use a trimmed copy for parsing only.
    /// </summary>
    public static bool TryPrepare(string? text, out string chatOrSlashPayload, out bool isSlash)
    {
        chatOrSlashPayload = string.Empty;
        isSlash = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Slash detection allows leading whitespace; parser still sees a trimmed command line.
        if (ShellCommandParser.IsSlash(text))
        {
            isSlash = true;
            chatOrSlashPayload = text.Trim();
            return chatOrSlashPayload.Length > 0;
        }

        // Preserve embedded blank lines and trailing newlines — do not Trim chat content.
        chatOrSlashPayload = text;
        return true;
    }
}
