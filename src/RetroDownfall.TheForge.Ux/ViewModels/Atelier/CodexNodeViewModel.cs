using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>Leaf node for a campaign's CODEX.md. Open routes to a Codex Workbench document.</summary>
public sealed partial class CodexNodeViewModel : AtelierNodeViewModel
{

    private readonly INavigationService _navigation;

    public CodexNodeViewModel(CampaignDto campaign, INavigationService navigation)
    {

        _navigation = navigation;

        Campaign = campaign;

        Label = "CODEX.md";

        Icon = "IconCodex";

    }

    public CampaignDto Campaign { get; }

    public override bool HasChildren => false;

    public override ICommand? PrimaryCommand => OpenCommand;

    [RelayCommand]
    private void Open()
    {

        _navigation.OpenDocument(DocumentKind.Codex, Campaign.Id.ToString("D"));

    }

}
