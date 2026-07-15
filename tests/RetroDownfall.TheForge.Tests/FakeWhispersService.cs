using System.Collections.ObjectModel;
using RetroDownfall.TheForge.Ux.Services.Whispers;

namespace RetroDownfall.TheForge.Tests;

internal sealed class FakeWhispersService : IWhispersService
{

    public ObservableCollection<WhisperNotification> Notifications { get; } = [];

    public List<(WhisperSeverity Severity, string Message, string? Title)> Calls { get; } = [];

    public void Show(WhisperSeverity severity, string message, string? title = null) =>
        Calls.Add((severity, message, title));

    public void Dismiss(Guid id)
    {
    }

    public void Clear()
    {
    }

    public void ExpireDue()
    {
    }

}
