using CommunityToolkit.Mvvm.ComponentModel;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>One transcript bubble, status notice, or tool card host in The Tome.</summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private bool _isPinned;

    public ChatMessageViewModel(
        string role,
        string content,
        ToolCallCardViewModel? toolCall = null,
        Guid? entryId = null,
        bool isPinned = false)
    {

        Role = role;

        Content = content;

        ToolCall = toolCall;

        EntryId = entryId;

        IsPinned = isPinned;

    }

    public string Role { get; }

    private Guid? _entryId;

    public Guid? EntryId
    {

        get => _entryId;

        set
        {

            if (_entryId == value)
            {

                return;

            }

            _entryId = value;

            OnPropertyChanged();

            OnPropertyChanged(nameof(HasEntryId));

            OnPropertyChanged(nameof(CanPin));

            OnPropertyChanged(nameof(CanUnpin));

        }

    }

    public ToolCallCardViewModel? ToolCall { get; }

    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);

    public bool IsAssistant => string.Equals(Role, "assistant", StringComparison.OrdinalIgnoreCase);

    public bool IsSystem => string.Equals(Role, "system", StringComparison.OrdinalIgnoreCase);

    public bool IsTool => string.Equals(Role, "tool", StringComparison.OrdinalIgnoreCase) || ToolCall is not null;

    public bool IsStatus => string.Equals(Role, "status", StringComparison.OrdinalIgnoreCase);

    public bool IsError => string.Equals(Role, "error", StringComparison.OrdinalIgnoreCase);

    public bool HasEntryId => EntryId.HasValue;

    public bool CanPin => HasEntryId && !IsPinned;

    public bool CanUnpin => HasEntryId && IsPinned;

    public void AppendContent(string data) => Content += data;

    partial void OnIsPinnedChanged(bool value)
    {

        OnPropertyChanged(nameof(CanPin));

        OnPropertyChanged(nameof(CanUnpin));

    }

}
