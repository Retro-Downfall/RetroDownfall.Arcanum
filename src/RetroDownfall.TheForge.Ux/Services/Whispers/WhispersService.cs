using System.Collections.ObjectModel;

namespace RetroDownfall.TheForge.Ux.Services.Whispers;

public sealed class WhispersService : IWhispersService, IDisposable
{

    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often <see cref="ExpireDue"/> is swept while auto-dismissing toasts are on screen. The service
    /// owns this itself: no view drives expiry, so without it AutoDismiss is dead and every toast sits
    /// there until the operator clicks its close button or the cap evicts it.
    /// </summary>
    private static readonly TimeSpan ExpirySweepInterval = TimeSpan.FromSeconds(1);

    private readonly IWhispersClock _clock;

    private readonly IUiThreadDispatcher _dispatcher;

    private readonly object _sweepGate = new();

    private Timer? _sweepTimer;

    private bool _disposed;

    public ObservableCollection<WhisperNotification> Notifications { get; } = [];

    public WhispersService(IWhispersClock clock, IUiThreadDispatcher dispatcher)
    {

        _clock = clock;

        _dispatcher = dispatcher;

    }

    public void Show(WhisperSeverity severity, string message, string? title = null)
    {

        WhisperNotification notification = new(
            Guid.NewGuid(),
            severity,
            message,
            title,
            _clock.UtcNow,
            severity != WhisperSeverity.Error);

        _dispatcher.Post(() =>
        {

            EnforceCapBeforeAdd();

            Notifications.Add(notification);

        });

        if (notification.AutoDismiss)
        {

            StartSweep();

        }

    }

    public void Dismiss(Guid id)
    {

        _dispatcher.Post(() =>
        {

            WhisperNotification? item = FindById(id);

            if (item is not null)
            {

                Notifications.Remove(item);

            }

        });

    }

    public void Clear()
    {

        _dispatcher.Post(Notifications.Clear);

    }

    public void ExpireDue()
    {

        _dispatcher.Post(() =>
        {

            DateTimeOffset now = _clock.UtcNow;

            List<WhisperNotification> due = Notifications
                .Where(n => n.AutoDismiss && now - n.CreatedAtUtc >= AutoDismissDelay)
                .ToList();

            foreach (WhisperNotification item in due)
            {

                Notifications.Remove(item);

            }

            if (!Notifications.Any(n => n.AutoDismiss))
            {

                StopSweep();

            }

        });

    }

    private void StartSweep()
    {

        lock (_sweepGate)
        {

            if (_disposed)
            {

                return;

            }

            if (_sweepTimer is null)
            {

                _sweepTimer = new Timer(_ => ExpireDue(), null, ExpirySweepInterval, ExpirySweepInterval);

                return;

            }

            _sweepTimer.Change(ExpirySweepInterval, ExpirySweepInterval);

        }

    }

    private void StopSweep()
    {

        lock (_sweepGate)
        {

            _sweepTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        }

    }

    public void Dispose()
    {

        lock (_sweepGate)
        {

            _disposed = true;

            _sweepTimer?.Dispose();

            _sweepTimer = null;

        }

    }

    private void EnforceCapBeforeAdd()
    {

        while (Notifications.Count >= IWhispersService.MaxActive)
        {

            WhisperNotification? toRemove = Notifications
                .Where(n => n.Severity != WhisperSeverity.Error)
                .OrderBy(n => n.CreatedAtUtc)
                .FirstOrDefault();

            if (toRemove is null)
            {

                toRemove = Notifications.OrderBy(n => n.CreatedAtUtc).First();

            }

            Notifications.Remove(toRemove);

        }

    }

    private WhisperNotification? FindById(Guid id)
    {

        foreach (WhisperNotification item in Notifications)
        {

            if (item.Id == id)
            {

                return item;

            }

        }

        return null;

    }

}
