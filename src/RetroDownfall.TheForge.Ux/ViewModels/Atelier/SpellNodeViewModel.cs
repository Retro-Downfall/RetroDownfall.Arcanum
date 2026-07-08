using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>Leaf node for a spell. Double-click / Open routes to a Spell Workbench document.</summary>
public sealed partial class SpellNodeViewModel : AtelierNodeViewModel
{

    private readonly INavigationService _navigation;

    public SpellNodeViewModel(SpellSummary spell, INavigationService navigation)
    {

        _navigation = navigation;

        Spell = spell;

        Label = spell.Name;

        Icon = "IconSpell";

    }

    public SpellSummary Spell { get; }

    public override bool HasChildren => false;

    public override ICommand? PrimaryCommand => OpenCommand;

    [RelayCommand]
    private void Open()
    {

        _navigation.OpenDocument(DocumentKind.Spell, Spell.Name);

    }

}
