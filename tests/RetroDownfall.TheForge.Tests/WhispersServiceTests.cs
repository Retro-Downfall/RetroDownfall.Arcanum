using RetroDownfall.TheForge.Ux.Services.Whispers;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class WhispersServiceTests
{

    [Fact]
    public void Show_AddsNotification()
    {

        WhispersService service = NewService();

        service.Show(WhisperSeverity.Info, "Hello", "Title");

        Assert.Single(service.Notifications);

        WhisperNotification notification = service.Notifications[0];

        Assert.Equal(WhisperSeverity.Info, notification.Severity);

        Assert.Equal("Hello", notification.Message);

        Assert.Equal("Title", notification.Title);

        Assert.True(notification.AutoDismiss);

    }

    [Fact]
    public void Dismiss_RemovesMatchingNotification()
    {

        WhispersService service = NewService();

        service.Show(WhisperSeverity.Success, "Saved");

        Guid id = service.Notifications[0].Id;

        service.Dismiss(id);

        Assert.Empty(service.Notifications);

    }

    [Fact]
    public void Clear_RemovesAllNotifications()
    {

        WhispersService service = NewService();

        service.Show(WhisperSeverity.Info, "One");

        service.Show(WhisperSeverity.Warning, "Two");

        service.Clear();

        Assert.Empty(service.Notifications);

    }

    [Fact]
    public void Show_WhenAtCap_DropsOldestNonError()
    {

        FakeWhispersClock clock = new();

        DateTimeOffset baseTime = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

        clock.UtcNow = baseTime;

        WhispersService service = NewService(clock);

        service.Show(WhisperSeverity.Info, "info-1");

        clock.UtcNow = baseTime.AddSeconds(1);

        service.Show(WhisperSeverity.Success, "success-1");

        clock.UtcNow = baseTime.AddSeconds(2);

        service.Show(WhisperSeverity.Warning, "warning-1");

        clock.UtcNow = baseTime.AddSeconds(3);

        service.Show(WhisperSeverity.Info, "info-2");

        clock.UtcNow = baseTime.AddSeconds(4);

        service.Show(WhisperSeverity.Success, "success-2");

        Assert.Equal(IWhispersService.MaxActive, service.Notifications.Count);

        clock.UtcNow = baseTime.AddSeconds(5);

        service.Show(WhisperSeverity.Error, "error-1");

        Assert.Equal(IWhispersService.MaxActive, service.Notifications.Count);

        Assert.DoesNotContain(service.Notifications, n => n.Message == "info-1");

        Assert.Contains(service.Notifications, n => n.Message == "error-1");

        Assert.Contains(service.Notifications, n => n.Message == "success-2");

    }

    [Fact]
    public void Show_WhenAtCapWithOnlyErrors_DropsOldestOverall()
    {

        FakeWhispersClock clock = new();

        DateTimeOffset baseTime = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

        clock.UtcNow = baseTime;

        WhispersService service = NewService(clock);

        for (int i = 0; i < IWhispersService.MaxActive; i++)
        {

            clock.UtcNow = baseTime.AddSeconds(i);

            service.Show(WhisperSeverity.Error, $"error-{i}");

        }

        clock.UtcNow = baseTime.AddSeconds(IWhispersService.MaxActive);

        service.Show(WhisperSeverity.Error, "error-new");

        Assert.Equal(IWhispersService.MaxActive, service.Notifications.Count);

        Assert.DoesNotContain(service.Notifications, n => n.Message == "error-0");

        Assert.Contains(service.Notifications, n => n.Message == "error-new");

    }

    [Fact]
    public void ExpireDue_RemovesAutoDismissNotificationsAfterDelay()
    {

        FakeWhispersClock clock = new();

        DateTimeOffset baseTime = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

        clock.UtcNow = baseTime;

        WhispersService service = NewService(clock);

        service.Show(WhisperSeverity.Info, "Transient");

        service.Show(WhisperSeverity.Error, "Persistent");

        clock.UtcNow = baseTime.AddSeconds(4);

        service.ExpireDue();

        Assert.Equal(2, service.Notifications.Count);

        clock.UtcNow = baseTime.AddSeconds(5);

        service.ExpireDue();

        Assert.Single(service.Notifications);

        Assert.Equal(WhisperSeverity.Error, service.Notifications[0].Severity);

        Assert.Equal("Persistent", service.Notifications[0].Message);

    }

    [Fact]
    public void Show_Error_DoesNotAutoDismiss()
    {

        WhispersService service = NewService();

        service.Show(WhisperSeverity.Error, "Failed");

        Assert.False(service.Notifications[0].AutoDismiss);

    }

    private static WhispersService NewService(FakeWhispersClock? clock = null) =>
        new(clock ?? new FakeWhispersClock(), new SynchronousUiThreadDispatcher());

    private sealed class FakeWhispersClock : IWhispersClock
    {

        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

    }

    private sealed class SynchronousUiThreadDispatcher : IUiThreadDispatcher
    {

        public void Post(Action action) => action();

    }

}
