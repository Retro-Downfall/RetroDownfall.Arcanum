using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class GlobalRootCreationTests
{

    [Fact]
    public async Task GlobalSpellsRoot_NewSpellCommand_CreatesWorkspaceSpellAndOpensEditor()
    {

        FakeArtifactCreationDataSource creation = new()
        {
            SpellSuccess = true,
            WorkspaceOptions = [new WorkspaceOption("/ws/picked", "Picked")],
        };

        FakeArtifactCreationDialogService dialog = new()
        {
            SpellInputs = new NewSpellInputs("light", null, "# Light", "/ws/picked"),
        };

        NavigationService navigation = new();

        (DocumentKind, string, string?)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, workspace) => opened = (kind, id, workspace);

        GlobalSpellsRootNodeViewModel root = NewGlobalSpellsRoot(creation, dialog, navigation);

        Assert.True(root.HasNewSpell);

        Assert.False(root.HasNewPrompt);

        Assert.False(root.HasNewSession);

        Assert.Equal("New Workspace Spell…", root.NewSpellLabel);

        await root.NewSpellCommand!.ExecuteAsync(null);

        Assert.Equal("/ws/picked", creation.LastSpellWorkspace);

        Assert.Equal((DocumentKind.Spell, "light", "/ws/picked"), opened);

    }

    [Fact]
    public async Task GlobalPromptsRoot_NewPromptCommand_CreatesGlobalPromptAndOpensScriptorium()
    {

        Guid promptId = Guid.NewGuid();

        FakeArtifactCreationDataSource creation = new()
        {
            CreatedPrompt = new PromptDetailDto(
                promptId,
                null,
                "greeting",
                "v1",
                null,
                [],
                "# greeting",
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
            PromptInputs = new NewPromptInputs("greeting", "v1", null, "# greeting"),
        };

        NavigationService navigation = new();

        (DocumentKind, string)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        GlobalPromptsRootNodeViewModel root = NewGlobalPromptsRoot(creation, dialog, navigation);

        Assert.True(root.HasNewPrompt);

        Assert.False(root.HasNewSpell);

        await root.NewPromptCommand!.ExecuteAsync(null);

        Assert.Null(creation.LastPromptRequest!.CampaignId);

        Assert.Equal((DocumentKind.Prompt, promptId.ToString()), opened);

    }

    [Fact]
    public async Task SessionsRoot_NewSessionCommand_CreatesNoCampaignSessionAndOpensTome()
    {

        Guid sessionId = Guid.NewGuid();

        FakeArtifactCreationDataSource creation = new()
        {
            CreatedSession = new SessionDetailDto(
                sessionId,
                null,
                "Standalone",
                "active",
                0,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                0),
        };

        FakeArtifactCreationDialogService dialog = new()
        {
            SessionInputs = new NewSessionInputs("Standalone"),
        };

        NavigationService navigation = new();

        (DocumentKind, string)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        SessionsRootNodeViewModel root = NewSessionsRoot(creation, dialog, navigation);

        Assert.True(root.HasNewSession);

        await root.NewSessionCommand!.ExecuteAsync(null);

        Assert.Null(creation.LastSessionRequest!.CampaignId);

        Assert.Equal((DocumentKind.Session, sessionId.ToString("D")), opened);

    }

    private static GlobalSpellsRootNodeViewModel NewGlobalSpellsRoot(
        FakeArtifactCreationDataSource creation,
        FakeArtifactCreationDialogService dialog,
        NavigationService navigation) =>
        new(new NullAtelierDataSource(), navigation, creation, dialog, new FoundryFloorViewModel(new NullLogService()));

    private static GlobalPromptsRootNodeViewModel NewGlobalPromptsRoot(
        FakeArtifactCreationDataSource creation,
        FakeArtifactCreationDialogService dialog,
        NavigationService navigation) =>
        new(new NullAtelierDataSource(), navigation, creation, dialog, new FoundryFloorViewModel(new NullLogService()));

    private static SessionsRootNodeViewModel NewSessionsRoot(
        FakeArtifactCreationDataSource creation,
        FakeArtifactCreationDialogService dialog,
        NavigationService navigation) =>
        new(new NullAtelierDataSource(), navigation, creation, dialog, new FoundryFloorViewModel(new NullLogService()));

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
