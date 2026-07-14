using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Top-level "Sessions" root. Lazy-loads recent sessions and exposes a New Session command that
/// creates a no-campaign session (<c>CampaignId: null</c>) and opens it in The Tome.
/// </summary>
public sealed partial class SessionsRootNodeViewModel : AtelierNodeViewModel
{

    private readonly IAtelierDataSource _dataSource;

    private readonly INavigationService _navigation;

    private readonly IArtifactCreationDataSource _creationDataSource;

    private readonly IArtifactCreationDialogService _dialogService;

    private readonly FoundryFloorViewModel _foundryFloor;

    public SessionsRootNodeViewModel(
        IAtelierDataSource dataSource,
        INavigationService navigation,
        IArtifactCreationDataSource creationDataSource,
        IArtifactCreationDialogService dialogService,
        FoundryFloorViewModel foundryFloor)
    {

        _dataSource = dataSource;

        _navigation = navigation;

        _creationDataSource = creationDataSource;

        _dialogService = dialogService;

        _foundryFloor = foundryFloor;

        Label = "Sessions";

        Icon = "IconSession";

        // Manual override (not [RelayCommand]) so HasNewSession sees the real command.
        NewSessionCommand = new AsyncRelayCommand(NewSessionAsync);

    }

    public override IAsyncRelayCommand? NewSessionCommand { get; }

    public override string? NewSessionLabel => "New Session";

    private async Task NewSessionAsync(CancellationToken cancellationToken)
    {

        LastError = null;

        NewSessionInputs? inputs = await _dialogService
            .PromptNewSessionAsync(campaignId: null, campaignName: null, cancellationToken)
            .ConfigureAwait(true);

        if (inputs is null)
        {

            return;

        }

        CreateSessionRequest request = new(CampaignId: null, Title: inputs.Title);

        (SessionDetailDto? session, string? error) = await _creationDataSource
            .CreateSessionAsync(request, cancellationToken)
            .ConfigureAwait(true);

        if (session is null)
        {

            LastError = error ?? "Failed to create session.";

            _foundryFloor.AppendLine($"New Session failed: {LastError}");

            return;

        }

        StatusText = "Session created.";

        await ReloadAsync(cancellationToken).ConfigureAwait(true);

        _navigation.OpenDocument(DocumentKind.Session, session.Id.ToString("D"));

    }

    protected override async Task<IReadOnlyList<AtelierNodeViewModel>> LoadChildrenAsync(CancellationToken cancellationToken)
    {

        return (await _dataSource.GetRecentSessionsAsync(cancellationToken).ConfigureAwait(true))
            .OrderByDescending(static session => session.UpdatedAt)
            .Select(session => new SessionNodeViewModel(session, _navigation))
            .Cast<AtelierNodeViewModel>()
            .ToArray();

    }

}
