using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class CliSectionViewModel : ObservableObject
{

    [ObservableProperty] private ArcanumTheme _theme;

    [ObservableProperty] private bool _showManaBar;

    private CliSettings _snapshot = new();

    public void LoadFrom(CliSettings settings)
    {

        _snapshot = settings;

        Theme = settings.Theme;

        ShowManaBar = settings.ShowManaBar;

    }

    public CliSettings Build() => _snapshot with
    {
        Theme = Theme,
        ShowManaBar = ShowManaBar,
    };

}
