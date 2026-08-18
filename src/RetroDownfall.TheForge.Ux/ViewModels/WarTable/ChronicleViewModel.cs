using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.TheForge.Core.Chronicle;

namespace RetroDownfall.TheForge.Ux.ViewModels.WarTable;

/// <summary>
/// Live Chronicle stream for one apprentice. Owns a CTS started on activation and cancelled on
/// deactivation. Frames are The Forge-local <see cref="ChronicleFrame"/> records (raw-string Type).
/// </summary>
public sealed partial class ChronicleViewModel : ObservableObject, IDisposable
{

    /// <summary>Retention bound for <see cref="Entries"/>; newest frames win and the tail is dropped.</summary>
    public const int MaxEntries = 2000;

    private readonly Guid _apprenticeId;

    private readonly IWarTableDataSource _dataSource;

    private CancellationTokenSource? _streamCts;

    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(EmptyStateText))]
    [NotifyPropertyChangedFor(nameof(ShowStreamingIndicator))]
    private bool _isStreaming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowErrorBanner))]
    private string? _lastError;

    public ChronicleViewModel(Guid apprenticeId, IWarTableDataSource dataSource)
    {

        _apprenticeId = apprenticeId;

        _dataSource = dataSource;

        Entries.CollectionChanged += OnEntriesChanged;

    }

    public ObservableCollection<ChronicleEntryViewModel> Entries { get; } = [];

    public bool ShowErrorBanner => !string.IsNullOrWhiteSpace(LastError);

    public bool ShowStreamingIndicator => IsStreaming;

    public bool ShowEmptyState => Entries.Count == 0 && string.IsNullOrWhiteSpace(LastError);

    public string EmptyStateText => IsStreaming
        ? "Listening for chronicle frames…"
        : "Chronicle stream ended.";

    public void Start()
    {

        Stop();

        // The server replays plan/escalation/step frames on every reconnect, so a re-activation that
        // kept the previous entries would prepend a second copy of the same preamble.
        Entries.Clear();

        _streamCts = new CancellationTokenSource();

        IsStreaming = true;

        LastError = null;

        _ = RunAsync(_streamCts.Token);

    }

    public void Stop()
    {

        if (_streamCts is null)
        {

            return;

        }

        _streamCts.Cancel();

        _streamCts.Dispose();

        _streamCts = null;

        IsStreaming = false;

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        Entries.CollectionChanged -= OnEntriesChanged;

        Stop();

        GC.SuppressFinalize(this);

    }

    private void EnforceEntryCap()
    {

        while (Entries.Count > MaxEntries)
        {

            Entries.RemoveAt(Entries.Count - 1);

        }

    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {

        OnPropertyChanged(nameof(ShowEmptyState));

        OnPropertyChanged(nameof(EmptyStateText));

    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {

        try
        {

            await foreach (ChronicleFrame frame in _dataSource.StreamChronicleAsync(_apprenticeId, cancellationToken)
                               .ConfigureAwait(true))
            {

                if (cancellationToken.IsCancellationRequested)
                {

                    return;

                }

                Entries.Insert(0, new ChronicleEntryViewModel(frame));

                EnforceEntryCap();

            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            // Expected on deactivation.

        }
        catch (Exception ex)
        {

            // A cancelled stream can surface its abort as an IO failure; a superseded run must not
            // raise an error banner on the run that replaced it.
            if (!cancellationToken.IsCancellationRequested)
            {

                LastError = ex.Message;

            }

        }
        finally
        {

            if (!cancellationToken.IsCancellationRequested)
            {

                IsStreaming = false;

            }

        }

    }

}
