using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

/// <summary>
/// Scrying — vision/multimodality image gate. Bound from <c>Arcanum:Scrying</c>.
/// </summary>
public sealed partial class ScryingSectionViewModel : ObservableObject
{

    [ObservableProperty] private bool _enabled;

    [ObservableProperty] private long _maxImageBytes;

    [ObservableProperty] private int _maxImagesPerRequest;

    [ObservableProperty] private string _allowedMimeTypes = string.Empty;

    private ScryingSettings _snapshot = new();

    public void LoadFrom(ScryingSettings settings)
    {

        _snapshot = settings;

        Enabled = settings.Enabled;

        MaxImageBytes = settings.MaxImageBytes;

        MaxImagesPerRequest = settings.MaxImagesPerRequest;

        AllowedMimeTypes = settings.AllowedMimeTypes.JoinCsv();

    }

    public ScryingSettings Build() => _snapshot with
    {

        Enabled = Enabled,

        MaxImageBytes = MaxImageBytes,

        MaxImagesPerRequest = MaxImagesPerRequest,

        AllowedMimeTypes = AllowedMimeTypes.SplitCsv(),

    };

}
