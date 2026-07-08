using CommunityToolkit.Mvvm.ComponentModel;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>Expandable tool-call card rendered in The Tome transcript.</summary>
public sealed partial class ToolCallCardViewModel : ObservableObject
{

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _argumentsJson = string.Empty;

    [ObservableProperty]
    private string? _result;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorMessage;

    public ToolCallCardViewModel(string callId, string name, string argumentsJson)
    {

        CallId = callId;

        Name = name;

        ArgumentsJson = argumentsJson;

    }

    public string CallId { get; }

}
