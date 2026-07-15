using System.Collections.ObjectModel;

namespace RetroDownfall.TheForge.Ux.Services.Whispers;

public sealed class WhispersService : IWhispersService
{

    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(5);

    private readonly IWhispersClock _clock;

    private readonly IUiThreadDispatcher _dispatcher;

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

        });

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
