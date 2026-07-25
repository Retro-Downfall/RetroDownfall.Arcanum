using System.ComponentModel;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.Anvil;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class AnvilViewModelTests
{

    [Fact]
    public async Task RefreshAsync_WhenConnected_AggregatesBudgetWardsApprenticesAndMcp()
    {

        FakeArcanumConnection connection = new();

        connection.SetState(ConnectionState.Connected);

        FakeAnvilDataSource dataSource = new()
        {
            Budget = new BudgetSummaryDto(true, 10m, 80, 2.5m, 7.5m, 25),
            Wards = [NewWard()],
            Apprentices =
            [
                NewApprentice("Running"),
                NewApprentice("Paused"),
                NewApprentice("InProgress"),
            ],
            McpServers =
            [
                NewMcp("a", McpServerState.Running),
                NewMcp("b", McpServerState.Stopped),
            ],
        };

        AnvilViewModel viewModel = Create(connection, dataSource, new NavigationService());

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal(2.5m, viewModel.TodaySpendUsd);

        Assert.Equal(25, viewModel.ManaPercent);

        Assert.Equal(1, viewModel.ActiveWardsCount);

        Assert.Equal(2, viewModel.RunningApprenticesCount);

        Assert.Equal("1/2", viewModel.McpOnlineTotal);

        viewModel.Dispose();

    }

    [Fact]
    public void FocusCommands_RequestExpectedPanels()
    {

        FakeArcanumConnection connection = new();

        NavigationService navigation = new();

        List<PanelKind> focused = [];

        navigation.PanelFocusRequested += panel => focused.Add(panel);

        AnvilViewModel viewModel = Create(connection, new FakeAnvilDataSource(), navigation);

        viewModel.FocusCampaignCommand.Execute(null);

        viewModel.FocusWardsCommand.Execute(null);

        viewModel.FocusApprenticesCommand.Execute(null);

        viewModel.FocusMcpCommand.Execute(null);

        viewModel.FocusBudgetCommand.Execute(null);

        Assert.Equal(
            [PanelKind.Atelier, PanelKind.Gatehouse, PanelKind.WarTable, PanelKind.Arsenal, PanelKind.Treasury],
            focused);

        viewModel.Dispose();

    }

    [Fact]
    public void ConnectionState_InitializesStatusText()
    {

        using AnvilViewModel disconnected = Create(
            new FakeArcanumConnection(),
            new FakeAnvilDataSource(),
            new NavigationService());

        Assert.Equal("Arcanum disconnected", disconnected.ConnectionStatusText);

        FakeArcanumConnection connectedState = new();

        connectedState.SetState(ConnectionState.Connected);

        using AnvilViewModel connected = Create(
            connectedState,
            new FakeAnvilDataSource(),
            new NavigationService());

        Assert.Equal(ConnectionState.Connected, connected.ConnectionState);

        Assert.Equal("Arcanum connected", connected.ConnectionStatusText);

    }

    [Fact]
    public void ConnectionStatusText_MapsDistinctErrorCodes()
    {

        AssertConnectionStatus("Security.MissingApiKey", "API key required", showEnterApiKey: true);

        AssertConnectionStatus("Auth.Unauthorized", "API key rejected", showEnterApiKey: true);

        AssertConnectionStatus("Connection.Timeout", "Arcanum timed out", showEnterApiKey: false);

        AssertConnectionStatus("Connection.Failed", "Arcanum connection failed", showEnterApiKey: false);

        AssertConnectionStatus("Http.503", "Arcanum unreachable", showEnterApiKey: false);

    }

    [Fact]
    public void ConnectionStatusText_AuthErrorsWinOverConnectingState()
    {

        FakeArcanumConnection connection = new();

        connection.SetConnectingWithError("Security.MissingApiKey");

        using AnvilViewModel viewModel = Create(
            connection,
            new FakeAnvilDataSource(),
            new NavigationService());

        Assert.Equal(ConnectionState.Connecting, viewModel.ConnectionState);

        Assert.Equal("API key required", viewModel.ConnectionStatusText);

        Assert.True(viewModel.ShowEnterApiKey);

    }

    private static void AssertConnectionStatus(
        string errorCode,
        string expectedText,
        bool showEnterApiKey)
    {

        FakeArcanumConnection connection = new();

        connection.SetError(errorCode);

        using AnvilViewModel viewModel = Create(
            connection,
            new FakeAnvilDataSource(),
            new NavigationService());

        Assert.Equal(expectedText, viewModel.ConnectionStatusText);

        Assert.Equal(showEnterApiKey, viewModel.ShowEnterApiKey);

    }

    private static AnvilViewModel Create(
        IArcanumConnection connection,
        IAnvilDataSource dataSource,
        INavigationService navigation) =>
        new(
            connection,
            dataSource,
            navigation,
            new NoopApiKeyProvider(),
            new NoopSetupWizardDialogService(),
            new FakeCompendiumLauncher(),
            new FakeWhispersService(),
            new StaticOptionsMonitor(new TheForgeSettings()),
            new FakeActiveCampaignService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AnvilViewModel>.Instance);

    private static WardDto NewWard() =>
        new("ward-1", "hammer", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));

    private static ApprenticeSummaryDto NewApprentice(string status) =>
        new(Guid.NewGuid(), null, "A", "goal", status, 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static McpServerInfo NewMcp(string name, McpServerState state) =>
        new(name, null, McpServerTransport.Stdio, false, "cmd", [], null, state, null, [], null);

    private sealed class FakeAnvilDataSource : IAnvilDataSource
    {

        public BudgetSummaryDto? Budget { get; init; }

        public IReadOnlyList<WardDto> Wards { get; init; } = [];

        public IReadOnlyList<ApprenticeSummaryDto> Apprentices { get; init; } = [];

        public IReadOnlyList<McpServerInfo> McpServers { get; init; } = [];

        public Task<BudgetSummaryDto?> GetBudgetAsync(CancellationToken cancellationToken) => Task.FromResult(Budget);

        public Task<IReadOnlyList<WardDto>> ListWardsAsync(CancellationToken cancellationToken) => Task.FromResult(Wards);

        public Task<IReadOnlyList<ApprenticeSummaryDto>> ListApprenticesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Apprentices);

        public Task<IReadOnlyList<McpServerInfo>> ListMcpServersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(McpServers);

    }

    private sealed class FakeArcanumConnection : IArcanumConnection
    {

        public event PropertyChangedEventHandler? PropertyChanged;

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        public HealthReportDto? LastReport { get; private set; }

        public InstanceMetadataDto? LastMeta { get; private set; }

        public string? LastErrorCode { get; private set; }

        public string? LastErrorMessage { get; private set; }

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        public void SetState(ConnectionState state)
        {

            State = state;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));

        }

        public void SetError(string code, string? message = null)
        {

            LastErrorCode = code;

            LastErrorMessage = message;

            State = ConnectionState.Error;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastErrorCode)));

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastErrorMessage)));

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));

        }

        public void SetConnectingWithError(string code, string? message = null)
        {

            LastErrorCode = code;

            LastErrorMessage = message;

            State = ConnectionState.Connecting;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastErrorCode)));

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastErrorMessage)));

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));

        }

    }

    private sealed class NoopApiKeyProvider : RetroDownfall.TheForge.Core.Services.ITheForgeApiKeyProvider
    {

        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task PersistPastedKeyAsync(string apiKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ClearPasteDecline()
        {
        }

    }

    private sealed class StaticOptionsMonitor(TheForgeSettings current) : IOptionsMonitor<TheForgeSettings>
    {

        public TheForgeSettings CurrentValue { get; } = current;

        public TheForgeSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TheForgeSettings, string?> listener) => null;

    }

}
