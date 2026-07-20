using CommunityToolkit.Mvvm.Input;
using RetroDownfall.TheForge.Ux.ViewModels;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Campaigns root branch. Exposes New Campaign via the shared coordinator; children are campaign nodes
/// supplied by a loader.
/// </summary>
public sealed class CampaignsRootNodeViewModel : AtelierNodeViewModel
{

    private readonly Func<CancellationToken, Task<IReadOnlyList<AtelierNodeViewModel>>> _loader;

    private readonly ICampaignCommandCoordinator _campaignCommands;

    private readonly Func<CancellationToken, Task> _afterCampaignsChanged;

    public CampaignsRootNodeViewModel(
        ICampaignCommandCoordinator campaignCommands,
        Func<CancellationToken, Task> afterCampaignsChanged,
        Func<CancellationToken, Task<IReadOnlyList<AtelierNodeViewModel>>>? loader = null)
    {

        _campaignCommands = campaignCommands;

        _afterCampaignsChanged = afterCampaignsChanged;

        _loader = loader ?? (static _ => Task.FromResult<IReadOnlyList<AtelierNodeViewModel>>([]));

        Label = "Campaigns";

        Icon = "IconCampaign";

        NewCampaignCommand = new AsyncRelayCommand(NewCampaignAsync);

    }

    public override IAsyncRelayCommand? NewCampaignCommand { get; }

    public override string? NewCampaignLabel => "New Campaign…";

    private async Task NewCampaignAsync(CancellationToken cancellationToken)
    {

        LastError = null;

        await _campaignCommands.NewCampaignAsync(cancellationToken).ConfigureAwait(true);

        await _afterCampaignsChanged(cancellationToken).ConfigureAwait(true);

    }

    protected override Task<IReadOnlyList<AtelierNodeViewModel>> LoadChildrenAsync(CancellationToken cancellationToken) =>
        _loader(cancellationToken);

    internal static string FormatCampaignError(string? code, string? message, string fallback)
    {

        if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(message))
        {

            return $"{code}: {message}";

        }

        if (!string.IsNullOrWhiteSpace(code))
        {

            return code;

        }

        return message ?? fallback;

    }

}
