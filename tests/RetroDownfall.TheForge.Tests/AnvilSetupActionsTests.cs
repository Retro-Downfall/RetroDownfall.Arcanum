using System.ComponentModel;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Compendium;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.Anvil;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class AnvilSetupActionsTests
{

    [Fact]
    public async Task OpenSetupWizard_InvokesDialogService()
    {

        NoopSetupWizardDialogService dialog = new();

        AnvilViewModel viewModel = Create(dialog: dialog);

        await viewModel.OpenSetupWizardCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ShowCount);

        viewModel.Dispose();

    }

    [Fact]
    public void OpenCompendium_UsesLauncher()
    {

        FakeCompendiumLauncher launcher = new();

        FakeWhispersService whispers = new();

        AnvilViewModel viewModel = Create(launcher: launcher, whispers: whispers);

        viewModel.OpenCompendiumCommand.Execute(null);

        Assert.Equal(1, launcher.LaunchCount);

        Assert.NotEmpty(whispers.Calls);

        viewModel.Dispose();

    }

    [Fact]
    public void Reconnect_DisconnectsThenConnects()
    {

        TrackingConnection connection = new();

        AnvilViewModel viewModel = Create(connection: connection);

        viewModel.ReconnectCommand.Execute(null);

        Assert.Equal(["Disconnect", "Connect"], connection.Calls);

        viewModel.Dispose();

    }

    private static AnvilViewModel Create(
        IArcanumConnection? connection = null,
        NoopSetupWizardDialogService? dialog = null,
        FakeCompendiumLauncher? launcher = null,
        FakeWhispersService? whispers = null) =>
        new(
            connection ?? new TrackingConnection(),
            new EmptyAnvilDataSource(),
            new NavigationService(),
            new NoopApiKeyProvider(),
            dialog ?? new NoopSetupWizardDialogService(),
            launcher ?? new FakeCompendiumLauncher(),
            whispers ?? new FakeWhispersService(),
            new StaticOptionsMonitor(new TheForgeSettings()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AnvilViewModel>.Instance);

    private sealed class EmptyAnvilDataSource : IAnvilDataSource
    {

        public Task<BudgetSummaryDto?> GetBudgetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BudgetSummaryDto?>(null);

        public Task<IReadOnlyList<WardDto>> ListWardsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WardDto>>([]);

        public Task<IReadOnlyList<ApprenticeSummaryDto>> ListApprenticesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ApprenticeSummaryDto>>([]);

        public Task<IReadOnlyList<McpServerInfo>> ListMcpServersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<McpServerInfo>>([]);

    }

    private sealed class TrackingConnection : IArcanumConnection
    {

        public List<string> Calls { get; } = [];

        public event PropertyChangedEventHandler? PropertyChanged;

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        public HealthReportDto? LastReport => null;

        public InstanceMetadataDto? LastMeta => null;

        public string? LastErrorCode => null;

        public string? LastErrorMessage => null;

        public void Connect()
        {

            Calls.Add("Connect");

            State = ConnectionState.Connected;

        }

        public void Disconnect()
        {

            Calls.Add("Disconnect");

            State = ConnectionState.Disconnected;

        }

    }

    private sealed class NoopApiKeyProvider : ITheForgeApiKeyProvider
    {

        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task PersistPastedKeyAsync(string apiKey, CancellationToken cancellationToken) => Task.CompletedTask;

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
