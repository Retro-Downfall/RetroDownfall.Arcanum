using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// Arrow / j-k navigation inside an overlay that is not the session picker (command palette, MCP
/// status, arsenal, …) walks that overlay's own rows. Bounding it by the session list instead makes
/// palette entries unreachable, freezes the selection, and assigns an index the bound source does
/// not have.
/// </summary>
public sealed class CommandCenterOverlayNavigationTests
{
    private static readonly string[] PaletteRows =
    [
        "New Session",
        "Open Sessions",
        "Refresh",
        "Model List",
        "Provider List",
        "MCP Status",
        "Arsenal",
        "Campaign List",
        "Spell List",
        "Ward List",
        "Doctor",
        "Mana",
        "Help",
        "Quit",
    ];

    [Fact]
    public void Palette_navigation_stays_inside_the_palette_rows_when_sessions_are_longer()
    {
        CommandCenterWindow window = new();
        window.ApplyAbsoluteLayout(120, 40);
        CommandCenterState state = new(new SessionLogBuffer()) { Sessions = BuildSessions(30) };
        window.ShowOverlay(CommandCenterOverlayKind.CommandPalette, PaletteRows, "Palette", showFilter: false);

        for (int i = 0; i < 25; i++)
        {
            window.MoveSessionSelection(1, state);
        }

        Assert.Equal(PaletteRows.Length - 1, window.GetOverlaySelectedIndex());
    }

    [Fact]
    public void Palette_navigation_works_with_no_sessions_loaded()
    {
        CommandCenterWindow window = new();
        window.ApplyAbsoluteLayout(120, 40);
        CommandCenterState state = new(new SessionLogBuffer());
        window.ShowOverlay(CommandCenterOverlayKind.CommandPalette, PaletteRows, "Palette", showFilter: false);

        window.MoveSessionSelection(1, state);
        window.MoveSessionSelection(1, state);

        Assert.Equal(2, window.GetOverlaySelectedIndex());
        Assert.Null(state.SelectedSessionId);
    }

    [Fact]
    public void Palette_navigation_does_not_change_the_selected_session()
    {
        CommandCenterWindow window = new();
        window.ApplyAbsoluteLayout(120, 40);
        IReadOnlyList<SessionListItem> sessions = BuildSessions(30);
        CommandCenterState state = new(new SessionLogBuffer())
        {
            Sessions = sessions,
            SelectedSessionId = sessions[0].Id,
        };
        window.ShowOverlay(CommandCenterOverlayKind.CommandPalette, PaletteRows, "Palette", showFilter: false);

        window.MoveSessionSelection(1, state);

        Assert.Equal(sessions[0].Id, state.SelectedSessionId);
    }

    [Fact]
    public void Sidebar_refresh_does_not_push_a_session_index_into_a_palette_overlay()
    {
        CommandCenterWindow window = new();
        window.ApplyAbsoluteLayout(120, 40);
        IReadOnlyList<SessionListItem> sessions = BuildSessions(30);
        CommandCenterState state = new(new SessionLogBuffer())
        {
            Sessions = sessions,
            SelectedSessionId = sessions[25].Id,
        };
        window.ApplyState(state);
        window.ShowOverlay(CommandCenterOverlayKind.CommandPalette, PaletteRows, "Palette", showFilter: false);

        window.ApplyState(state);

        Assert.True(window.GetOverlaySelectedIndex() < PaletteRows.Length);
    }

    [Fact]
    public void Session_picker_overlay_still_moves_the_session_selection()
    {
        CommandCenterWindow window = new();
        window.ApplyAbsoluteLayout(120, 40);
        IReadOnlyList<SessionListItem> sessions = BuildSessions(6);
        CommandCenterState state = new(new SessionLogBuffer()) { Sessions = sessions };
        window.ShowSessionPickerOverlay();
        window.ApplyState(state);

        window.MoveSessionSelection(2, state);

        Assert.Equal(sessions[2].Id, state.SelectedSessionId);
    }

    private static IReadOnlyList<SessionListItem> BuildSessions(int count)
    {
        List<SessionListItem> sessions = new(count);
        for (int i = 0; i < count; i++)
        {
            sessions.Add(new SessionListItem(Guid.NewGuid(), $"Session {i}", "active", DateTimeOffset.UtcNow, 3));
        }

        return sessions;
    }
}
