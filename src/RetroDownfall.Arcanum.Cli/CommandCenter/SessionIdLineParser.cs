namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Extracts a Grimoire Session Guid from a session-list log line (or a wrapped neighbor).
/// </summary>
internal static class SessionIdLineParser
{
    public static bool TryExtract(string? line, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        foreach (string raw in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string token = raw.TrimStart('-', '•', '*');
            if (Guid.TryParse(token, out sessionId))
            {
                return true;
            }
        }

        // Whole line might be just the Guid (preferred list format).
        return Guid.TryParse(line.Trim(), out sessionId);
    }

    /// <summary>
    /// Resolves a Guid from <paramref name="index"/>, looking at the selected line and
    /// one line above (title lines sit under a Guid-only row).
    /// </summary>
    public static bool TryExtractNear(IReadOnlyList<string> lines, int index, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        if (lines.Count == 0 || index < 0 || index >= lines.Count)
        {
            return false;
        }

        if (TryExtract(lines[index], out sessionId))
        {
            return true;
        }

        if (index > 0 && TryExtract(lines[index - 1], out sessionId))
        {
            return true;
        }

        return false;
    }
}
