using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Diff;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class SpellEditorViewModelTests
{

    [Fact]
    public async Task LoadAsync_PopulatesSpellBodyAndMetadata()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            Versions = [new SpellVersionDto("v1", true, DateTimeOffset.UtcNow, "Initial")],
        };

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("mend-armor", viewModel.Spell?.Name);

        Assert.Equal("# Mend Armor", viewModel.MarkdownBody);

        Assert.Contains("Description: Repairs armor", viewModel.Frontmatter);

        Assert.Contains("repair", viewModel.SpellJson);

        Assert.Single(viewModel.Versions);

    }

    [Fact]
    public async Task SaveAsync_SendsUpdateRequestFromEditedState()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
        };

        FakeWhispersService whispers = new();

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource, whispers: whispers);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.MarkdownBody = "# Updated Mend Armor";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.NotNull(dataSource.LastUpdateRequest);

        Assert.Equal("# Updated Mend Armor", dataSource.LastUpdateRequest.SystemPrompt);

        Assert.NotNull(dataSource.LastUpdateRequest.Tags);

        Assert.Equal(["repair", "armor"], dataSource.LastUpdateRequest.Tags);

        (WhisperSeverity Severity, string Message, string? Title) whisper = Assert.Single(whispers.Calls);

        Assert.Equal(WhisperSeverity.Success, whisper.Severity);

        Assert.Equal("Spell saved.", whisper.Message);

    }

    [Fact]
    public async Task SaveAsync_WhenRejected_ShowsShortErrorWhisper()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            SaveSucceeds = false,
        };

        FakeWhispersService whispers = new();

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource, whispers: whispers);

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.Equal("Save failed.", viewModel.StatusText);

        (WhisperSeverity Severity, string Message, string? Title) whisper = Assert.Single(whispers.Calls);

        Assert.Equal(WhisperSeverity.Error, whisper.Severity);

        Assert.Equal("Spell save failed.", whisper.Message);

        Assert.DoesNotContain("rejected", whisper.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task CastAsync_StoresDryRunPreview()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            CastResult = new SpellCastResult("mend-armor", "Repairs armor", "system prompt", ["smithing"], ["tool-a"], ["script.sh"], "codex"),
        };

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.CastAsync(CancellationToken.None);

        Assert.NotNull(viewModel.CastPreview);

        Assert.Equal("system prompt", viewModel.CastPreview.SystemPrompt);

    }

    [Fact]
    public async Task EstimateManaAsync_StoresManaCount()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            ManaCount = new ManaCountResult(42, "o200k_base"),
        };

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.EstimateManaAsync(CancellationToken.None);

        Assert.Equal(42, viewModel.ManaCount);

    }

    [Fact]
    public async Task ExecuteAsync_StreamsEventsAndOpensSessionWhenSessionBound()
    {

        NavigationService navigation = new();

        (DocumentKind Kind, string Id)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            ExecutionEvents =
            [
                new IntelligenceEvent(IntelligenceEventType.Token, "", "hello"),
                new IntelligenceEvent(IntelligenceEventType.SessionBound, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa").ToString()),
            ],
        };

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource, navigation);

        viewModel.ExecutionPrompt = "repair this";

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.ExecuteAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.ExecutionEvents.Count);

        Assert.Equal((DocumentKind.Session, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), opened);

        Assert.NotNull(dataSource.LastExecuteRequest);

        Assert.Equal("repair this", dataSource.LastExecuteRequest.Prompt);

    }

    [Fact]
    public async Task ActivateVersionAsync_SendsVersionAndReloads()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            Versions = [new SpellVersionDto("v2", false, DateTimeOffset.UtcNow, "Second")],
            ActivateResult = new SpellVersionDto("v2", true, DateTimeOffset.UtcNow, "Second", PreviousVersion: "v1"),
        };

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.ActivateVersionAsync(viewModel.Versions.Single(), CancellationToken.None);

        Assert.Equal("v2", dataSource.ActivatedVersion);

        Assert.Equal(2, dataSource.LoadCallCount);

        Assert.Contains("v1", viewModel.ActivatePreviousVersionNote ?? string.Empty);

    }

    [Fact]
    public async Task Mirror_SelectingVersion_ComparesAgainstPersistedActiveBody_NotDirtyEditor()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            Versions = [new SpellVersionDto("v2", false, DateTimeOffset.UtcNow, "Second")],
            VersionDetails =
            {
                ["v2"] = new SpellVersionDetailDto("v2", false, DateTimeOffset.UtcNow, "Second", "# Version Two"),
            },
        };

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.MarkdownBody = "# Dirty editor buffer";

        Assert.True(viewModel.IsEditorDirty);

        Assert.True(viewModel.ShowDirtyComparisonWarning);

        viewModel.SelectedVersion = viewModel.Versions.Single();

        await viewModel.RefreshMirrorDiffAsync(CancellationToken.None);

        Assert.Equal(1, dataSource.GetVersionDetailCallCount);

        Assert.DoesNotContain(
            viewModel.DiffLines,
            static line => line.Text.Contains("Dirty editor buffer", StringComparison.Ordinal));

        Assert.Contains(viewModel.DiffLines, static line => line.Kind == LineDiffKind.Added && line.Text == "# Version Two");

        Assert.Contains(viewModel.DiffLines, static line => line.Kind == LineDiffKind.Removed && line.Text == "# Mend Armor");

        Assert.True(viewModel.ShowDirtyComparisonWarning);

    }

    [Fact]
    public async Task Mirror_DirtyWarning_DoesNotBlockVersionSelection()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            Versions =
            [
                new SpellVersionDto("v1", true, DateTimeOffset.UtcNow, "Initial"),
                new SpellVersionDto("v2", false, DateTimeOffset.UtcNow, "Second"),
            ],
            VersionDetails =
            {
                ["v1"] = new SpellVersionDetailDto("v1", true, DateTimeOffset.UtcNow, "Initial", "# Mend Armor"),
                ["v2"] = new SpellVersionDetailDto("v2", false, DateTimeOffset.UtcNow, "Second", "# Version Two"),
            },
        };

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.MarkdownBody = "# Dirty";

        viewModel.SelectedVersion = viewModel.Versions[0];

        await viewModel.RefreshMirrorDiffAsync(CancellationToken.None);

        viewModel.SelectedVersion = viewModel.Versions[1];

        await viewModel.RefreshMirrorDiffAsync(CancellationToken.None);

        Assert.Equal(2, dataSource.GetVersionDetailCallCount);

        Assert.Equal("v2", viewModel.SelectedVersion?.Version);

        Assert.True(viewModel.ShowDirtyComparisonWarning);

    }

    [Fact]
    public async Task Mirror_CreateVersion_SendsMarkdownBodyOnly()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            CreateVersionResult = new SpellVersionDto("3.0", false, DateTimeOffset.UtcNow, null),
        };

        ControllableTextInput textInput = new(["3.0"]);

        FakeWhispersService whispers = new();

        SpellEditorViewModel viewModel = NewViewModel(
            "mend-armor",
            dataSource,
            workspace: "/tmp/workspace",
            textInput: textInput,
            whispers: whispers);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.MarkdownBody = "# Editor body for version";

        await viewModel.CreateVersionAsync(CancellationToken.None);

        Assert.NotNull(dataSource.LastCreateVersionRequest);

        Assert.Equal("3.0", dataSource.LastCreateVersionRequest.Version);

        Assert.Equal("# Editor body for version", dataSource.LastCreateVersionRequest.Body);

        Assert.Equal("/tmp/workspace", dataSource.LastCreateVersionRequest.Workspace);

        (WhisperSeverity Severity, string Message, string? Title) whisper = Assert.Single(whispers.Calls);

        Assert.Equal(WhisperSeverity.Success, whisper.Severity);

        Assert.Equal("Version created.", whisper.Message);

    }

    [Fact]
    public async Task Mirror_UpdateVersion_SendsMarkdownBodyOnly()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            Versions = [new SpellVersionDto("v2", false, DateTimeOffset.UtcNow, "Second")],
            UpdateVersionResult = new SpellVersionDto("v2", false, DateTimeOffset.UtcNow, "Second"),
        };

        FakeWhispersService whispers = new();

        SpellEditorViewModel viewModel = NewViewModel(
            "mend-armor",
            dataSource,
            workspace: "/tmp/workspace",
            whispers: whispers);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.SelectedVersion = viewModel.Versions.Single();

        viewModel.MarkdownBody = "# Updated version body";

        await viewModel.UpdateSelectedVersionAsync(CancellationToken.None);

        Assert.NotNull(dataSource.LastUpdateVersionRequest);

        Assert.Equal("# Updated version body", dataSource.LastUpdateVersionRequest.Body);

        Assert.Equal("/tmp/workspace", dataSource.LastUpdateVersionRequest.Workspace);

        Assert.Equal("v2", dataSource.LastUpdatedVersion);

        (WhisperSeverity Severity, string Message, string? Title) whisper = Assert.Single(whispers.Calls);

        Assert.Equal(WhisperSeverity.Success, whisper.Severity);

        Assert.Equal("Version updated.", whisper.Message);

    }

    [Fact]
    public async Task Mirror_Builtin_DisablesMutation_AllowsCompare()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("heal") with { Source = SpellSource.Builtin },
            Versions = [new SpellVersionDto("v1", false, DateTimeOffset.UtcNow, "Draft")],
            VersionDetails =
            {
                ["v1"] = new SpellVersionDetailDto("v1", false, DateTimeOffset.UtcNow, "Draft", "# Heal draft"),
            },
        };

        SpellEditorViewModel viewModel = NewViewModel("heal", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.False(viewModel.CanMutateVersions);

        Assert.False(viewModel.CanActivateVersion);

        Assert.False(viewModel.CanCreateVersion);

        Assert.False(viewModel.CanUpdateVersion);

        viewModel.SelectedVersion = viewModel.Versions.Single();

        await viewModel.RefreshMirrorDiffAsync(CancellationToken.None);

        Assert.Equal(1, dataSource.GetVersionDetailCallCount);

        Assert.NotEmpty(viewModel.DiffLines);

        await viewModel.CreateVersionAsync(CancellationToken.None);

        Assert.Null(dataSource.LastCreateVersionRequest);

        await viewModel.UpdateSelectedVersionAsync(CancellationToken.None);

        Assert.Null(dataSource.LastUpdateVersionRequest);

        await viewModel.ActivateVersionAsync(viewModel.Versions.Single(), CancellationToken.None);

        Assert.Null(dataSource.ActivatedVersion);

    }

    [Fact]
    public async Task BuiltInSpell_DisablesSaveAndDelete_AllowsCloneAndExport()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("heal") with { Source = SpellSource.Builtin },
            ExportResult = new SpellExportDto(null, "# heal", []),
            CloneResult = new SpellSummary("heal-copy", null, SpellSource.Workspace, []),
        };

        ControllableFileDialog fileDialog = new("/tmp/heal.json");

        ControllableTextInput textInput = new(["heal-copy", "/ws/target"]);

        SpellEditorViewModel viewModel = NewViewModel(
            "heal",
            dataSource,
            fileDialog: fileDialog,
            textInput: textInput);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.True(viewModel.IsBuiltIn);

        Assert.False(viewModel.CanSave);

        Assert.False(viewModel.CanDelete);

        await viewModel.ExportAsync(CancellationToken.None);

        Assert.Equal(1, dataSource.ExportCallCount);

        await viewModel.CloneAsync(CancellationToken.None);

        Assert.NotNull(dataSource.LastCloneRequest);

    }

    [Fact]
    public async Task ExportAsync_WhenFileDialogCancelled_IsNoOp()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
        };

        ControllableFileDialog fileDialog = new(null);

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource, fileDialog: fileDialog);

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.ExportAsync(CancellationToken.None);

        Assert.Equal(0, dataSource.ExportCallCount);

        Assert.Null(viewModel.LastError);

    }

    [Fact]
    public async Task DeleteAsync_WhenConfirmationCancelled_IsNoOp()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
        };

        ControllableConfirmation confirmation = new(false);

        SpellEditorViewModel viewModel = NewViewModel(
            "mend-armor",
            dataSource,
            workspace: "/tmp/workspace",
            confirmation: confirmation);

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.DeleteAsync(CancellationToken.None);

        Assert.Equal(0, dataSource.DeleteCallCount);

        Assert.Null(viewModel.LastError);

    }

    [Fact]
    public async Task LoadAsync_PopulatesSpellMetadataDesigner()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail(
                "mend-armor",
                version: "1.4.0",
                declaredTools: ["tool-a"],
                dependencies: ["helper-spell"],
                activeVersion: "v2"),
            SpellNames = ["mend-armor", "helper-spell"],
            AvailableToolNames = ["tool-a"],
        };

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("1.4.0", viewModel.MetadataVersion);

        Assert.Equal("v2", viewModel.MetadataActiveVersion);

        Assert.Equal(["helper-spell"], viewModel.Dependencies);

        Assert.Equal(["tool-a"], viewModel.DeclaredTools);

        Assert.Contains("mend-armor", viewModel.SpellJson);

        Assert.Contains("1.4.0", viewModel.SpellJson);

        Assert.True(viewModel.CanEditMetadata);

        Assert.Null(viewModel.DependencyWarnings);

        Assert.Null(viewModel.ToolWarnings);

    }

    [Fact]
    public async Task SaveAsync_SendsDesignerMetadataFields()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor", version: "1.0.0"),
        };

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.MetadataVersion = "2.0.0";

        viewModel.NewDependencyText = "dep-spell";

        viewModel.AddDependencyCommand.Execute(null);

        viewModel.NewDeclaredToolText = "forge-tool";

        viewModel.AddDeclaredToolCommand.Execute(null);

        viewModel.InputSchemaJson = """{"type":"object"}""";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.NotNull(dataSource.LastUpdateRequest);

        Assert.Equal("2.0.0", dataSource.LastUpdateRequest.Version);

        Assert.NotNull(dataSource.LastUpdateRequest.Dependencies);

        Assert.NotNull(dataSource.LastUpdateRequest.DeclaredTools);

        Assert.Equal(["dep-spell"], dataSource.LastUpdateRequest.Dependencies);

        Assert.Equal(["forge-tool"], dataSource.LastUpdateRequest.DeclaredTools);

        Assert.NotNull(dataSource.LastUpdateRequest.InputSchema);

    }

    [Fact]
    public async Task SaveAsync_InvalidSchema_BlocksSave()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
        };

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.InputSchemaJson = "{not-json";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.Null(dataSource.LastUpdateRequest);

        Assert.False(string.IsNullOrWhiteSpace(viewModel.MetadataValidationError));

        Assert.Contains("Save blocked", viewModel.StatusText);

    }

    [Fact]
    public async Task AddAndRemoveDependencyAndDeclaredTool_UpdateCollections()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
        };

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.NewDependencyText = "dep-a";

        viewModel.AddDependencyCommand.Execute(null);

        viewModel.NewDeclaredToolText = "tool-b";

        viewModel.AddDeclaredToolCommand.Execute(null);

        Assert.Equal(["dep-a"], viewModel.Dependencies);

        Assert.Equal(["tool-b"], viewModel.DeclaredTools);

        viewModel.SelectedDependency = "dep-a";

        viewModel.RemoveDependencyCommand.Execute(null);

        viewModel.SelectedDeclaredTool = "tool-b";

        viewModel.RemoveDeclaredToolCommand.Execute(null);

        Assert.Empty(viewModel.Dependencies);

        Assert.Empty(viewModel.DeclaredTools);

    }

    [Fact]
    public async Task BuiltInSpell_MetadataIsReadOnly()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("builtin-spell", source: SpellSource.Builtin),
        };

        SpellEditorViewModel viewModel = NewViewModel("builtin-spell", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.False(viewModel.CanSave);

        Assert.False(viewModel.CanEditMetadata);

        viewModel.NewDependencyText = "should-not-add";

        viewModel.AddDependencyCommand.Execute(null);

        Assert.Empty(viewModel.Dependencies);

    }

    [Fact]
    public async Task LoadAsync_WarnsWhenCatalogLacksDependencyOrTool()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail(
                "mend-armor",
                declaredTools: ["missing-tool"],
                dependencies: ["missing-spell"]),
            SpellNames = ["mend-armor"],
            AvailableToolNames = ["tool-a"],
        };

        SpellEditorViewModel viewModel = NewViewModel("mend-armor", dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Contains("missing-spell", viewModel.DependencyWarnings);

        Assert.Contains("missing-tool", viewModel.ToolWarnings);

    }

    private static SpellEditorViewModel NewViewModel(
        string spellName,
        ISpellEditorDataSource dataSource,
        NavigationService? navigation = null,
        string? workspace = null,
        IConfirmationDialogService? confirmation = null,
        IArtifactFileDialogService? fileDialog = null,
        ITextInputDialogService? textInput = null,
        IWhispersService? whispers = null) =>
        new(
            spellName,
            dataSource,
            navigation ?? new NavigationService(),
            new FoundryFloorViewModel(new NullLogService()),
            confirmation ?? new NullConfirmationDialogService(),
            fileDialog ?? new NullArtifactFileDialogService(),
            textInput ?? new NullTextInputDialogService(),
            whispers ?? new FakeWhispersService(),
            workspace);

    private static SpellDetail NewSpellDetail(
        string name,
        SpellSource source = SpellSource.Workspace,
        string? version = null,
        string[]? declaredTools = null,
        string[]? dependencies = null,
        string? activeVersion = null) =>
        new(
            name,
            "Repairs armor",
            source,
            ["repair", "armor"],
            "# Mend Armor",
            null,
            "# Mend Armor",
            "gpt-4o",
            "openai",
            ["tool-a"],
            [],
            "/tmp/workspace",
            "/tmp/workspace/SPELL.md",
            Version: version,
            DeclaredTools: declaredTools,
            Dependencies: dependencies,
            ActiveVersion: activeVersion);

    private sealed class ControllableFileDialog(string? path) : IArtifactFileDialogService
    {

        public Task<string?> PickSaveJsonPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<string?> PickOpenJsonPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<string?> PickSaveCsvPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<string?> PickOpenAnyPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<string?> PickSaveAnyPathAsync(string suggestedFileName, string? defaultExtension, CancellationToken cancellationToken) =>
            Task.FromResult(path);

    }

    private sealed class ControllableTextInput(IReadOnlyList<string?> answers) : ITextInputDialogService
    {

        private int _index;

        public Task<string?> PromptAsync(string title, string label, string? defaultValue, CancellationToken cancellationToken)
        {

            string? answer = _index < answers.Count ? answers[_index] : null;

            _index++;

            return Task.FromResult(answer);

        }

    }

    private sealed class ControllableConfirmation(bool accept) : IConfirmationDialogService
    {

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken, bool confirmIsDefault = true) =>
            Task.FromResult(accept);

    }

    private sealed class FakeSpellEditorDataSource : ISpellEditorDataSource
    {

        public SpellDetail? Spell { get; init; }

        public IReadOnlyList<SpellVersionDto> Versions { get; init; } = [];

        public Dictionary<string, SpellVersionDetailDto> VersionDetails { get; } = new(StringComparer.Ordinal);

        public SpellCastResult? CastResult { get; init; }

        public ManaCountResult? ManaCount { get; init; }

        public IReadOnlyList<IntelligenceEvent> ExecutionEvents { get; init; } = [];

        public SpellExportDto? ExportResult { get; init; }

        public SpellSummary? CloneResult { get; init; }

        public SpellVersionDto? CreateVersionResult { get; init; }

        public SpellVersionDto? UpdateVersionResult { get; init; }

        public SpellVersionDto? ActivateResult { get; init; }

        public bool SaveSucceeds { get; init; } = true;

        public UpdateSpellRequest? LastUpdateRequest { get; private set; }

        public SpellExecuteRequest? LastExecuteRequest { get; private set; }

        public CloneSpellRequest? LastCloneRequest { get; private set; }

        public CreateSpellVersionRequest? LastCreateVersionRequest { get; private set; }

        public UpdateSpellVersionRequest? LastUpdateVersionRequest { get; private set; }

        public string? LastUpdatedVersion { get; private set; }

        public string? ActivatedVersion { get; private set; }

        public int LoadCallCount { get; private set; }

        public int ExportCallCount { get; private set; }

        public int DeleteCallCount { get; private set; }

        public int GetVersionDetailCallCount { get; private set; }

        public IReadOnlyList<string> SpellNames { get; init; } = [];

        public IReadOnlyList<string> AvailableToolNames { get; init; } = [];

        public Task<SpellDetail?> LoadSpellAsync(string name, string? workspace, CancellationToken cancellationToken)
        {

            LoadCallCount++;

            return Task.FromResult(Spell);

        }

        public Task<IReadOnlyList<SpellVersionDto>> ListVersionsAsync(string name, string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult(Versions);

        public Task<SpellVersionDetailDto?> GetVersionDetailAsync(string name, string version, string? workspace, CancellationToken cancellationToken)
        {

            GetVersionDetailCallCount++;

            VersionDetails.TryGetValue(version, out SpellVersionDetailDto? detail);

            return Task.FromResult(detail);

        }

        public Task<SpellVersionDto?> CreateVersionAsync(string name, CreateSpellVersionRequest request, CancellationToken cancellationToken)
        {

            LastCreateVersionRequest = request;

            return Task.FromResult(CreateVersionResult);

        }

        public Task<SpellVersionDto?> UpdateVersionAsync(string name, string version, UpdateSpellVersionRequest request, CancellationToken cancellationToken)
        {

            LastUpdatedVersion = version;

            LastUpdateVersionRequest = request;

            return Task.FromResult(UpdateVersionResult);

        }

        public Task<bool> SaveAsync(string name, UpdateSpellRequest request, string? workspace, CancellationToken cancellationToken)
        {

            LastUpdateRequest = request;

            return Task.FromResult(SaveSucceeds);

        }

        public Task<SpellValidationResultDto?> ValidateAsync(string name, string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult<SpellValidationResultDto?>(null);

        public Task<SpellExportDto?> ExportAsync(string name, string? workspace, CancellationToken cancellationToken)
        {

            ExportCallCount++;

            return Task.FromResult(ExportResult);

        }

        public Task<SpellSummary?> CloneAsync(string name, CloneSpellRequest request, CancellationToken cancellationToken)
        {

            LastCloneRequest = request;

            return Task.FromResult(CloneResult);

        }

        public Task<DataSourceResult<SpellSummary>> ImportAsync(SpellImportRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<SpellSummary>(null, false, "test", "not used"));

        public Task<bool> DeleteAsync(string name, string workspace, CancellationToken cancellationToken)
        {

            DeleteCallCount++;

            return Task.FromResult(true);

        }

        public Task<SpellCastResult?> CastAsync(string name, SpellCastRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CastResult);

        public Task<ManaCountResult?> EstimateManaAsync(ManaCountRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ManaCount);

        public async IAsyncEnumerable<IntelligenceEvent> ExecuteStreamAsync(string name, SpellExecuteRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {

            LastExecuteRequest = request;

            foreach (IntelligenceEvent ev in ExecutionEvents)
            {

                yield return ev;

                await Task.Yield();

            }

        }

        public Task<SpellVersionDto?> ActivateVersionAsync(string name, string version, string? workspace, CancellationToken cancellationToken)
        {

            ActivatedVersion = version;

            return Task.FromResult<SpellVersionDto?>(ActivateResult ?? new SpellVersionDto(version, true, DateTimeOffset.UtcNow, null));

        }

        public Task<IReadOnlyList<string>> ListSpellNamesAsync(string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult(SpellNames);

        public Task<IReadOnlyList<string>> ListAvailableToolNamesAsync(string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult(AvailableToolNames);

    }

}
