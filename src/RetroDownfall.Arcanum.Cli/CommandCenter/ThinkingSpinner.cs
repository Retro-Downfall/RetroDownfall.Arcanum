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

/// <summary>
/// Pure Tab/Shift+Tab focus cycle for Command Center panes.
/// </summary>
/// <remarks>
/// A region only joins the cycle when it is actually on screen. Terminal.Gui's fixed-viewport rules
/// mean the sidebar and the model drop-down both disappear on a narrow terminal, and Tab landing on
/// something invisible would strand the operator with no way to see where focus went.
/// </remarks>
internal static class CommandCenterFocusCycle
{
    public static CommandCenterFocusRegion Next(
        CommandCenterFocusRegion current,
        bool forward,
        bool sidebarVisible,
        bool modelSelectorVisible)
    {
        List<CommandCenterFocusRegion> order = [CommandCenterFocusRegion.Composer];

        if (sidebarVisible)
        {
            order.Add(CommandCenterFocusRegion.Sessions);
        }

        order.Add(CommandCenterFocusRegion.Transcript);

        order.Add(CommandCenterFocusRegion.Incantations);

        if (modelSelectorVisible)
        {
            order.Add(CommandCenterFocusRegion.Model);
        }

        int idx = order.IndexOf(current);
        if (idx < 0)
        {
            // Overlay / unknown / a region that just went off screen → start of cycle.
            return forward ? order[0] : order[^1];
        }

        idx = forward ? (idx + 1) % order.Count : (idx - 1 + order.Count) % order.Count;
        return order[idx];
    }
}
