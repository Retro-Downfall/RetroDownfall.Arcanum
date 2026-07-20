namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>Braille spinner frames for synthetic Thinking indicator.</summary>
internal static class ThinkingSpinner
{
    public static readonly string[] Frames =
    [
        "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏",
    ];

    public const string Prefix = "Thinking";

    public static string Frame(int tick)
    {
        int i = tick % Frames.Length;
        if (i < 0)
        {
            i += Frames.Length;
        }

        return Frames[i];
    }

    public static string Format(int tick) => $"{Prefix} {Frame(tick)}";
}

/// <summary>Pure Tab/Shift+Tab focus cycle for Command Center panes.</summary>
internal static class CommandCenterFocusCycle
{
    public static CommandCenterFocusRegion Next(
        CommandCenterFocusRegion current,
        bool forward,
        bool sidebarVisible)
    {
        CommandCenterFocusRegion[] order = sidebarVisible
            ?
            [
                CommandCenterFocusRegion.Composer,
                CommandCenterFocusRegion.Sessions,
                CommandCenterFocusRegion.Transcript,
                CommandCenterFocusRegion.Incantations,
            ]
            :
            [
                CommandCenterFocusRegion.Composer,
                CommandCenterFocusRegion.Transcript,
                CommandCenterFocusRegion.Incantations,
            ];

        int idx = Array.IndexOf(order, current);
        if (idx < 0)
        {
            // Overlay / unknown → start of cycle.
            return forward ? order[0] : order[^1];
        }

        idx = forward ? (idx + 1) % order.Length : (idx - 1 + order.Length) % order.Length;
        return order[idx];
    }
}
