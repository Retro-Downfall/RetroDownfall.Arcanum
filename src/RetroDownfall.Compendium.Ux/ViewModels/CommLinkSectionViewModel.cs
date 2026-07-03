using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class CommLinkSectionViewModel : ObservableObject
{

    [ObservableProperty] private string _webhookUrl = string.Empty;

    [ObservableProperty] private int _webhookTimeoutSeconds;

    [ObservableProperty] private string _allowedSchemes = string.Empty;

    [ObservableProperty] private string _allowedHosts = string.Empty;

    private CommLinkSettings _snapshot = new();

    public void LoadFrom(CommLinkSettings settings)
    {

        _snapshot = settings;

        WebhookUrl = settings.WebhookUrl ?? string.Empty;

        WebhookTimeoutSeconds = settings.WebhookTimeoutSeconds;

        AllowedSchemes = settings.AllowedSchemes.JoinCsv();

        AllowedHosts = settings.AllowedHosts.JoinCsv();

    }

    public CommLinkSettings Build() => _snapshot with
    {

        WebhookUrl = string.IsNullOrWhiteSpace(WebhookUrl) ? null : WebhookUrl,

        WebhookTimeoutSeconds = WebhookTimeoutSeconds,

        AllowedSchemes = AllowedSchemes.SplitCsv(),

        AllowedHosts = AllowedHosts.SplitCsv(),

    };

}
