using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class CodexViewModelTests
{

    private static readonly Guid CampaignId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task Load_Campaign()
    {

        CodexContentDto dto = new("campaigns/foo/CODEX.md", "# Campaign Codex", true);

        FakeCodexDataSource dataSource = new()
        {

            CampaignGetResult = new DataSourceResult<CodexContentDto>(dto, true, null, null),

        };

        CodexViewModel viewModel = NewViewModel(CampaignId, dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("# Campaign Codex", viewModel.Content);

        Assert.True(viewModel.Exists);

        Assert.Equal(CampaignId, dataSource.LastCampaignId);

    }

    [Fact]
    public async Task Load_Global()
    {

        CodexContentDto dto = new("CODEX.md", "# Global Codex", true);

        FakeCodexDataSource dataSource = new()
        {

            GlobalGetResult = new DataSourceResult<CodexContentDto>(dto, true, null, null),

        };

        CodexViewModel viewModel = NewViewModel(null, dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("# Global Codex", viewModel.Content);

        Assert.True(viewModel.IsGlobal);

        Assert.True(dataSource.GlobalGetCalled);

    }

    [Fact]
    public async Task Load_ExistsFalse_EmptyEditor()
    {

        CodexContentDto dto = new("CODEX.md", string.Empty, false);

        FakeCodexDataSource dataSource = new()
        {

            GlobalGetResult = new DataSourceResult<CodexContentDto>(dto, true, null, null),

        };

        CodexViewModel viewModel = NewViewModel(null, dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(string.Empty, viewModel.Content);

        Assert.False(viewModel.Exists);

        Assert.Contains("empty editor ready", viewModel.StatusText ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Save_CallsPut()
    {

        CodexContentDto saved = new("CODEX.md", "# Saved", true);

        FakeCodexDataSource dataSource = new()
        {

            GlobalPutResult = new DataSourceResult<CodexContentDto>(saved, true, null, null),

        };

        CodexViewModel viewModel = NewViewModel(null, dataSource);

        viewModel.Content = "# Saved";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.Equal("# Saved", dataSource.LastPutContent);

        Assert.True(dataSource.GlobalPutCalled);

        Assert.Equal("Saved.", viewModel.StatusText);

    }

    [Fact]
    public async Task Delete_CallsDelete()
    {

        FakeCodexDataSource dataSource = new()
        {

            CampaignDeleteResult = new DataSourceResult<bool>(true, true, null, null),

        };

        CodexViewModel viewModel = NewViewModel(CampaignId, dataSource);

        await viewModel.DeleteAsync(CancellationToken.None);

        Assert.Equal(CampaignId, dataSource.LastCampaignId);

        Assert.True(dataSource.CampaignDeleteCalled);

        Assert.Equal(string.Empty, viewModel.Content);

        Assert.False(viewModel.Exists);

    }

    private static CodexViewModel NewViewModel(Guid? campaignId, FakeCodexDataSource dataSource) =>
        new(campaignId, dataSource, new FoundryFloorViewModel(new NullLogService()));

    private sealed class FakeCodexDataSource : ICodexDataSource
    {

        public DataSourceResult<CodexContentDto> CampaignGetResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<CodexContentDto> CampaignPutResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<bool> CampaignDeleteResult { get; init; } =
            new(true, true, null, null);

        public DataSourceResult<CodexContentDto> GlobalGetResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<CodexContentDto> GlobalPutResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<bool> GlobalDeleteResult { get; init; } =
            new(true, true, null, null);

        public Guid? LastCampaignId { get; private set; }

        public string? LastPutContent { get; private set; }

        public bool GlobalGetCalled { get; private set; }

        public bool GlobalPutCalled { get; private set; }

        public bool CampaignDeleteCalled { get; private set; }

        public Task<DataSourceResult<CodexContentDto>> GetCampaignCodexAsync(Guid campaignId, CancellationToken cancellationToken)
        {

            LastCampaignId = campaignId;

            return Task.FromResult(CampaignGetResult);

        }

        public Task<DataSourceResult<CodexContentDto>> PutCampaignCodexAsync(Guid campaignId, string content, CancellationToken cancellationToken)
        {

            LastCampaignId = campaignId;

            LastPutContent = content;

            return Task.FromResult(CampaignPutResult);

        }

        public Task<DataSourceResult<bool>> DeleteCampaignCodexAsync(Guid campaignId, CancellationToken cancellationToken)
        {

            LastCampaignId = campaignId;

            CampaignDeleteCalled = true;

            return Task.FromResult(CampaignDeleteResult);

        }

        public Task<DataSourceResult<CodexContentDto>> GetGlobalCodexAsync(CancellationToken cancellationToken)
        {

            GlobalGetCalled = true;

            return Task.FromResult(GlobalGetResult);

        }

        public Task<DataSourceResult<CodexContentDto>> PutGlobalCodexAsync(string content, CancellationToken cancellationToken)
        {

            GlobalPutCalled = true;

            LastPutContent = content;

            return Task.FromResult(GlobalPutResult);

        }

        public Task<DataSourceResult<bool>> DeleteGlobalCodexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(GlobalDeleteResult);

    }

}
