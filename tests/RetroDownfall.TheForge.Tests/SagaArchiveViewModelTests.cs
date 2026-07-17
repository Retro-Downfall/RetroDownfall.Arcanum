using System.ComponentModel;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Archive;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class SagaArchiveViewModelTests
{

    [Fact]
    public async Task Refresh_PopulatesMemoriesAndStats()
    {

        SagaMemoryDto memory = new("mem-1", "ancient tale", DateTimeOffset.UtcNow, null, null, null);

        SagaStats stats = new(1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        FakeSagaArchiveDataSource dataSource = new()
        {

            ListResult = new DataSourceResult<SagaMemoryDto[]>([memory], true, null, null),

            StatsResult = new DataSourceResult<SagaStats>(stats, true, null, null),

        };

        SagaArchiveViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Single(viewModel.Memories);

        Assert.Equal("mem-1", viewModel.Memories[0].Id);

        Assert.NotNull(viewModel.Stats);

        Assert.Equal(1, viewModel.Stats!.TotalCount);

    }

    [Fact]
    public async Task Divine_SurfacesResults()
    {

        SagaMemoryDto memory = new("mem-2", "hidden lore", DateTimeOffset.UtcNow, null, "quest", "saga");

        SagaSearchResult search = new([memory], [0.92f]);

        FakeSagaArchiveDataSource dataSource = new()
        {

            DivineResult = new DataSourceResult<SagaSearchResult>(search, true, null, null),

        };

        SagaArchiveViewModel viewModel = NewViewModel(dataSource);

        viewModel.DivinationQuery = "hidden lore";

        await viewModel.DivineAsync(CancellationToken.None);

        Assert.Equal("hidden lore", dataSource.LastDivineQuery);

        Assert.Single(viewModel.Memories);

        Assert.True(viewModel.IsSearchActive);

    }

    [Fact]
    public async Task Divine_FeatureDisabled_SetsDisabledState()
    {

        FakeSagaArchiveDataSource dataSource = new()
        {

            DivineResult = new DataSourceResult<SagaSearchResult>(
                null,
                false,
                ErrorCodes.Embeddings.FeatureDisabled,
                "disabled"),

        };

        SagaArchiveViewModel viewModel = NewViewModel(dataSource);

        viewModel.DivinationQuery = "query";

        await viewModel.DivineAsync(CancellationToken.None);

        Assert.True(viewModel.IsFeatureDisabled);

        Assert.Contains(DisabledSettingPaths.EmbeddingsEnabled, viewModel.FeatureDisabledMessage);

        Assert.Contains(DisabledSettingPaths.SagaEnabled, viewModel.FeatureDisabledMessage);

        Assert.Equal("Divination disabled.", viewModel.StatusText);

    }

    [Fact]
    public async Task CopyDisabledPaths_CopiesJoinedPaths()
    {

        FakeClipboardService clipboard = new();

        SagaArchiveViewModel viewModel = NewViewModel(new FakeSagaArchiveDataSource(), clipboard: clipboard);

        viewModel.IsFeatureDisabled = true;

        await viewModel.CopyDisabledPathsCommand.ExecuteAsync(null);

        Assert.Equal(
            DisabledSettingPaths.JoinForClipboard(DisabledSettingPaths.SagaDivination),
            clipboard.LastText);

    }

    [Fact]
    public async Task DeleteMemory_CallsDataSource()
    {

        SagaMemoryDto memory = new("mem-3", "to delete", DateTimeOffset.UtcNow, null, null, null);

        FakeSagaArchiveDataSource dataSource = new()
        {

            ListResult = new DataSourceResult<SagaMemoryDto[]>([], true, null, null),

            StatsResult = new DataSourceResult<SagaStats>(new SagaStats(0, 0, null, null), true, null, null),

            DeleteResult = new DataSourceResult<bool>(true, true, null, null),

        };

        SagaArchiveViewModel viewModel = NewViewModel(dataSource);

        viewModel.SelectedMemory = memory;

        await viewModel.DeleteMemoryAsync(CancellationToken.None);

        Assert.Equal("mem-3", dataSource.LastDeleteId);

        Assert.Null(viewModel.SelectedMemory);

    }

    [Fact]
    public async Task DeleteAllMemories_WhenConfirmed_CallsDataSource()
    {

        FakeSagaArchiveDataSource dataSource = new()
        {

            ListResult = new DataSourceResult<SagaMemoryDto[]>([], true, null, null),

            StatsResult = new DataSourceResult<SagaStats>(new SagaStats(0, 0, null, null), true, null, null),

            DeleteAllResult = new DataSourceResult<bool>(true, true, null, null),

        };

        ControllableConfirmationDialog confirmation = new(accept: true);

        SagaArchiveViewModel viewModel = NewViewModel(dataSource, confirmation);

        await viewModel.DeleteAllMemoriesAsync(CancellationToken.None);

        Assert.Equal(1, dataSource.DeleteAllCallCount);

        Assert.Equal(1, confirmation.CallCount);

    }

    [Fact]
    public async Task DeleteAllMemories_WhenCancelled_DoesNotCallDataSource()
    {

        FakeSagaArchiveDataSource dataSource = new();

        ControllableConfirmationDialog confirmation = new(accept: false);

        SagaArchiveViewModel viewModel = NewViewModel(dataSource, confirmation);

        await viewModel.DeleteAllMemoriesAsync(CancellationToken.None);

        Assert.Equal(0, dataSource.DeleteAllCallCount);

        Assert.Null(viewModel.LastError);

    }

    private static SagaArchiveViewModel NewViewModel(
        FakeSagaArchiveDataSource dataSource,
        IConfirmationDialogService? confirmation = null,
        FakeClipboardService? clipboard = null) =>
        new(
            dataSource,
            new FoundryFloorViewModel(new NullLogService()),
            confirmation ?? new NullConfirmationDialogService(),
            clipboard ?? new FakeClipboardService(),
            new FakeWhispersService(),
            new NullArcanumConnection());

    private sealed class NullArcanumConnection : IArcanumConnection
    {

        public ConnectionState State => ConnectionState.Disconnected;

        public HealthReportDto? LastReport => null;

        public InstanceMetadataDto? LastMeta => null;

        public string? LastErrorCode => null;

        public string? LastErrorMessage => null;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

    }

    private sealed class FakeSagaArchiveDataSource : ISagaArchiveDataSource
    {

        public DataSourceResult<SagaMemoryDto[]> ListResult { get; init; } =
            new([], true, null, null);

        public DataSourceResult<SagaSearchResult> DivineResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<bool> DeleteResult { get; init; } =
            new(true, true, null, null);

        public DataSourceResult<bool> DeleteAllResult { get; init; } =
            new(true, true, null, null);

        public DataSourceResult<SagaStats> StatsResult { get; init; } =
            new(null, true, null, null);

        public string? LastDivineQuery { get; private set; }

        public string? LastDeleteId { get; private set; }

        public int DeleteAllCallCount { get; private set; }

        public Task<DataSourceResult<SagaMemoryDto[]>> ListAsync(string? query, Guid? sessionId, int? limit, int? offset, CancellationToken cancellationToken) =>
            Task.FromResult(ListResult);

        public Task<DataSourceResult<SagaSearchResult>> DivineAsync(string query, int? limit, CancellationToken cancellationToken)
        {

            LastDivineQuery = query;

            return Task.FromResult(DivineResult);

        }

        public Task<DataSourceResult<bool>> DeleteAsync(string id, CancellationToken cancellationToken)
        {

            LastDeleteId = id;

            return Task.FromResult(DeleteResult);

        }

        public Task<DataSourceResult<bool>> DeleteAllAsync(CancellationToken cancellationToken)
        {

            DeleteAllCallCount++;

            return Task.FromResult(DeleteAllResult);

        }

        public Task<DataSourceResult<SagaStats>> GetStatsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(StatsResult);

    }

    private sealed class ControllableConfirmationDialog(bool accept) : IConfirmationDialogService
    {

        public int CallCount { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken, bool confirmIsDefault = true)
        {

            CallCount++;

            return Task.FromResult(accept);

        }

    }

}
