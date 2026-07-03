using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class CliSectionViewModel : ObservableObject
{

    [ObservableProperty] private long _maxAttachFileSizeBytes;

    [ObservableProperty] private int _maxAttachedFilesPerRequest;

    [ObservableProperty] private int _maxAttachedFileRelativePathChars;

    [ObservableProperty] private ArcanumTheme _theme;

    [ObservableProperty] private bool _showManaBar;

    [ObservableProperty] private int _doctorHealthTimeoutSeconds;

    [ObservableProperty] private int _apiRequestTimeoutSeconds;

    [ObservableProperty] private string _lightText = string.Empty;

    [ObservableProperty] private string _lightHeading = string.Empty;

    [ObservableProperty] private string _lightHighlight = string.Empty;

    [ObservableProperty] private string _lightError = string.Empty;

    [ObservableProperty] private string _lightMuted = string.Empty;

    [ObservableProperty] private string _darkText = string.Empty;

    [ObservableProperty] private string _darkHeading = string.Empty;

    [ObservableProperty] private string _darkHighlight = string.Empty;

    [ObservableProperty] private string _darkError = string.Empty;

    [ObservableProperty] private string _darkMuted = string.Empty;

    private CliSettings _snapshot = new();

    public void LoadFrom(CliSettings settings)
    {

        _snapshot = settings;

        MaxAttachFileSizeBytes = settings.MaxAttachFileSizeBytes;

        MaxAttachedFilesPerRequest = settings.MaxAttachedFilesPerRequest;

        MaxAttachedFileRelativePathChars = settings.MaxAttachedFileRelativePathChars;

        Theme = settings.Theme;

        ShowManaBar = settings.ShowManaBar;

        DoctorHealthTimeoutSeconds = settings.DoctorHealthTimeoutSeconds;

        ApiRequestTimeoutSeconds = settings.ApiRequestTimeoutSeconds;

        LightText = settings.ThemeColors.Light.Text;

        LightHeading = settings.ThemeColors.Light.Heading;

        LightHighlight = settings.ThemeColors.Light.Highlight;

        LightError = settings.ThemeColors.Light.Error;

        LightMuted = settings.ThemeColors.Light.Muted;

        DarkText = settings.ThemeColors.Dark.Text;

        DarkHeading = settings.ThemeColors.Dark.Heading;

        DarkHighlight = settings.ThemeColors.Dark.Highlight;

        DarkError = settings.ThemeColors.Dark.Error;

        DarkMuted = settings.ThemeColors.Dark.Muted;

    }

    public CliSettings Build() => _snapshot with
    {

        MaxAttachFileSizeBytes = MaxAttachFileSizeBytes,

        MaxAttachedFilesPerRequest = MaxAttachedFilesPerRequest,

        MaxAttachedFileRelativePathChars = MaxAttachedFileRelativePathChars,

        Theme = Theme,

        ShowManaBar = ShowManaBar,

        DoctorHealthTimeoutSeconds = DoctorHealthTimeoutSeconds,

        ApiRequestTimeoutSeconds = ApiRequestTimeoutSeconds,

        ThemeColors = new ThemeColors
        {

            Light = new ThemeSemanticColors
            {

                Text = LightText,

                Heading = LightHeading,

                Highlight = LightHighlight,

                Error = LightError,

                Muted = LightMuted,

            },

            Dark = new ThemeSemanticColors
            {

                Text = DarkText,

                Heading = DarkHeading,

                Highlight = DarkHighlight,

                Error = DarkError,

                Muted = DarkMuted,

            },

        },

    };

}
