using System.Collections.ObjectModel;

namespace RetroDownfall.TheForge.Ux.Services.Whispers;

public interface IWhispersService
{

    const int MaxActive = 5;

    ObservableCollection<WhisperNotification> Notifications { get; }

    void Show(WhisperSeverity severity, string message, string? title = null);

    void Dismiss(Guid id);

    void Clear();

    void ExpireDue();

}
