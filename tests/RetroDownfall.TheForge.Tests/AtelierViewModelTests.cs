using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class AtelierViewModelTests
{

    [Fact]
    public async Task RefreshAsync_CreatesFiveRootNodes()
    {

        FakeAtelierDataSource dataSource = new();

        NavigationService navigation = new();

        AtelierViewModel viewModel = CreateAtelier(dataSource, navigation);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal(
            ["Campaigns", "Workspaces", "Global Spells", "Global Prompts", "Sessions"],
            viewModel.Roots.Select(static r => r.Label).ToArray());

    }

    [Fact]
    public async Task ExpandCampaignsRoot_LoadsCampaignNodes()
    {

        FakeAtelierDataSource dataSource = new()
        {
            Campaigns =
            [
                NewCampaign("First Campaign"),
                NewCampaign("Second Campaign"),
            ],
        };

        NavigationService navigation = new();

        AtelierViewModel viewModel = CreateAtelier(dataSource, navigation);

        await viewModel.RefreshAsync(CancellationToken.None);

        AtelierNodeViewModel campaignsRoot = viewModel.Roots.Single(static r => r.Label == "Campaigns");

        await campaignsRoot.ExpandAsync(CancellationToken.None);

        Assert.Equal(["First Campaign", "Second Campaign"], campaignsRoot.Children.Select(static child => child.Label).ToArray());

        Assert.All(campaignsRoot.Children, static child => Assert.IsType<CampaignNodeViewModel>(child));

    }

    [Fact]
    public async Task ExpandCampaignNode_LoadsSpellsPromptsSessionsCodexAndSanctum()
    {

        Guid campaignId = Guid.NewGuid();

        FakeAtelierDataSource dataSource = new()
        {
            Campaigns = [NewCampaign("Autumnfall", campaignId)],
            CampaignSpells = [new SpellSummary("heal", "Restore mana", SpellSource.Campaign, ["support"])],
            CampaignPrompts = [new PromptSummaryDto(Guid.NewGuid(), campaignId, "briefing", "v1", "Mission setup", [], DateTimeOffset.UtcNow)],
            CampaignSessions = [new SessionSummaryDto(Guid.NewGuid(), campaignId, "First Tome", "Active", 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
        };

        NavigationService navigation = new();

        AtelierViewModel viewModel = CreateAtelier(dataSource, navigation);

        await viewModel.RefreshAsync(CancellationToken.None);

        AtelierNodeViewModel campaignsRoot = viewModel.Roots.Single(static r => r.Label == "Campaigns");

        await campaignsRoot.ExpandAsync(CancellationToken.None);

        CampaignNodeViewModel campaign = Assert.IsType<CampaignNodeViewModel>(campaignsRoot.Children.Single());

        await campaign.ExpandAsync(CancellationToken.None);

        Assert.Equal(["Spells", "Prompts", "Sessions", "CODEX.md", "Sanctum"], campaign.Children.Select(static child => child.Label).ToArray());

        Assert.Contains("heal", campaign.Children[0].Children.Select(static child => child.Label));

        Assert.Contains("briefing v1", campaign.Children[1].Children.Select(static child => child.Label));

        Assert.Contains("First Tome", campaign.Children[2].Children.Select(static child => child.Label));

    }

    [Fact]
    public async Task GlobalSpellLeaf_OpenCommand_NavigatesToSpellDocument()
    {

        FakeAtelierDataSource dataSource = new()
        {
            GlobalSpells = [new SpellSummary("summon-light", "Create light", SpellSource.Builtin, [])],
        };

        NavigationService navigation = new();

        (DocumentKind Kind, string Id)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        AtelierViewModel viewModel = CreateAtelier(dataSource, navigation);

        await viewModel.RefreshAsync(CancellationToken.None);

        AtelierNodeViewModel globalSpellsRoot = viewModel.Roots.Single(static r => r.Label == "Global Spells");

        await globalSpellsRoot.ExpandAsync(CancellationToken.None);

        SpellNodeViewModel spell = Assert.IsType<SpellNodeViewModel>(globalSpellsRoot.Children.Single());

        spell.OpenCommand.Execute(null);

        Assert.Equal((DocumentKind.Spell, "summon-light"), opened);

    }

    [Fact]
    public async Task GlobalPromptsRoot_LoadsGlobalPromptLeaves()
    {

        FakeAtelierDataSource dataSource = new()
        {
            GlobalPrompts = [new PromptSummaryDto(Guid.NewGuid(), null, "greeting", "v1", "Say hello", [], DateTimeOffset.UtcNow)],
        };

        NavigationService navigation = new();

        AtelierViewModel viewModel = CreateAtelier(dataSource, navigation);

        await viewModel.RefreshAsync(CancellationToken.None);

        AtelierNodeViewModel globalPromptsRoot = viewModel.Roots.Single(static r => r.Label == "Global Prompts");

        Assert.IsType<GlobalPromptsRootNodeViewModel>(globalPromptsRoot);

        Assert.True(globalPromptsRoot.HasNewPrompt);

        await globalPromptsRoot.ExpandAsync(CancellationToken.None);

        PromptNodeViewModel prompt = Assert.IsType<PromptNodeViewModel>(globalPromptsRoot.Children.Single());

        Assert.Equal("greeting v1", prompt.Label);

    }

    [Fact]
    public async Task SessionLeaf_OpenCommand_NavigatesToSessionDocument()
    {

        Guid sessionId = Guid.NewGuid();

        FakeAtelierDataSource dataSource = new()
        {
            Sessions = [new SessionSummaryDto(sessionId, null, null, "Active", 3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
        };

        NavigationService navigation = new();

        (DocumentKind Kind, string Id)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        AtelierViewModel viewModel = CreateAtelier(dataSource, navigation);

        await viewModel.RefreshAsync(CancellationToken.None);

        AtelierNodeViewModel sessionsRoot = viewModel.Roots.Single(static r => r.Label == "Sessions");

        await sessionsRoot.ExpandAsync(CancellationToken.None);

        SessionNodeViewModel session = Assert.IsType<SessionNodeViewModel>(sessionsRoot.Children.Single());

        session.OpenCommand.Execute(null);

        Assert.Equal((DocumentKind.Session, sessionId.ToString()), opened);

    }

    [Fact]
    public async Task FocusCampaignAsync_MissingCanonicalCampaign_ReturnsFalse()
    {

        Guid existingCampaignId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        Guid missingCampaignId = Guid.Parse("99999999-9999-9999-9999-999999999999");

        FakeAtelierDataSource dataSource = new()
        {

            Campaigns = [NewCampaign("Existing", existingCampaignId)],

        };

        AtelierViewModel viewModel = CreateAtelier(
            dataSource,
            new NavigationService());

        bool focused = await viewModel.FocusCampaignAsync(
            missingCampaignId,
            CancellationToken.None);

        Assert.False(focused);

        Assert.Null(viewModel.SelectedNode);

    }

    [Fact]
    public async Task FocusCampaignAsync_CanonicalCampaignOutsideLoadedPage_UsesDirectDetail()
    {

        Guid listedCampaignId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");

        Guid olderCampaignId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

        CampaignDto olderCampaign = NewCampaign("Older", olderCampaignId);

        FakeAtelierDataSource dataSource = new()
        {

            Campaigns = [NewCampaign("Listed", listedCampaignId)],

            DirectCampaigns =
            {

                [olderCampaignId] = olderCampaign,

            },

        };

        AtelierViewModel viewModel = CreateAtelier(
            dataSource,
            new NavigationService());

        bool focused = await viewModel.FocusCampaignAsync(
            olderCampaignId,
            CancellationToken.None);

        Assert.True(focused);

        CampaignNodeViewModel selected = Assert.IsType<CampaignNodeViewModel>(
            viewModel.SelectedNode);

        Assert.Equal(olderCampaignId, selected.Campaign.Id);

        Assert.Equal([olderCampaignId], dataSource.RequestedCampaignIds);

    }

    [Fact]
    public async Task SelectingCampaignNode_ReportsAFailedActiveCampaignWrite()
    {

        Guid campaignId = Guid.NewGuid();

        FakeAtelierDataSource dataSource = new()
        {
            Campaigns = [NewCampaign("Autumnfall", campaignId)],
        };

        FakeActiveCampaignService activeCampaign = new()
        {
            SetFailure = new IOException("the settings volume is read-only"),
        };

        FakeWhispersService whispers = new();

        FoundryFloorViewModel foundryFloor = new(new NullLogService());

        AtelierViewModel viewModel = CreateAtelier(
            dataSource,
            new NavigationService(),
            activeCampaign,
            whispers,
            foundryFloor);

        await viewModel.RefreshAsync(CancellationToken.None);

        AtelierNodeViewModel campaignsRoot = viewModel.Roots.Single(static r => r.Label == "Campaigns");

        await campaignsRoot.ExpandAsync(CancellationToken.None);

        viewModel.SelectedNode = campaignsRoot.Children[0];

        for (int attempt = 0; attempt < 100 && whispers.Calls.Count == 0; attempt++)
        {

            await Task.Delay(10);

        }

        Assert.Contains(whispers.Calls, static call => call.Severity == WhisperSeverity.Error);

        Assert.Contains(foundryFloor.Lines, static line => line.Contains("read-only", StringComparison.Ordinal));

    }

    private static AtelierViewModel CreateAtelier(
        FakeAtelierDataSource dataSource,
        NavigationService navigation,
        FakeActiveCampaignService? activeCampaign = null,
        FakeWhispersService? whispers = null,
        FoundryFloorViewModel? foundryFloor = null)
    {

        return new AtelierViewModel(
            dataSource,
            navigation,
            activeCampaign ?? new FakeActiveCampaignService(),
            new NullCampaignCommandCoordinator(),
            new NullArtifactCreationDataSource(),
            new NullArtifactCreationDialogService(),
            new NullCampaignManagementDataSource(),
            new NullCampaignDialogService(),
            new NullConfirmationDialogService(),
            new NullArtifactFileDialogService(),
            whispers ?? new FakeWhispersService(),
            foundryFloor ?? new FoundryFloorViewModel(new NullLogService()),
            new ConnectedArcanumConnection(),
            ImmediateTheForgeLocalMutationRunner.Instance);

    }

    private static CampaignDto NewCampaign(string name, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), name, $"/campaigns/{name}", WorkspaceType.Campaign, null, CampaignSettings.CreateDefault(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class ConnectedArcanumConnection : IArcanumConnection
    {

        public ConnectionState State => ConnectionState.Connected;

        public HealthReportDto? LastReport => null;

        public InstanceMetadataDto? LastMeta => null;

        public string? LastErrorCode => null;

        public string? LastErrorMessage => null;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged
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

    private sealed class FakeAtelierDataSource : IAtelierDataSource
    {

        public IReadOnlyList<CampaignDto> Campaigns { get; init; } = [];

        public IReadOnlyList<WorkspaceInfo> Workspaces { get; init; } = [];

        public IReadOnlyList<SpellSummary> GlobalSpells { get; init; } = [];

        public IReadOnlyList<PromptSummaryDto> GlobalPrompts { get; init; } = [];

        public IReadOnlyList<SessionSummaryDto> Sessions { get; init; } = [];

        public IReadOnlyList<SpellSummary> CampaignSpells { get; init; } = [];

        public IReadOnlyList<PromptSummaryDto> CampaignPrompts { get; init; } = [];

        public IReadOnlyList<SessionSummaryDto> CampaignSessions { get; init; } = [];

        public Dictionary<Guid, CampaignDto> DirectCampaigns { get; } = [];

        public List<Guid> RequestedCampaignIds { get; } = [];

        public Task<IReadOnlyList<CampaignDto>> GetCampaignsAsync(CancellationToken cancellationToken) => Task.FromResult(Campaigns);

        public Task<CampaignDto?> GetCampaignAsync(Guid campaignId, CancellationToken cancellationToken)
        {

            RequestedCampaignIds.Add(campaignId);

            return Task.FromResult(
                DirectCampaigns.TryGetValue(campaignId, out CampaignDto? campaign)
                    ? campaign
                    : null);

        }

        public Task<IReadOnlyList<WorkspaceInfo>> GetWorkspacesAsync(CancellationToken cancellationToken) => Task.FromResult(Workspaces);

        public Task<IReadOnlyList<SpellSummary>> GetGlobalSpellsAsync(CancellationToken cancellationToken) => Task.FromResult(GlobalSpells);

        public Task<IReadOnlyList<PromptSummaryDto>> GetGlobalPromptsAsync(CancellationToken cancellationToken) => Task.FromResult(GlobalPrompts);

        public Task<IReadOnlyList<SessionSummaryDto>> GetRecentSessionsAsync(CancellationToken cancellationToken) => Task.FromResult(Sessions);

        public Task<IReadOnlyList<SpellSummary>> GetCampaignSpellsAsync(Guid campaignId, CancellationToken cancellationToken) => Task.FromResult(CampaignSpells);

        public Task<IReadOnlyList<PromptSummaryDto>> GetCampaignPromptsAsync(Guid campaignId, CancellationToken cancellationToken) => Task.FromResult(CampaignPrompts);

        public Task<IReadOnlyList<SessionSummaryDto>> GetCampaignSessionsAsync(Guid campaignId, CancellationToken cancellationToken) => Task.FromResult(CampaignSessions);

    }

}
