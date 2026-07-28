using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class SecuritySectionViewModel : ObservableObject
{

    [ObservableProperty] private int _maxApiKeyHeaderUtf16Chars;

    [ObservableProperty] private int _apiKeyCacheTtlSeconds;

    [ObservableProperty] private bool _wardEnabled;

    [ObservableProperty] private string _forbiddenArts = string.Empty;

    [ObservableProperty] private int _wardTimeoutSeconds;

    [ObservableProperty] private int _wardMaxActiveWards;

    [ObservableProperty] private bool _wardAutoDenyInUnattendedMode;

    [ObservableProperty] private bool _wardUnattendedMode;

    private SecuritySettings _securitySnapshot = new();

    private WardSettings _wardSnapshot = new();

    public void LoadFrom(SecuritySettings security, WardSettings ward)
    {

        _securitySnapshot = security;

        _wardSnapshot = ward;

        MaxApiKeyHeaderUtf16Chars = security.MaxApiKeyHeaderUtf16Chars;

        ApiKeyCacheTtlSeconds = security.ApiKeyCacheTtlSeconds;

        WardEnabled = ward.Enabled;

        ForbiddenArts = ward.ForbiddenArts.JoinCsv();

        WardTimeoutSeconds = ward.TimeoutSeconds;

        WardMaxActiveWards = ward.MaxActiveWards;

        WardAutoDenyInUnattendedMode = ward.AutoDenyInUnattendedMode;

        WardUnattendedMode = ward.UnattendedMode;

    }

    public SecuritySettings BuildSecurity() => _securitySnapshot with
    {

        MaxApiKeyHeaderUtf16Chars = MaxApiKeyHeaderUtf16Chars,

        ApiKeyCacheTtlSeconds = ApiKeyCacheTtlSeconds,

    };

    public WardSettings BuildWard() => _wardSnapshot with
    {

        Enabled = WardEnabled,

        ForbiddenArts = ForbiddenArts.SplitCsv(),

        TimeoutSeconds = WardTimeoutSeconds,

        MaxActiveWards = WardMaxActiveWards,

        AutoDenyInUnattendedMode = WardAutoDenyInUnattendedMode,

        UnattendedMode = WardUnattendedMode,

    };

}
