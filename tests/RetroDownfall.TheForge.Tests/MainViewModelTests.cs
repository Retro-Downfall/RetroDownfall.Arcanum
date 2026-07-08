using System.ComponentModel;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class MainViewModelTests
{

    [Fact]
    public void ToggleCommands_UpdatePanelVisibility()
    {

        FakeArcanumConnection connection = new();

        NavigationService navigation = new();

        MainViewModel viewModel = MainViewModelFactory.Create(connection, navigation);

        Assert.True(viewModel.IsAtelierVisible);

        Assert.True(viewModel.IsRightPanelVisible);

        Assert.True(viewModel.IsFoundryFloorVisible);

        viewModel.ToggleAtelierCommand.Execute(null);

        viewModel.ToggleRightPanelCommand.Execute(null);

        viewModel.ToggleFoundryFloorCommand.Execute(null);

        Assert.False(viewModel.IsAtelierVisible);

        Assert.False(viewModel.IsRightPanelVisible);

        Assert.False(viewModel.IsFoundryFloorVisible);

    }

    [Fact]
    public void ConnectAndDisconnectCommands_DelegateToConnectionService()
    {

        FakeArcanumConnection connection = new();

        NavigationService navigation = new();

        MainViewModel viewModel = MainViewModelFactory.Create(connection, navigation);

        viewModel.ConnectCommand.Execute(null);

        viewModel.DisconnectCommand.Execute(null);

        Assert.Equal(1, connection.ConnectCallCount);

        Assert.Equal(1, connection.DisconnectCallCount);

    }

    [Fact]
    public void ConnectionState_UpdatesWhenConnectionRaisesPropertyChanged()
    {

        FakeArcanumConnection connection = new();

        NavigationService navigation = new();

        MainViewModel viewModel = MainViewModelFactory.Create(connection, navigation);

        connection.SetState(ConnectionState.Connected);

        Assert.Equal(ConnectionState.Connected, viewModel.ConnectionState);

    }

    [Fact]
    public void NavigationOpenDocument_AddsAndActivatesDocument()
    {

        FakeArcanumConnection connection = new();

        NavigationService navigation = new();

        MainViewModel viewModel = MainViewModelFactory.Create(connection, navigation);

        navigation.OpenDocument(DocumentKind.Spell, "greater-heal");

        ViewModelBase document = Assert.Single(viewModel.OpenDocuments);

        Assert.Equal(DocumentKind.Spell, document.Kind);

        Assert.Equal("Spell: greater-heal", document.Title);

        Assert.Same(document, viewModel.ActiveDocument);

    }

    [Fact]
    public void NavigationOpenDocument_WhenAlreadyOpen_ActivatesExistingDocumentWithoutDuplicate()
    {

        FakeArcanumConnection connection = new();

        NavigationService navigation = new();

        MainViewModel viewModel = MainViewModelFactory.Create(connection, navigation);

        navigation.OpenDocument(DocumentKind.Spell, "greater-heal");

        ViewModelBase first = Assert.Single(viewModel.OpenDocuments);

        navigation.OpenDocument(DocumentKind.Spell, "greater-heal");

        ViewModelBase second = Assert.Single(viewModel.OpenDocuments);

        Assert.Same(first, second);

        Assert.Same(first, viewModel.ActiveDocument);

    }

    [Fact]
    public void NavigationCloseDocument_RemovesDocumentAndClearsActiveDocument()
    {

        FakeArcanumConnection connection = new();

        NavigationService navigation = new();

        MainViewModel viewModel = MainViewModelFactory.Create(connection, navigation);

        navigation.OpenDocument(DocumentKind.Prompt, "prompt-1");

        navigation.CloseDocument(DocumentKind.Prompt, "prompt-1");

        Assert.Empty(viewModel.OpenDocuments);

        Assert.Null(viewModel.ActiveDocument);

    }

    [Fact]
    public void NavigationFocusPanel_MakesRequestedPanelVisible()
    {

        FakeArcanumConnection connection = new();

        NavigationService navigation = new();

        MainViewModel viewModel = MainViewModelFactory.Create(connection, navigation);

        viewModel.ToggleAtelierCommand.Execute(null);

        Assert.False(viewModel.IsAtelierVisible);

        navigation.FocusPanel(PanelKind.Atelier);

        Assert.True(viewModel.IsAtelierVisible);

    }

    private sealed class FakeArcanumConnection : IArcanumConnection
    {

        public event PropertyChangedEventHandler? PropertyChanged;

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        public HealthReportDto? LastReport { get; private set; }

        public int ConnectCallCount { get; private set; }

        public int DisconnectCallCount { get; private set; }

        public void Connect()
        {

            ConnectCallCount++;

        }

        public void Disconnect()
        {

            DisconnectCallCount++;

        }

        public void SetState(ConnectionState state)
        {

            State = state;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));

        }

    }

}

