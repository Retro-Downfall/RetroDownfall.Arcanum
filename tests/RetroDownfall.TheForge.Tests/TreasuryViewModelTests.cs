using System.ComponentModel;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Treasury;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class TreasuryViewModelTests
{

    [Fact]
    public async Task Refresh_EnabledBudget_DisplaysSummary()
    {

        FakeTreasuryDataSource dataSource = new()
        {

            Budget = new BudgetSummaryDto(true, 10m, 80, 4m, 6m, 40),

        };

        TreasuryViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.True(viewModel.IsEnabled);

        Assert.Equal(10m, viewModel.DailyLimitUsd);

        Assert.Equal(4m, viewModel.TodaySpendUsd);

        Assert.Equal(6m, viewModel.RemainingUsd);

        Assert.Equal(40, viewModel.SpentPercent);

        Assert.Equal(80, viewModel.AlertThresholdPercent);

        Assert.Equal(string.Empty, viewModel.EmptyState);

        Assert.Equal("Budget loaded.", viewModel.StatusText);

    }

    [Fact]
    public async Task Refresh_DisabledBudget_ShowsDisabledEmptyState()
    {

        FakeTreasuryDataSource dataSource = new()
        {

            Budget = new BudgetSummaryDto(false, 10m, 80, 4m, 6m, 40),

        };

        TreasuryViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.False(viewModel.IsEnabled);

        Assert.Contains(DisabledSettingPaths.BudgetEnabled, viewModel.EmptyState);

        Assert.Contains(DisabledSettingPaths.BudgetEnabled, viewModel.BudgetDisabledMessage);

        Assert.Equal(viewModel.BudgetDisabledMessage, viewModel.StatusText);

    }

    [Fact]
    public async Task CopyDisabledPaths_CopiesJoinedPaths()
    {

        FakeClipboardService clipboard = new();

        TreasuryViewModel viewModel = NewViewModel(new FakeTreasuryDataSource(), clipboard);

        await viewModel.CopyDisabledPathsCommand.ExecuteAsync(null);

        Assert.Equal(
            DisabledSettingPaths.JoinForClipboard(DisabledSettingPaths.Budget),
            clipboard.LastText);

    }

    [Fact]
    public async Task Refresh_WhenBudgetNull_SetsLastError()
    {

        FakeTreasuryDataSource dataSource = new() { Budget = null };

        TreasuryViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(viewModel.LastError));

        Assert.Equal("Budget unavailable.", viewModel.StatusText);

    }

    [Fact]
    public async Task Refresh_WhenThrows_SetsLastError()
    {

        FakeTreasuryDataSource dataSource = new() { Throw = true };

        TreasuryViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(viewModel.LastError));

    }

    private static TreasuryViewModel NewViewModel(
        FakeTreasuryDataSource dataSource,
        FakeClipboardService? clipboard = null) =>
        new(
            new FakeConnection(),
            dataSource,
            new FoundryFloorViewModel(new NullLogService()),
            clipboard ?? new FakeClipboardService(),
            new FakeCompendiumLauncher(),
            new FakeWhispersService());

    private sealed class FakeTreasuryDataSource : ITreasuryDataSource
    {

        public BudgetSummaryDto? Budget { get; init; }

        public bool Throw { get; init; }

        public Task<BudgetSummaryDto?> GetBudgetAsync(CancellationToken cancellationToken)
        {

            if (Throw)
            {

                throw new InvalidOperationException("boom");

            }

            return Task.FromResult(Budget);

        }

    }

    private sealed class FakeConnection : IArcanumConnection
    {

#pragma warning disable CS0067

        public event PropertyChangedEventHandler? PropertyChanged;

#pragma warning restore CS0067

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        public HealthReportDto? LastReport => null;

        public InstanceMetadataDto? LastMeta => null;

        public string? LastErrorCode => null;

        public string? LastErrorMessage => null;

        public void Connect()
        {

        }

        public void Disconnect()
        {

        }

    }

}
