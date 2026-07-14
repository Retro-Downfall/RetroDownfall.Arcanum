using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Lore;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class LoreBrowserViewModelTests
{

    [Fact]
    public async Task Refresh_PopulatesLore()
    {

        LoreDto lore = new("faction", "Iron Legion", DateTime.UtcNow);

        FakeLoreDataSource dataSource = new()
        {

            ListResult = new DataSourceResult<ListPageResult<LoreDto>>(
                new ListPageResult<LoreDto>([lore], false),
                true,
                null,
                null),

        };

        LoreBrowserViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Single(viewModel.Lore);

        Assert.Equal("faction", viewModel.Lore[0].Key);

        Assert.Equal("Iron Legion", viewModel.Lore[0].Value);

        Assert.Null(viewModel.LastError);

    }

    [Fact]
    public async Task Save_CallsUpsertWithKeyValue_ThenRefreshes()
    {

        LoreDto saved = new("realm", "Eldoria", DateTime.UtcNow);

        FakeLoreDataSource dataSource = new()
        {

            ListResult = new DataSourceResult<ListPageResult<LoreDto>>(
                new ListPageResult<LoreDto>([], false),
                true,
                null,
                null),

            UpsertResult = new DataSourceResult<LoreDto>(saved, true, null, null),

        };

        LoreBrowserViewModel viewModel = NewViewModel(dataSource);

        viewModel.EditKey = "realm";

        viewModel.EditValue = "Eldoria";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.Equal("realm", dataSource.LastUpsertKey);

        Assert.Equal("Eldoria", dataSource.LastUpsertValue);

        Assert.Equal(1, dataSource.ListCallCount);

    }

    [Fact]
    public async Task Delete_CallsDataSourceAndRefreshes()
    {

        LoreDto lore = new("sigil", "azure", DateTime.UtcNow);

        FakeLoreDataSource dataSource = new()
        {

            ListResult = new DataSourceResult<ListPageResult<LoreDto>>(
                new ListPageResult<LoreDto>([lore], false),
                true,
                null,
                null),

            DeleteResult = new DataSourceResult<bool>(true, true, null, null),

        };

        LoreBrowserViewModel viewModel = NewViewModel(dataSource);

        viewModel.SelectedLore = lore;

        await viewModel.DeleteAsync(CancellationToken.None);

        Assert.Equal("sigil", dataSource.LastDeleteKey);

        Assert.Equal(1, dataSource.ListCallCount);

        Assert.Null(viewModel.SelectedLore);

    }

    [Fact]
    public async Task Refresh_Failure_SetsLastError_DoesNotThrow()
    {

        FakeLoreDataSource dataSource = new()
        {

            ListResult = new DataSourceResult<ListPageResult<LoreDto>>(null, false, "Lore.Failed", "boom"),

        };

        LoreBrowserViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal("boom", viewModel.LastError);

        Assert.Empty(viewModel.Lore);

    }

    private static LoreBrowserViewModel NewViewModel(FakeLoreDataSource dataSource) =>
        new(dataSource, new FoundryFloorViewModel(new NullLogService()));

    private sealed class FakeLoreDataSource : ILoreDataSource
    {

        public DataSourceResult<ListPageResult<LoreDto>> ListResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<LoreDto> UpsertResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<bool> DeleteResult { get; init; } =
            new(true, true, null, null);

        public DataSourceResult<LoreDto> GetResult { get; init; } =
            new(null, true, null, null);

        public int ListCallCount { get; private set; }

        public string? LastUpsertKey { get; private set; }

        public string? LastUpsertValue { get; private set; }

        public string? LastDeleteKey { get; private set; }

        public Task<DataSourceResult<ListPageResult<LoreDto>>> ListAsync(CancellationToken cancellationToken)
        {

            ListCallCount++;

            return Task.FromResult(ListResult);

        }

        public Task<DataSourceResult<LoreDto>> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(GetResult);

        public Task<DataSourceResult<LoreDto>> UpsertAsync(string key, string value, CancellationToken cancellationToken)
        {

            LastUpsertKey = key;

            LastUpsertValue = value;

            return Task.FromResult(UpsertResult);

        }

        public Task<DataSourceResult<bool>> DeleteAsync(string key, CancellationToken cancellationToken)
        {

            LastDeleteKey = key;

            return Task.FromResult(DeleteResult);

        }

    }

}
