using CommunityToolkit.Mvvm.ComponentModel;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>One transcript bubble, status notice, or tool card host in The Tome.</summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{

    [ObservableProperty]
    private string _content = string.Empty;

    public ChatMessageViewModel(string role, string content, ToolCallCardViewModel? toolCall = null)
    {

        Role = role;

        Content = content;

        ToolCall = toolCall;

    }

    public string Role { get; }

    public ToolCallCardViewModel? ToolCall { get; }

    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);

    public bool IsAssistant => string.Equals(Role, "assistant", StringComparison.OrdinalIgnoreCase);

    public bool IsSystem => string.Equals(Role, "system", StringComparison.OrdinalIgnoreCase);

    public bool IsTool => string.Equals(Role, "tool", StringComparison.OrdinalIgnoreCase) || ToolCall is not null;

    public bool IsStatus => string.Equals(Role, "status", StringComparison.OrdinalIgnoreCase);

    public bool IsError => string.Equals(Role, "error", StringComparison.OrdinalIgnoreCase);

    public void AppendContent(string data) => Content += data;

}
