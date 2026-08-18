using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

/// <summary>
/// The Foundry Floor collects cast/trial/tool output and streams Arcanum log lines while visible.
/// <see cref="Lines"/> is a bounded live buffer: the log SSE tail runs for as long as the panel is
/// shown, so it is capped at <see cref="MaxLines"/> the way The Hearth caps its own scrollback.
/// </summary>
public sealed partial class FoundryFloorViewModel : ViewModelBase, IDisposable
{

    /// <summary>Retention bound for <see cref="Lines"/>; matches <c>HearthViewModel.MaxLines</c>.</summary>
    public const int MaxLines = 5000;

    private readonly ILogService _logService;

    private readonly ILogger<FoundryFloorViewModel>? _logger;

    private CancellationTokenSource? _streamCts;

    private bool _disposed;

    [ObservableProperty]
    private string _latestLine = string.Empty;

    [ObservableProperty]
    private bool _isVisible;

    public FoundryFloorViewModel(ILogService logService, ILogger<FoundryFloorViewModel>? logger = null)
    {

        _logService = logService;

        _logger = logger;

        Title = "The Foundry Floor";

    }

    public ObservableCollection<string> Lines { get; } = [];

    public bool HasNoLines => Lines.Count == 0;

    public string OutputEmptyState => HasNoLines
        ? "Output from casts, trials, and tools will collect here."
        : string.Empty;

    public string LogsEmptyState => "Arcanum logs will stream here when connected.";

    public void AppendLine(string line)
    {

        void Apply()
        {

            bool wasEmpty = Lines.Count == 0;

            Lines.Add(line);

            EnforceLineCap();

            LatestLine = line;

            if (wasEmpty)
            {

                OnPropertyChanged(nameof(HasNoLines));

                OnPropertyChanged(nameof(OutputEmptyState));

            }

        }

        Marshal(Apply);

    }

    /// <summary>Empties the buffer; the only operator-facing trim the Foundry Floor has.</summary>
    [RelayCommand]
    public void Clear()
    {

        void Apply()
        {

            if (Lines.Count == 0 && LatestLine.Length == 0)
            {

                return;

            }

            Lines.Clear();

            LatestLine = string.Empty;

            OnPropertyChanged(nameof(HasNoLines));

            OnPropertyChanged(nameof(OutputEmptyState));

        }

        Marshal(Apply);

    }

    partial void OnIsVisibleChanged(bool value)
    {

        if (value)
        {

            StartLogStream();

        }
        else
        {

            StopLogStream();

        }

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        StopLogStream();

        GC.SuppressFinalize(this);

    }

    private void StartLogStream()
    {

        StopLogStream();

        _streamCts = new CancellationTokenSource();

        TaskUtilities.FireAndForget(StreamLogsAsync(_streamCts.Token), _logger);

    }

    private void StopLogStream()
    {

        if (_streamCts is null)
        {

            return;

        }

        _streamCts.Cancel();

        _streamCts.Dispose();

        _streamCts = null;

    }

    private async Task StreamLogsAsync(CancellationToken cancellationToken)
    {

        try
        {

            await foreach (LogEntry entry in _logService.StreamLogsAsync(cancellationToken).ConfigureAwait(true))
            {

                string line = $"[{entry.Timestamp:HH:mm:ss}] {entry.Level} {entry.Category}: {entry.Message}";

                AppendLine(line);

            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            // Visibility gated stop.

        }
        catch (Exception ex)
        {

            _logger?.LogWarning(ex, "Foundry Floor log stream ended with an error.");

            AppendLine($"Log stream error: {ex.Message}");

        }

    }

    private void EnforceLineCap()
    {

        while (Lines.Count > MaxLines)
        {

            Lines.RemoveAt(0);

        }

    }

    /// <summary>
    /// Runs <paramref name="apply"/> on the UI thread when one exists. <see cref="Lines"/> is bound, and
    /// callers may append from a thread-pool continuation, so the mutation cannot assume it is already
    /// there. Falls through synchronously in headless/unit-test hosts where Avalonia is not running.
    /// </summary>
    private static void Marshal(Action apply)
    {

        if (TryGetUiDispatcher(out Dispatcher? dispatcher) && dispatcher is not null && !dispatcher.CheckAccess())
        {

            dispatcher.Post(apply, DispatcherPriority.Background);

            return;

        }

        apply();

    }

    private static bool TryGetUiDispatcher(out Dispatcher? dispatcher)
    {

        try
        {

            if (Avalonia.Application.Current is null)
            {

                dispatcher = null;

                return false;

            }

            dispatcher = Dispatcher.UIThread;

            return true;

        }
        catch (Exception)
        {

            dispatcher = null;

            return false;

        }

    }

}
