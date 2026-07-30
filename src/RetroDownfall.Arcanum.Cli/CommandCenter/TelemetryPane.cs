using Terminal.Gui;
using Terminal.Gui.Views;

using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Non-focusable telemetry pane displaying real-time session aggregates
/// from the TelemetryService / IModelCallExecutor event stream.
/// </summary>
public sealed class TelemetryPane
{
    public bool CanFocus => false;
    public object? Width => null;
    public int Height => 10;

    public TelemetryPane() { }

    public void UpdateMetrics(TelemetrySnapshot snapshot)
    {
        // Debounced rendering handled by host
    }
}
