using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>Leaf node for a prompt template. Double-click / Open routes to a Prompt Workbench document.</summary>
public sealed partial class PromptNodeViewModel : AtelierNodeViewModel
{

    private readonly INavigationService _navigation;

    public PromptNodeViewModel(PromptSummaryDto prompt, INavigationService navigation)
    {

        _navigation = navigation;

        Prompt = prompt;

        Label = $"{prompt.Name} {prompt.Version}";

        Icon = "IconPrompt";

    }

    public PromptSummaryDto Prompt { get; }

    public override bool HasChildren => false;

    public override ICommand? PrimaryCommand => OpenCommand;

    [RelayCommand]
    private void Open()
    {

        _navigation.OpenDocument(DocumentKind.Prompt, Prompt.Id.ToString());

    }

}
