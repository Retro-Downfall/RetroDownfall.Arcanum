using System.Drawing;

using RetroDownfall.Arcanum.Cli.CommandCenter;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterPickerSelectionTests
{
    [Theory]
    [InlineData(0, "beta")]
    [InlineData(-1, "alpha")]
    [InlineData(1, "gamma")]
    [InlineData(-99, "alpha")]
    [InlineData(99, "gamma")]
    public void The_model_cursor_follows_the_Enter_target_while_the_filter_keeps_focus(int delta, string expected)
    {
        using CommandCenterWindow window = new();

        CommandCenterState state = ModelState();

        window.ShowModelPickerOverlay(state);

        window.MoveModelSelection(delta, state);

        Assert.True(window.OverlayFilter.HasFocus);

        Assert.Contains(expected, CursorRow(window), StringComparison.Ordinal);

        Assert.Equal(expected, CommandCenterModelPicker.Resolve(state.FilteredModels, state.SelectedModelIndex));

        Assert.Equal("beta", state.Model);

        Assert.Contains(window.GetOverlayLinesSnapshot(), static row => row.Contains("● beta", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("alp", "alpha")]
    [InlineData("absent", null)]
    public void Filtering_models_keeps_the_cursor_on_a_real_Enter_target(string filter, string? expected)
    {
        using CommandCenterWindow window = new();

        CommandCenterState state = ModelState();

        window.ShowModelPickerOverlay(state);

        window.MoveModelSelection(1, state);

        window.OverlayFilter.Text = filter;

        CommandCenterHost.ApplyOverlayFilterText(state, window, static action => action());

        if (expected is null)
        {
            Assert.DoesNotContain(window.GetOverlayLinesSnapshot(), static row => row.StartsWith("> ", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(expected, CursorRow(window), StringComparison.Ordinal);
        }

        Assert.Equal(expected, CommandCenterModelPicker.Resolve(state.FilteredModels, state.SelectedModelIndex));
    }

    [Theory]
    [InlineData(false, 0, "New Session")]
    [InlineData(false, 1, "Choose Model")]
    [InlineData(true, 0, "/help")]
    [InlineData(true, 1, "/keys")]
    public void Both_command_pickers_use_the_same_cursor_for_the_Enter_target(bool slash, int delta, string expected)
    {
        using CommandCenterWindow window = new();

        CommandCenterState state = new(new SessionLogBuffer())
        {
            Overlay = CommandCenterOverlayKind.CommandPalette,
            PaletteMode = slash ? CommandPaletteMode.Slash : CommandPaletteMode.Actions,
            PaletteFilter = slash ? "/" : string.Empty,
        };

        window.ShowPaletteOverlay(state);

        window.MovePaletteSelection(delta);

        Assert.True(window.OverlayFilter.HasFocus);

        Assert.Contains(expected, CursorRow(window), StringComparison.Ordinal);

        Assert.Equal(expected, CommandCenterHost.SelectedPaletteEntry(state, window)?.Title);

        window.OverlayFilter.Text = slash ? "/not-a-command" : "not-a-command";

        CommandCenterHost.ApplyOverlayFilterText(state, window, static action => action());

        window.MovePaletteSelection(1);

        Assert.DoesNotContain(window.GetOverlayLinesSnapshot(), static row => row.StartsWith("> ", StringComparison.Ordinal));

        Assert.Null(CommandCenterHost.SelectedPaletteEntry(state, window));
    }

    [Fact]
    public void A_raw_command_palette_uses_the_same_cursor()
    {
        using CommandCenterWindow window = new();

        window.ShowOverlay(CommandCenterOverlayKind.CommandPalette, ["First", "Second"], "Commands", showFilter: true);

        window.MovePaletteSelection(1);

        Assert.Equal("> Second", CursorRow(window));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sessions_distinguish_the_active_session_from_the_single_Enter_target(bool overlay)
    {
        using CommandCenterWindow window = new();

        CommandCenterState state = SessionState();

        if (overlay)
        {
            window.ShowSessionPickerOverlay();
        }

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

        window.MoveSessionSelection(1, state);

        IReadOnlyList<string> rows = overlay
            ? window.GetOverlayLinesSnapshot()
            : window.SessionsView.Source!.ToList().Cast<string>().ToArray();

        Assert.Contains("beta session", Assert.Single(rows, static row => row.StartsWith("> ", StringComparison.Ordinal)), StringComparison.Ordinal);

        Assert.Contains(rows, static row => row.Contains("● alpha session", StringComparison.Ordinal));

        Assert.Equal(state.Sessions[0].Id, state.SessionId);

        Assert.Equal(state.Sessions[1].Id, window.GetSelectedSessionId(state));
    }

    [Fact]
    public void Filtering_out_the_pending_session_marks_the_visible_fallback_Enter_will_resume()
    {
        using CommandCenterWindow window = new();

        CommandCenterState state = SessionState();

        window.ShowSessionPickerOverlay();

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

        window.MoveSessionSelection(1, state);

        state.SessionFilter = "alpha";

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

        Assert.Contains("alpha session", CursorRow(window), StringComparison.Ordinal);

        Assert.Equal(state.Sessions[0].Id, window.GetSelectedSessionId(state));
    }

    [Fact]
    public void The_session_cursor_stays_visible_after_the_sidebar_rows_are_rebuilt()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(120, 24);

        SessionListItem[] sessions = Enumerable.Range(1, 40)
            .Select(static i => new SessionListItem(new Guid(i, 0, 0, new byte[8]), $"session {i}", "Active", DateTimeOffset.UnixEpoch, 1))
            .ToArray();

        CommandCenterState state = new(new SessionLogBuffer())
        {
            Sessions = sessions,
            SessionId = sessions[0].Id,
            SelectedSessionId = sessions[0].Id,
        };

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

        window.Layout(new Size(120, 24));

        window.MoveSessionSelection(100, state);

        Assert.Equal(39, window.SessionsView.SelectedItem);

        Assert.True(window.SessionsView.Viewport.Y > 0);

        Assert.InRange(39, window.SessionsView.Viewport.Y, window.SessionsView.Viewport.Bottom - 1);

        Assert.Equal(sessions[39].Id, window.GetSelectedSessionId(state));
    }

    private static string CursorRow(CommandCenterWindow window) =>
        Assert.Single(window.GetOverlayLinesSnapshot(), static row => row.StartsWith("> ", StringComparison.Ordinal));

    private static CommandCenterState ModelState() => new(new SessionLogBuffer())
    {
        Model = "beta",
        ModelChoices = CommandCenterModelPicker.Build(
        [
            new ModelInfoDto("alpha", "local", "OpenAICompatible", "***", 8_192),
            new ModelInfoDto("beta", "local", "OpenAICompatible", "***", 8_192),
            new ModelInfoDto("gamma", "local", "OpenAICompatible", "***", 8_192),
        ]),
        Overlay = CommandCenterOverlayKind.ModelPicker,
    };

    private static CommandCenterState SessionState()
    {
        SessionListItem[] sessions =
        [
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "alpha session", "Active", DateTimeOffset.UnixEpoch, 1),
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "beta session", "Active", DateTimeOffset.UnixEpoch, 1),
        ];

        return new CommandCenterState(new SessionLogBuffer())
        {
            Sessions = sessions,
            SessionId = sessions[0].Id,
            SelectedSessionId = sessions[0].Id,
            Overlay = CommandCenterOverlayKind.SessionPicker,
        };
    }
}
