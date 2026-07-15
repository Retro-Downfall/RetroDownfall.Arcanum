using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Campaigns root branch. Exposes New Campaign; children are campaign nodes supplied by a loader.
/// </summary>
public sealed class CampaignsRootNodeViewModel : AtelierNodeViewModel
{

    private readonly Func<CancellationToken, Task<IReadOnlyList<AtelierNodeViewModel>>> _loader;

    private readonly ICampaignManagementDataSource _management;

    private readonly ICampaignDialogService _campaignDialog;

    private readonly IWhispersService _whispers;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly Func<CancellationToken, Task> _refreshCampaigns;

    public CampaignsRootNodeViewModel(
        ICampaignManagementDataSource management,
        ICampaignDialogService campaignDialog,
        IWhispersService whispers,
        FoundryFloorViewModel foundryFloor,
        Func<CancellationToken, Task> refreshCampaigns,
        Func<CancellationToken, Task<IReadOnlyList<AtelierNodeViewModel>>>? loader = null)
    {

        _management = management;

        _campaignDialog = campaignDialog;

        _whispers = whispers;

        _foundryFloor = foundryFloor;

        _refreshCampaigns = refreshCampaigns;

        _loader = loader ?? (static _ => Task.FromResult<IReadOnlyList<AtelierNodeViewModel>>([]));

        Label = "Campaigns";

        Icon = "IconCampaign";

        NewCampaignCommand = new AsyncRelayCommand(NewCampaignAsync);

    }

    public override IAsyncRelayCommand? NewCampaignCommand { get; }

    public override string? NewCampaignLabel => "New Campaign";

    private async Task NewCampaignAsync(CancellationToken cancellationToken)
    {

        LastError = null;

        NewCampaignInputs? inputs = await _campaignDialog
            .PromptNewCampaignAsync(cancellationToken)
            .ConfigureAwait(true);

        if (inputs is null)
        {

            return;

        }

        RegisterCampaignRequest request = new(
            inputs.Name,
            inputs.Path,
            inputs.Type,
            inputs.Description);

        DataSourceResult<CampaignDto> result = await _management
            .CreateAsync(request, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Success || result.Data is null)
        {

            string detail = FormatCampaignError(result.ErrorCode, result.ErrorMessage, "Failed to create campaign.");

            LastError = detail;

            _foundryFloor.AppendLine($"Campaign create failed: {detail}");

            _whispers.Show(WhisperSeverity.Error, "Campaign create failed.");

            return;

        }

        StatusText = "Campaign created.";

        _foundryFloor.AppendLine($"Campaign created: {result.Data.Name} ({result.Data.Id:D}).");

        _whispers.Show(WhisperSeverity.Success, "Campaign created.");

        await _refreshCampaigns(cancellationToken).ConfigureAwait(true);

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
