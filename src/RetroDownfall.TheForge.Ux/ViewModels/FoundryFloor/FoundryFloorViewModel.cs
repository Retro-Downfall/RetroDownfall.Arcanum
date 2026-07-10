using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

/// <summary>
/// The Foundry Floor collects cast/trial/tool output and streams Arcanum log lines while visible.
/// </summary>
public sealed partial class FoundryFloorViewModel : ViewModelBase, IDisposable
{

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

        Lines.Add(line);

        LatestLine = line;

        OnPropertyChanged(nameof(HasNoLines));

        OnPropertyChanged(nameof(OutputEmptyState));

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

}
