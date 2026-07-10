using System.ComponentModel;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Docking;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
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

        Assert.False(viewModel.DockLayout.Left.IsCollapsed);

        Assert.False(viewModel.DockLayout.Right.IsCollapsed);

        Assert.False(viewModel.DockLayout.Bottom.IsCollapsed);

        viewModel.DockLayout.HideTool(DockToolId.Atelier);

        viewModel.DockLayout.HideTool(DockToolId.Gatehouse);

        viewModel.DockLayout.HideTool(DockToolId.Treasury);

        viewModel.DockLayout.HideTool(DockToolId.Arsenal);

        viewModel.DockLayout.HideTool(DockToolId.WarTable);

        viewModel.DockLayout.HideTool(DockToolId.Output);

        viewModel.DockLayout.HideTool(DockToolId.Logs);

        viewModel.DockLayout.HideTool(DockToolId.Hearth);

        Assert.True(viewModel.DockLayout.Left.IsCollapsed);

        Assert.True(viewModel.DockLayout.Right.IsCollapsed);

        Assert.True(viewModel.DockLayout.Bottom.IsCollapsed);

        viewModel.Dispose();

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

        viewModel.DockLayout.HideTool(DockToolId.Atelier);

        Assert.True(viewModel.DockLayout.Left.IsCollapsed);

        navigation.FocusPanel(PanelKind.Atelier);

        Assert.False(viewModel.DockLayout.Left.IsCollapsed);

        Assert.Equal(DockToolId.Atelier, viewModel.DockLayout.Left.SelectedTool?.ToolId);

        viewModel.Dispose();

    }

    [Fact]
    public void NavigationFocusWarTable_SelectsTabAndSetsWarTableVisible()
    {

        FakeArcanumConnection connection = new();

        NavigationService navigation = new();

        MainViewModel viewModel = MainViewModelFactory.Create(connection, navigation);

        viewModel.DockLayout.HideTool(DockToolId.Gatehouse);

        viewModel.DockLayout.HideTool(DockToolId.Treasury);

        viewModel.DockLayout.HideTool(DockToolId.Arsenal);

        viewModel.DockLayout.HideTool(DockToolId.WarTable);

        Assert.True(viewModel.DockLayout.Right.IsCollapsed);

        Assert.False(viewModel.WarTable.IsVisible);

        navigation.FocusPanel(PanelKind.WarTable);

        Assert.False(viewModel.DockLayout.Right.IsCollapsed);

        Assert.Equal(DockToolId.WarTable, viewModel.DockLayout.Right.SelectedTool?.ToolId);

        Assert.True(viewModel.WarTable.IsVisible);

        Assert.False(viewModel.Gatehouse.IsVisible);

        viewModel.Dispose();

    }

    [Fact]
    public void NavigationFocusGatehouse_SelectsFirstTab()
    {

        FakeArcanumConnection connection = new();

        NavigationService navigation = new();

        MainViewModel viewModel = MainViewModelFactory.Create(connection, navigation);

        viewModel.DockLayout.FocusTool(DockToolId.WarTable);

        navigation.FocusPanel(PanelKind.Gatehouse);

        Assert.Equal(DockToolId.Gatehouse, viewModel.DockLayout.Right.SelectedTool?.ToolId);

        Assert.True(viewModel.Gatehouse.IsVisible);

        Assert.False(viewModel.WarTable.IsVisible);

        viewModel.Dispose();

    }

    [Fact]
    public void NavigationFocusWarTable_WorksAfterMoveToBottom()
    {

        FakeArcanumConnection connection = new();

        NavigationService navigation = new();

        MainViewModel viewModel = MainViewModelFactory.Create(connection, navigation);

        viewModel.DockLayout.MoveTool(viewModel.DockLayout.FindTool(DockToolId.WarTable)!, DockRegion.Bottom);

        navigation.FocusPanel(PanelKind.WarTable);

        Assert.Equal(DockToolId.WarTable, viewModel.DockLayout.Bottom.SelectedTool?.ToolId);

        Assert.True(viewModel.WarTable.IsVisible);

        viewModel.Dispose();

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

    public static MainViewModel Create(IArcanumConnection connection, INavigationService navigation)
    {

        NullLogService logs = new();

        FoundryFloorViewModel foundryFloor = new(logs);

        string tempPath = Path.Combine(Path.GetTempPath(), $"forge-test-{Guid.NewGuid():N}.json");

        ForgeSettingsStore settingsStore = new(tempPath);

        return new(
            connection,
            navigation,
            new RetroDownfall.TheForge.Ux.ViewModels.Atelier.AtelierViewModel(new NullAtelierDataSource(), navigation),
            new RetroDownfall.TheForge.Ux.ViewModels.WarTable.WarTableViewModel(new NullWarTableDataSource()),
            new RetroDownfall.TheForge.Ux.ViewModels.Gatehouse.GatehouseViewModel(new NullGatehouseDataSource()),
            new RetroDownfall.TheForge.Ux.ViewModels.Treasury.TreasuryViewModel(),
            new RetroDownfall.TheForge.Ux.ViewModels.Arsenal.ArsenalViewModel(),
            foundryFloor,
            new RetroDownfall.TheForge.Ux.ViewModels.Hearth.HearthViewModel(new NullTerminalCommandRunner()),
            new RetroDownfall.TheForge.Ux.ViewModels.Anvil.AnvilViewModel(
                connection,
                new NullAnvilDataSource(),
                navigation,
                new StaticForgeSettingsMonitor(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RetroDownfall.TheForge.Ux.ViewModels.Anvil.AnvilViewModel>.Instance),
            new RetroDownfall.TheForge.Ux.ViewModels.Workbench.WorkbenchDocumentFactory(
                new NullSpellEditorDataSource(),
                new NullTomeDataSource(),
                navigation,
                foundryFloor),
            settingsStore,
            new StaticForgeSettingsMonitor());

    }

}

internal sealed class NullLogService : RetroDownfall.TheForge.Ux.Services.Services.ILogService
{

    public Task<RetroDownfall.Arcanum.Core.Primitives.ApiResponse<RetroDownfall.Arcanum.Core.Logging.LogQueryResult>?> QueryAsync(
        RetroDownfall.Arcanum.Core.Logging.LogLevel? minLevel,
        string? category,
        string? search,
        int? limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<RetroDownfall.Arcanum.Core.Primitives.ApiResponse<RetroDownfall.Arcanum.Core.Logging.LogQueryResult>?>(null);

    public async IAsyncEnumerable<RetroDownfall.Arcanum.Core.Logging.LogEntry> StreamLogsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {

        await Task.CompletedTask;

        yield break;

    }

}


internal sealed class NullWarTableDataSource : RetroDownfall.TheForge.Ux.ViewModels.WarTable.IWarTableDataSource
{

    public Task<IReadOnlyList<RetroDownfall.Arcanum.Core.TheForge.ApprenticeSummaryDto>> ListApprenticesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RetroDownfall.Arcanum.Core.TheForge.ApprenticeSummaryDto>>([]);

    public Task<RetroDownfall.Arcanum.Core.TheForge.ApprenticeDetailDto?> GetApprenticeAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<RetroDownfall.Arcanum.Core.TheForge.ApprenticeDetailDto?>(null);

    public Task<RetroDownfall.Arcanum.Core.TheForge.ApprenticeDetailDto?> CreateApprenticeAsync(RetroDownfall.Arcanum.Core.TheForge.CreateApprenticeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<RetroDownfall.Arcanum.Core.TheForge.ApprenticeDetailDto?>(null);

    public Task<bool> StartAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> PauseAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> ResumeAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<RetroDownfall.Arcanum.Core.TheForge.ApprenticeDetailDto?> ReweaveAsync(Guid id, RetroDownfall.Arcanum.Core.TheForge.ReweaveApprenticeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<RetroDownfall.Arcanum.Core.TheForge.ApprenticeDetailDto?>(null);

    public Task<bool> InterveneAsync(Guid id, RetroDownfall.Arcanum.Core.TheForge.InterveneApprenticeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<RetroDownfall.Arcanum.Core.TheForge.ApprenticeDetailDto>> GetLineageAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RetroDownfall.Arcanum.Core.TheForge.ApprenticeDetailDto>>([]);

    public async IAsyncEnumerable<RetroDownfall.TheForge.Core.Chronicle.ChronicleFrame> StreamChronicleAsync(Guid id, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {

        await Task.CompletedTask;

        yield break;

    }

    public Task<IReadOnlyList<RetroDownfall.Arcanum.Core.TheForge.CampaignDto>> ListCampaignsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RetroDownfall.Arcanum.Core.TheForge.CampaignDto>>([]);

    public Task<IReadOnlyList<RetroDownfall.Arcanum.Core.Workspaces.WorkspaceInfo>> ListWorkspacesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RetroDownfall.Arcanum.Core.Workspaces.WorkspaceInfo>>([]);

}

internal sealed class NullGatehouseDataSource : RetroDownfall.TheForge.Ux.ViewModels.Gatehouse.IGatehouseDataSource
{

    public Task<IReadOnlyList<RetroDownfall.Arcanum.Core.Wards.WardDto>> ListWardsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RetroDownfall.Arcanum.Core.Wards.WardDto>>([]);

    public Task<bool> ResolveAsync(string wardId, bool allow, string? reason, CancellationToken cancellationToken) =>
        Task.FromResult(false);

}

internal sealed class NullAnvilDataSource : RetroDownfall.TheForge.Ux.ViewModels.Anvil.IAnvilDataSource
{

    public Task<RetroDownfall.TheForge.Core.Models.BudgetSummaryDto?> GetBudgetAsync(CancellationToken cancellationToken) =>
        Task.FromResult<RetroDownfall.TheForge.Core.Models.BudgetSummaryDto?>(null);

    public Task<IReadOnlyList<RetroDownfall.Arcanum.Core.Wards.WardDto>> ListWardsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RetroDownfall.Arcanum.Core.Wards.WardDto>>([]);

    public Task<IReadOnlyList<RetroDownfall.Arcanum.Core.TheForge.ApprenticeSummaryDto>> ListApprenticesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RetroDownfall.Arcanum.Core.TheForge.ApprenticeSummaryDto>>([]);

    public Task<IReadOnlyList<RetroDownfall.Arcanum.Core.Mcp.McpServerInfo>> ListMcpServersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RetroDownfall.Arcanum.Core.Mcp.McpServerInfo>>([]);

}

internal sealed class StaticForgeSettingsMonitor : Microsoft.Extensions.Options.IOptionsMonitor<RetroDownfall.TheForge.Core.Models.ForgeSettings>
{

    public RetroDownfall.TheForge.Core.Models.ForgeSettings CurrentValue { get; } = new();

    public RetroDownfall.TheForge.Core.Models.ForgeSettings Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<RetroDownfall.TheForge.Core.Models.ForgeSettings, string?> listener) => null;

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

internal sealed class NullTerminalCommandRunner : RetroDownfall.TheForge.Ux.Services.Terminal.ITerminalCommandRunner
{

    public Task<RetroDownfall.TheForge.Ux.Services.Terminal.TerminalCommandResult> RunAsync(
        string command,
        string workingDirectory,
        IProgress<RetroDownfall.TheForge.Ux.Services.Terminal.TerminalOutputEvent>? progress,
        CancellationToken cancellationToken) =>
        Task.FromResult(RetroDownfall.TheForge.Ux.Services.Terminal.TerminalCommandResult.Completed(0));

}
