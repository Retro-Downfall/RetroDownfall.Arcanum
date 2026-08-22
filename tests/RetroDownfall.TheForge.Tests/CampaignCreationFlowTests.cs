using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class CampaignCreationFlowTests
{

    [Fact]
    public async Task NewSpellCommand_CreatesSpellInCampaignWorkspaceAndOpensEditor()
    {

        CampaignDto campaign = NewCampaign();

        FakeArtifactCreationDataSource creation = new() { SpellSuccess = true };

        FakeArtifactCreationDialogService dialog = new()
        {
            SpellInputs = new NewSpellInputs("light", null, "# Light", "ignored"),
        };

        NavigationService navigation = new();

        (DocumentKind, string, string?)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, workspace) => opened = (kind, id, workspace);

        CampaignNodeViewModel node = NewCampaignNode(campaign, creation, dialog, navigation);

        Assert.True(node.HasNewSpell);

        await node.NewSpellCommand!.ExecuteAsync(null);

        Assert.NotNull(creation.LastSpellRequest);

        Assert.Equal(campaign.Path, creation.LastSpellWorkspace);

        Assert.Equal("light", creation.LastSpellRequest!.Name);

        Assert.Equal((DocumentKind.Spell, "light", campaign.Path), opened);

    }

    [Fact]
    public async Task NewPromptCommand_CreatesCampaignPromptAndOpensScriptorium()
    {

        CampaignDto campaign = NewCampaign();

        Guid promptId = Guid.NewGuid();

        FakeArtifactCreationDataSource creation = new()
        {
            CreatedPrompt = new PromptDetailDto(
                promptId,
                campaign.Id,
                "briefing",
                "v1",
                null,
                [],
                "# briefing",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
        };

        FakeArtifactCreationDialogService dialog = new()
        {
            PromptInputs = new NewPromptInputs("briefing", "v1", null, "# briefing"),
        };

        NavigationService navigation = new();

        (DocumentKind, string)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        CampaignNodeViewModel node = NewCampaignNode(campaign, creation, dialog, navigation);

        Assert.True(node.HasNewPrompt);

        await node.NewPromptCommand!.ExecuteAsync(null);

        Assert.NotNull(creation.LastPromptRequest);

        Assert.Equal(campaign.Id, creation.LastPromptRequest!.CampaignId);

        Assert.Equal("briefing", creation.LastPromptRequest.Name);

        Assert.Equal((DocumentKind.Prompt, promptId.ToString()), opened);

    }

    [Fact]
    public async Task NewSessionCommand_CreatesCampaignSessionAndOpensTome()
    {

        CampaignDto campaign = NewCampaign();

        Guid sessionId = Guid.NewGuid();

        FakeArtifactCreationDataSource creation = new()
        {
            CreatedSession = new SessionDetailDto(
                sessionId,
                campaign.Id,
                "My Tome",
                "active",
                0,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                0),
        };

        FakeArtifactCreationDialogService dialog = new()
        {
            SessionInputs = new NewSessionInputs("My Tome"),
        };

        NavigationService navigation = new();

        (DocumentKind, string)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        CampaignNodeViewModel node = NewCampaignNode(campaign, creation, dialog, navigation);

        Assert.True(node.HasNewSession);

        await node.NewSessionCommand!.ExecuteAsync(null);

        Assert.NotNull(creation.LastSessionRequest);

        Assert.Equal(campaign.Id, creation.LastSessionRequest!.CampaignId);

        Assert.Equal((DocumentKind.Session, sessionId.ToString("D")), opened);

    }

    [Fact]
    public async Task NewSpellCommand_WhenDialogCancelled_DoesNotCreateOrNavigate()
    {

        CampaignDto campaign = NewCampaign();

        FakeArtifactCreationDataSource creation = new();

        FakeArtifactCreationDialogService dialog = new() { SpellInputs = null };

        NavigationService navigation = new();

        (DocumentKind, string)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        CampaignNodeViewModel node = NewCampaignNode(campaign, creation, dialog, navigation);

        await node.NewSpellCommand!.ExecuteAsync(null);

        Assert.Null(creation.LastSpellRequest);

        Assert.Null(opened);

    }

    [Fact]
    public async Task NewSpellCommand_WhenCreateFails_SetsLastErrorAndDoesNotNavigate()
    {

        CampaignDto campaign = NewCampaign();

        FakeArtifactCreationDataSource creation = new() { SpellSuccess = false, SpellError = "boom" };

        FakeArtifactCreationDialogService dialog = new()
        {
            SpellInputs = new NewSpellInputs("light", null, "# Light", "ignored"),
        };

        NavigationService navigation = new();

        (DocumentKind, string)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        CampaignNodeViewModel node = NewCampaignNode(campaign, creation, dialog, navigation);

        await node.NewSpellCommand!.ExecuteAsync(null);

        Assert.NotNull(node.LastError);

        Assert.Null(opened);

        Assert.NotNull(creation.LastSpellRequest);

    }

    private static CampaignDto NewCampaign() =>
        new(
            Guid.NewGuid(),
            "Autumnfall",
            "/campaigns/autumnfall",
            WorkspaceType.Campaign,
            null,
            CampaignSettings.CreateDefault(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static CampaignNodeViewModel NewCampaignNode(
        CampaignDto campaign,
        FakeArtifactCreationDataSource creation,
        FakeArtifactCreationDialogService dialog,
        NavigationService navigation)
    {

        FoundryFloorViewModel foundryFloor = new(new NullLogService());

        return new CampaignNodeViewModel(
            campaign,
            new NullAtelierDataSource(),
            navigation,
            new FakeActiveCampaignService(),
            creation,
            dialog,
            foundryFloor,
            new NullCampaignManagementDataSource(),
            new NullCampaignDialogService(),
            new NullConfirmationForCampaignCreation(),
            new NullFileDialogForCampaignCreation(),
            new FakeWhispersService(),
            ImmediateTheForgeLocalMutationRunner.Instance,
            static _ => Task.CompletedTask);

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

    private sealed class NullConfirmationForCampaignCreation : IConfirmationDialogService
    {

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            CancellationToken cancellationToken,
            bool confirmIsDefault = true) =>
            Task.FromResult(false);

    }

    private sealed class NullFileDialogForCampaignCreation : IArtifactFileDialogService
    {

        public Task<string?> PickSaveJsonPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickOpenJsonPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveCsvPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
        public Task<string?> PickOpenAnyPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveAnyPathAsync(string suggestedFileName, string? defaultExtension, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);


    }

    private sealed class FakeArtifactCreationDataSource : IArtifactCreationDataSource
    {

        public bool SpellSuccess { get; init; } = true;

        public string? SpellError { get; init; }

        public PromptDetailDto? CreatedPrompt { get; init; }

        public SessionDetailDto? CreatedSession { get; init; }

        public IReadOnlyList<WorkspaceOption> WorkspaceOptions { get; init; } = [];

        public string? LastSpellWorkspace { get; private set; }

        public CreateSpellRequest? LastSpellRequest { get; private set; }

        public CreatePromptRequest? LastPromptRequest { get; private set; }

        public CreateSessionRequest? LastSessionRequest { get; private set; }

        public Task<(bool Success, string? Error)> CreateSpellAsync(string workspacePath, CreateSpellRequest request, CancellationToken cancellationToken)
        {

            LastSpellWorkspace = workspacePath;

            LastSpellRequest = request;

            return Task.FromResult((SpellSuccess, SpellError));

        }

        public Task<(PromptDetailDto? Prompt, string? Error)> CreatePromptAsync(CreatePromptRequest request, CancellationToken cancellationToken)
        {

            LastPromptRequest = request;

            return Task.FromResult<(PromptDetailDto? Prompt, string? Error)>((CreatedPrompt, null));

        }

        public Task<(SessionDetailDto? Session, string? Error)> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken)
        {

            LastSessionRequest = request;

            return Task.FromResult<(SessionDetailDto? Session, string? Error)>((CreatedSession, null));

        }

        public Task<IReadOnlyList<WorkspaceOption>> ListWorkspaceOptionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(WorkspaceOptions);

    }

    private sealed class FakeArtifactCreationDialogService : IArtifactCreationDialogService
    {

        public NewSpellInputs? SpellInputs { get; init; }

        public NewPromptInputs? PromptInputs { get; init; }

        public NewSessionInputs? SessionInputs { get; init; }

        public Task<NewSpellInputs?> PromptNewSpellAsync(
            IReadOnlyList<WorkspaceOption> workspaces,
            WorkspaceOption? preselected,
            CancellationToken cancellationToken)
        {

            NewSpellInputs? inputs = SpellInputs is null
                ? null
                : SpellInputs with { WorkspacePath = preselected?.Path ?? SpellInputs.WorkspacePath };

            return Task.FromResult(inputs);

        }

        public Task<NewPromptInputs?> PromptNewPromptAsync(Guid? campaignId, string? campaignName, CancellationToken cancellationToken) =>
            Task.FromResult(PromptInputs);

        public Task<NewSessionInputs?> PromptNewSessionAsync(Guid? campaignId, string? campaignName, CancellationToken cancellationToken) =>
            Task.FromResult(SessionInputs);

    }

}
