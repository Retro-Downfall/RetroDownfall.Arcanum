using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class ServerSectionViewModel : ObservableObject
{

    [ObservableProperty] private string _pidFilePath = string.Empty;

    private ServerSettings _snapshot = new();

    public void LoadFrom(ServerSettings settings)
    {

        _snapshot = settings;

        PidFilePath = settings.PidFilePath ?? string.Empty;

    }

    public ServerSettings Build() => _snapshot with
    {

        PidFilePath = string.IsNullOrWhiteSpace(PidFilePath) ? null : PidFilePath,

    };

}
