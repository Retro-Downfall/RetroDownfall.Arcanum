using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>Leaf node for a Tome session. Double-click / Open routes to a Session Workbench document.</summary>
public sealed partial class SessionNodeViewModel : AtelierNodeViewModel
{

    private readonly INavigationService _navigation;

    public SessionNodeViewModel(SessionSummaryDto session, INavigationService navigation)
    {

        _navigation = navigation;

        Session = session;

        Label = string.IsNullOrWhiteSpace(session.Title) ? $"Session {session.Id:N}" : session.Title;

        Icon = "IconSession";

    }

    public SessionSummaryDto Session { get; }

    public override bool HasChildren => false;

    public override ICommand? PrimaryCommand => OpenCommand;

    [RelayCommand]
    private void Open()
    {

        _navigation.OpenDocument(DocumentKind.Session, Session.Id.ToString());

    }

}
