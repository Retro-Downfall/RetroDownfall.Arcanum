using System.Drawing;

using RetroDownfall.Arcanum.Cli.CommandCenter;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// The overlay list has to show every row its frame was sized for. These facts lay the view tree out
/// the way the main loop does, because the rows the overlay holds and the rows the operator can see
/// are different things: the overlay can hold both models and still draw only one.
/// </summary>
public sealed class CommandCenterOverlayListGeometryTests
{
    /// <summary>
    /// A two-model drop-down was sized to a five-row frame (border, filter row, two list rows) and drew
    /// one model: the list sat one row down under the filter and then left another row empty at the
    /// bottom, so the second model was clipped below the frame.
    /// </summary>
    [Theory]
    [InlineData(120, 36)]
    [InlineData(80, 24)]
    internal void The_model_drop_down_draws_every_model_it_lists(int cols, int rows)
    {
        using var window = new CommandCenterWindow();

        var state = new CommandCenterState(new SessionLogBuffer())
        {
            ModelChoices = CommandCenterModelPicker.Build(
            [
                new ModelInfoDto("ornith1.5:35b", "Ollama", "OpenAICompatible", "***", 32_768),
                new ModelInfoDto("gemma4:e4b", "Ollama", "OpenAICompatible", "***", 32_768),
            ]),
            Model = "ornith1.5:35b",
            Overlay = CommandCenterOverlayKind.ModelPicker,
            FocusRegion = CommandCenterFocusRegion.Overlay,
        };

        window.ApplyAbsoluteLayout(cols, rows);

        window.ShowModelPickerOverlay(state);

        window.Layout(new Size(cols, rows));

        Assert.True(
            window.OverlayList.Viewport.Height >= 2,
            $"The model list shows {window.OverlayList.Viewport.Height} row(s) for two models at {cols}x{rows}.");

        Assert.Equal(0, window.OverlayList.Viewport.Y);

        Assert.Equal(
            CommandCenterModelPicker.Render(state.FilteredModels, state.Model),
            window.GetOverlayLinesSnapshot());

        Assert.Equal(2, window.GetOverlayLinesSnapshot().Count);

        Assert.True(
            window.OverlayList.Frame.Bottom <= window.OverlayPane.Viewport.Height,
            $"The model list ends at row {window.OverlayList.Frame.Bottom}, past the overlay's {window.OverlayPane.Viewport.Height} inner rows.");
    }

    /// <summary>
    /// The Ctrl+O picker opens before the host has handed it any sessions, so a frame measured only at
    /// open time was always the minimum five rows, however many sessions arrived a moment later.
    /// </summary>
    [Fact]
    internal void The_session_picker_grows_to_the_sessions_it_is_handed_after_it_opens()
    {
        using var window = new CommandCenterWindow();

        var state = new CommandCenterState(new SessionLogBuffer())
        {
            Sessions = Sessions,
            Overlay = CommandCenterOverlayKind.SessionPicker,
            FocusRegion = CommandCenterFocusRegion.Sessions,
        };

        window.ApplyAbsoluteLayout(80, 24);

        window.ShowSessionPickerOverlay();

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

        window.Layout(new Size(80, 24));

        Assert.Equal(3, window.GetOverlayLinesSnapshot().Count);

        Assert.True(
            window.OverlayList.Viewport.Height >= 3,
            $"The session list shows {window.OverlayList.Viewport.Height} row(s) for three sessions.");

        Assert.True(
            window.OverlayList.Frame.Bottom <= window.OverlayPane.Viewport.Height,
            $"The session list ends at row {window.OverlayList.Frame.Bottom}, past the overlay's {window.OverlayPane.Viewport.Height} inner rows.");
    }

    /// <summary>
    /// The same geometry applies to any list overlay opened with a filter row, so a filtered palette
    /// must not lose its last entry the way the drop-down lost its second model.
    /// </summary>
    [Fact]
    internal void A_filtered_list_overlay_draws_every_row_it_holds()
    {
        using var window = new CommandCenterWindow();

        window.ApplyAbsoluteLayout(80, 24);

        window.ShowOverlay(
            CommandCenterOverlayKind.CommandPalette,
            ["New Session", "Refresh", "Quit"],
            "Commands",
            showFilter: true);

        window.Layout(new Size(80, 24));

        Assert.Equal(1, window.OverlayList.Frame.Y);

        Assert.True(
            window.OverlayList.Viewport.Height >= 3,
            $"The filtered list shows {window.OverlayList.Viewport.Height} row(s) for three entries.");

        Assert.True(window.OverlayList.Frame.Bottom <= window.OverlayPane.Viewport.Height);
    }

    private static readonly SessionListItem[] Sessions =
    [
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "api rewrite",
            "Active",
            DateTimeOffset.UnixEpoch,
            2),
        new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "grimoire schema",
            "Active",
            DateTimeOffset.UnixEpoch,
            3),
        new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "ward tuning",
            "Active",
            DateTimeOffset.UnixEpoch,
            4),
    ];
}
