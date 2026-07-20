using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class AtelierOpenHonestyTests
{

    [Fact]
    public void HasPrimaryCommand_IsFalseForCategoryRoots()
    {

        CampaignsRootNodeViewModel root = new(new NullCampaignCommandCoordinator(), static _ => Task.CompletedTask);

        Assert.False(root.HasPrimaryCommand);

        Assert.Null(root.PrimaryCommand);

    }

    [Fact]
    public async Task CampaignPrimaryCommand_OpensCodexAndSetsActiveCampaign()
    {

        CampaignDto campaign = new(
            Guid.NewGuid(),
            "Autumnfall",
            "/campaigns/autumnfall",
            WorkspaceType.Campaign,
            null,
            CampaignSettings.CreateDefault(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        FakeActiveCampaignService active = new();

        NavigationService navigation = new();

        (DocumentKind Kind, string Id)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        CampaignNodeViewModel node = new(
            campaign,
            new NullAtelierDataSource(),
            navigation,
            active,
            new NullArtifactCreationDataSource(),
            new NullArtifactCreationDialogService(),
            new FoundryFloorViewModel(new NullLogService()),
            new NullCampaignManagementDataSource(),
            new NullCampaignDialogService(),
            new NullConfirmationDialogService(),
            new NullArtifactFileDialogService(),
            new FakeWhispersService(),
            static _ => Task.CompletedTask);

        Assert.True(node.HasPrimaryCommand);

        Assert.NotNull(node.PrimaryCommand);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)node.PrimaryCommand!).ExecuteAsync(null);

        Assert.Equal(campaign.Id, active.ActiveCampaign?.Id);

        Assert.Equal((DocumentKind.Codex, campaign.Id.ToString("D")), opened);

    }

    private sealed class NullCampaignManagementDataSource : ICampaignManagementDataSource
    {

        public Task<DataSourceResult<CampaignDto>> CreateAsync(RegisterCampaignRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<CampaignDto>(null, false, "test", "not used"));

        public Task<DataSourceResult<CampaignDto>> UpdateAsync(Guid id, UpdateCampaignRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<CampaignDto>(null, false, "test", "not used"));

        public Task<DataSourceResult<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<bool>(false, false, "test", "not used"));

        public Task<DataSourceResult<CampaignExportDto>> ExportAsync(Guid campaignId, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<CampaignExportDto>(null, false, "test", "not used"));

        public Task<DataSourceResult<CampaignImportResultDto>> ImportAsync(
            Guid campaignId,
            string strategy,
            CampaignExportDto payload,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<CampaignImportResultDto>(null, false, "test", "not used"));

    }

    private sealed class NullCampaignDialogService : ICampaignDialogService
    {

        public Task<NewCampaignInputs?> PromptNewCampaignAsync(
            NewCampaignDialogOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<NewCampaignInputs?>(null);

        public Task<string?> PromptOpenCampaignPathAsync(
            bool allowLocalFolderBrowse,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<EditCampaignInputs?> PromptEditCampaignAsync(CampaignDto existing, CancellationToken cancellationToken) =>
            Task.FromResult<EditCampaignInputs?>(null);

        public Task<string?> PromptImportStrategyAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

    }

}