internal sealed class NullAtelierDataSource : RetroDownfall.TheForge.Ux.ViewModels.Atelier.IAtelierDataSource
{

    public Task<IReadOnlyList<CampaignDto>> GetCampaignsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CampaignDto>>([]);

    public Task<IReadOnlyList<WorkspaceInfo>> GetWorkspacesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkspaceInfo>>([]);

    public Task<IReadOnlyList<SpellSummary>> GetGlobalSpellsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SpellSummary>>([]);

    public Task<IReadOnlyList<SessionSummaryDto>> GetRecentSessionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SessionSummaryDto>>([]);

    public Task<IReadOnlyList<SpellSummary>> GetCampaignSpellsAsync(Guid campaignId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SpellSummary>>([]);

    public Task<IReadOnlyList<PromptSummaryDto>> GetCampaignPromptsAsync(Guid campaignId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PromptSummaryDto>>([]);

    public Task<IReadOnlyList<SessionSummaryDto>> GetCampaignSessionsAsync(Guid campaignId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SessionSummaryDto>>([]);

}

internal static class MainViewModelFactory
{

    public static MainViewModel Create(IArcanumConnection connection, INavigationService navigation) =>
        new(
            connection,
            navigation,
            new RetroDownfall.TheForge.Ux.ViewModels.Atelier.AtelierViewModel(new NullAtelierDataSource(), navigation),
            new RetroDownfall.TheForge.Ux.ViewModels.WarTable.WarTableViewModel(),
            new RetroDownfall.TheForge.Ux.ViewModels.Gatehouse.GatehouseViewModel(),
            new RetroDownfall.TheForge.Ux.ViewModels.Treasury.TreasuryViewModel(),
            new RetroDownfall.TheForge.Ux.ViewModels.Arsenal.ArsenalViewModel(),
            new RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor.FoundryFloorViewModel(),
            new RetroDownfall.TheForge.Ux.ViewModels.Hearth.HearthViewModel(),
            new RetroDownfall.TheForge.Ux.ViewModels.Anvil.AnvilViewModel(connection),
            new RetroDownfall.TheForge.Ux.ViewModels.Workbench.WorkbenchDocumentFactory(
                new NullSpellEditorDataSource(),
                new NullTomeDataSource(),
                navigation,
                new RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor.FoundryFloorViewModel()));

}

internal sealed class NullTomeDataSource : RetroDownfall.TheForge.Ux.ViewModels.Workbench.ITomeDataSource
{

    public Task<SessionDetailDto?> GetSessionAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<SessionDetailDto?>(null);

    public async IAsyncEnumerable<IntelligenceEvent> PingStreamAsync(PingRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {

        await Task.CompletedTask;

        yield break;

    }

    public async IAsyncEnumerable<EntryDto> StreamEntriesAsync(Guid id, DateTimeOffset? since, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {

        await Task.CompletedTask;

        yield break;

    }

    public Task<EntryDto?> AppendEntryAsync(Guid id, AppendEntryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<EntryDto?>(null);

    public Task<SessionDetailDto?> ForkAsync(Guid id, ForkSessionRequest? request, CancellationToken cancellationToken) =>
        Task.FromResult<SessionDetailDto?>(null);

    public Task<SessionExportResult?> ExportAsync(Guid id, string format, CancellationToken cancellationToken) =>
        Task.FromResult<SessionExportResult?>(null);

}


internal sealed class NullSpellEditorDataSource : RetroDownfall.TheForge.Ux.ViewModels.Workbench.ISpellEditorDataSource
{

    public Task<SpellDetail?> LoadSpellAsync(string name, string? workspace, CancellationToken cancellationToken) => Task.FromResult<SpellDetail?>(null);

    public Task<IReadOnlyList<SpellVersionDto>> ListVersionsAsync(string name, string? workspace, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SpellVersionDto>>([]);

    public Task<bool> SaveAsync(string name, UpdateSpellRequest request, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<SpellCastResult?> CastAsync(string name, SpellCastRequest request, CancellationToken cancellationToken) => Task.FromResult<SpellCastResult?>(null);

    public Task<ManaCountResult?> EstimateManaAsync(ManaCountRequest request, CancellationToken cancellationToken) => Task.FromResult<ManaCountResult?>(null);

    public async IAsyncEnumerable<IntelligenceEvent> ExecuteStreamAsync(string name, SpellExecuteRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {

        await Task.CompletedTask;

        yield break;

    }

    public Task<bool> ActivateVersionAsync(string name, string version, string? workspace, CancellationToken cancellationToken) => Task.FromResult(false);

}
