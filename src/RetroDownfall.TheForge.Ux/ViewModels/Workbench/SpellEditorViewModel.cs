using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Ux.Markdown;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Diff;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// Workbench editor for a Spell. Loads and edits the spell's assembled body/metadata, supports
/// Save/Cast dry-run/Mana estimate/version activation, validation, clone/export/delete lifecycle
/// actions, The Mirror (version compare/create/update), Spell Metadata Designer / raw SPELL.json,
/// and streams Execute events through the spell-specific NDJSON endpoint.
/// </summary>
public sealed partial class SpellEditorViewModel : ViewModelBase
{

    private readonly ISpellEditorDataSource _dataSource;

    private readonly INavigationService _navigation;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IConfirmationDialogService _confirmationDialog;

    private readonly IArtifactFileDialogService _fileDialog;

    private readonly ITextInputDialogService _textInputDialog;

    private readonly IWhispersService _whispers;

    private string _persistedActiveBody = string.Empty;

    private bool _syncingMetadata;

    private bool _rawSpellJsonInvalid;

    private IReadOnlyList<string> _spellCatalog = [];

    private IReadOnlyList<string> _toolCatalog = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuiltIn))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanEditMetadata))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(CanMutateVersions))]
    [NotifyPropertyChangedFor(nameof(CanActivateVersion))]
    [NotifyPropertyChangedFor(nameof(CanCreateVersion))]
    [NotifyPropertyChangedFor(nameof(CanUpdateVersion))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ActivateVersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateVersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedVersionCommand))]
    private SpellDetail? _spell;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorDirty))]
    [NotifyPropertyChangedFor(nameof(ShowDirtyComparisonWarning))]
    private string _markdownBody = string.Empty;

    [ObservableProperty]
    private string _frontmatter = string.Empty;

    [ObservableProperty]
    private string _spellJson = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditMetadata))]
    private string _metadataVersion = string.Empty;

    [ObservableProperty]
    private string? _metadataActiveVersion;

    [ObservableProperty]
    private string _inputSchemaJson = "{}";

    [ObservableProperty]
    private string _outputSchemaJson = "{}";

    [ObservableProperty]
    private string? _metadataValidationError;

    [ObservableProperty]
    private string? _dependencyWarnings;

    [ObservableProperty]
    private string? _toolWarnings;

    [ObservableProperty]
    private string _newDependencyText = string.Empty;

    [ObservableProperty]
    private string _newDeclaredToolText = string.Empty;

    [ObservableProperty]
    private string? _selectedDependency;

    [ObservableProperty]
    private string? _selectedDeclaredTool;

    [ObservableProperty]
    private SpellCastResult? _castPreview;

    [ObservableProperty]
    private int? _manaCount;

    [ObservableProperty]
    private string _executionPrompt = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActivateVersion))]
    [NotifyPropertyChangedFor(nameof(CanUpdateVersion))]
    [NotifyCanExecuteChangedFor(nameof(ActivateVersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedVersionCommand))]
    private SpellVersionDto? _selectedVersion;

    [ObservableProperty]
    private string? _activatePreviousVersionNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceVisible))]
    [NotifyPropertyChangedFor(nameof(IsPreviewVisible))]
    [NotifyPropertyChangedFor(nameof(IsSplitterVisible))]
    private MarkdownViewMode _viewMode = MarkdownViewMode.Source;

    [ObservableProperty]
    private bool _loadRemoteImages;

    [ObservableProperty]
    private bool _syncScrollEnabled = true;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _validationSummary;

    public SpellEditorViewModel(
        string spellName,
        ISpellEditorDataSource dataSource,
        INavigationService navigation,
        FoundryFloorViewModel foundryFloor,
        IConfirmationDialogService confirmationDialog,
        IArtifactFileDialogService fileDialog,
        ITextInputDialogService textInputDialog,
        IWhispersService whispers,
        string? workspace = null)
    {

        SpellName = spellName;

        Workspace = WorkspacePathHelper.ForApi(workspace);

        _dataSource = dataSource;

        _navigation = navigation;

        _foundryFloor = foundryFloor;

        _confirmationDialog = confirmationDialog;

        _fileDialog = fileDialog;

        _textInputDialog = textInputDialog;

        _whispers = whispers;

        Title = $"Spell: {spellName}";

        DocumentTooltip = Workspace;

    }

    public override DocumentKind? Kind => DocumentKind.Spell;

    public string SpellName { get; }

    public string? Workspace { get; }

    public bool IsBuiltIn => Spell?.Source == SpellSource.Builtin;

    public bool CanSave => Spell is not null && !IsBuiltIn;

    public bool CanEditMetadata => CanSave;

    public bool CanDelete => !IsBuiltIn && Workspace is not null;

    public bool CanMutateVersions => CanSave;

    public bool CanActivateVersion => CanMutateVersions && SelectedVersion is not null;

    public bool CanCreateVersion => CanMutateVersions;

    public bool CanUpdateVersion => CanMutateVersions && SelectedVersion is not null;

    public bool IsEditorDirty => !string.Equals(MarkdownBody, _persistedActiveBody, StringComparison.Ordinal);

    public bool ShowDirtyComparisonWarning => IsEditorDirty;

    public ObservableCollection<SpellVersionDto> Versions { get; } = [];

    public ObservableCollection<string> Dependencies { get; } = [];

    public ObservableCollection<string> DeclaredTools { get; } = [];

    public ObservableCollection<DiffLineItem> DiffLines { get; } = [];

    public ObservableCollection<IntelligenceEvent> ExecutionEvents { get; } = [];

    public bool IsSourceVisible => MarkdownViewModeHelper.IsSourceVisible(ViewMode);

    public bool IsPreviewVisible => MarkdownViewModeHelper.IsPreviewVisible(ViewMode);

    public bool IsSplitterVisible => MarkdownViewModeHelper.IsSplitterVisible(ViewMode);

    public string ScriptsSummary => Spell is null || Spell.Tools.Length == 0
        ? "No attuned tools yet."
        : string.Join(Environment.NewLine, Spell.Tools.Select(static tool => $"- {tool}"));

    [RelayCommand]
    private void SetViewMode(MarkdownViewMode mode) => ViewMode = mode;

    [RelayCommand]
    private void AddDependency()
    {

        if (!CanEditMetadata)
        {

            return;

        }

        string value = NewDependencyText.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {

            return;

        }

        if (Dependencies.Contains(value, StringComparer.OrdinalIgnoreCase))
        {

            NewDependencyText = string.Empty;

            return;

        }

        Dependencies.Add(value);

        NewDependencyText = string.Empty;

        OnDesignerChanged();

    }

    [RelayCommand]
    private void RemoveDependency()
    {

        if (!CanEditMetadata || SelectedDependency is null)
        {

            return;

        }

        Dependencies.Remove(SelectedDependency);

        SelectedDependency = null;

        OnDesignerChanged();

    }

    [RelayCommand]
    private void AddDeclaredTool()
    {

        if (!CanEditMetadata)
        {

            return;

        }

        string value = NewDeclaredToolText.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {

            return;

        }

        if (DeclaredTools.Contains(value, StringComparer.OrdinalIgnoreCase))
        {

            NewDeclaredToolText = string.Empty;

            return;

        }

        DeclaredTools.Add(value);

        NewDeclaredToolText = string.Empty;

        OnDesignerChanged();

    }

    [RelayCommand]
    private void RemoveDeclaredTool()
    {

        if (!CanEditMetadata || SelectedDeclaredTool is null)
        {

            return;

        }

        DeclaredTools.Remove(SelectedDeclaredTool);

        SelectedDeclaredTool = null;

        OnDesignerChanged();

    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        MetadataValidationError = null;

        try
        {

            Spell = await _dataSource.LoadSpellAsync(SpellName, Workspace, cancellationToken).ConfigureAwait(true);

            string body = Spell?.Body ?? Spell?.SystemPrompt ?? string.Empty;

            _persistedActiveBody = body;

            MarkdownBody = body;

            Frontmatter = BuildFrontmatter(Spell);

            ApplyDesignerState(SpellJsonSync.LoadFromSpell(Spell));

            if (Spell is not null)
            {

                SetSpellJson(SpellJsonSync.SerializeKnownFields(Spell));

            }
            else
            {

                SetSpellJson("{}");

            }

            _rawSpellJsonInvalid = false;

            MetadataValidationError = null;

            _spellCatalog = await _dataSource.ListSpellNamesAsync(Workspace, cancellationToken).ConfigureAwait(true);

            _toolCatalog = await _dataSource.ListAvailableToolNamesAsync(Workspace, cancellationToken).ConfigureAwait(true);

            RefreshCatalogWarnings();

            await LoadVersionsAsync(cancellationToken).ConfigureAwait(true);

            OnPropertyChanged(nameof(ScriptsSummary));

            OnPropertyChanged(nameof(IsEditorDirty));

            OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

            OnPropertyChanged(nameof(CanEditMetadata));

            StatusText = Spell is null ? "Spell not found." : "Loaded.";

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync(CancellationToken cancellationToken)
    {

        if (Spell is null || IsBuiltIn)
        {

            return;

        }

        SpellJsonSync.DesignerState designer = CaptureDesignerState();

        if (!SpellJsonSync.TryBuildUpdateFields(
                designer,
                out string? version,
                out JsonDocument? inputSchema,
                out JsonDocument? outputSchema,
                out string[]? declaredTools,
                out string[]? dependencies,
                out string? schemaError))
        {

            MetadataValidationError = schemaError;

            LastError = schemaError;

            StatusText = "Save blocked — invalid SPELL.json metadata.";

            return;

        }

        string? rawError = null;

        if (_rawSpellJsonInvalid
            || !SpellJsonSync.TryParseRaw(SpellJson, out _, out rawError))
        {

            string message = rawError ?? MetadataValidationError ?? "SPELL.json is invalid.";

            MetadataValidationError = message;

            LastError = message;

            StatusText = "Save blocked — invalid SPELL.json.";

            inputSchema?.Dispose();

            outputSchema?.Dispose();

            return;

        }

        IsBusy = true;

        LastError = null;

        MetadataValidationError = null;

        try
        {

            UpdateSpellRequest request = new(
                Spell.Description,
                Spell.Tags,
                MarkdownBody,
                Spell.Template,
                Spell.Model,
                Spell.Provider,
                Spell.Tools,
                Spell.RequiredMcpServers,
                version,
                inputSchema,
                outputSchema,
                declaredTools,
                dependencies);

            if (await _dataSource.SaveAsync(Spell.Name, request, Workspace, cancellationToken).ConfigureAwait(true))
            {

                Spell = Spell with
                {
                    SystemPrompt = MarkdownBody,
                    Body = MarkdownBody,
                    Version = version,
                    InputSchema = inputSchema,
                    OutputSchema = outputSchema,
                    DeclaredTools = declaredTools,
                    Dependencies = dependencies,
                };

                _persistedActiveBody = MarkdownBody;

                ApplyDesignerState(SpellJsonSync.LoadFromSpell(Spell));

                SetSpellJson(SpellJsonSync.SerializeKnownFields(Spell));

                _rawSpellJsonInvalid = false;

                MetadataValidationError = null;

                RefreshCatalogWarnings();

                OnPropertyChanged(nameof(IsEditorDirty));

                OnPropertyChanged(nameof(ShowDirtyComparisonWarning));

                StatusText = "Saved.";

                _foundryFloor.AppendLine($"Spell saved: {Spell.Name}.");

                _whispers.Show(WhisperSeverity.Success, "Spell saved.");

            }
            else
            {

                inputSchema?.Dispose();

                outputSchema?.Dispose();

                LastError = "Save failed — the server rejected the request.";

                StatusText = "Save failed.";

                _foundryFloor.AppendLine($"Spell save failed: {Spell.Name}.");

                _whispers.Show(WhisperSeverity.Error, "Spell save failed.");

            }

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task ValidateAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        ValidationSummary = null;

        try
        {

            SpellValidationResultDto? result = await _dataSource
                .ValidateAsync(SpellName, Workspace, cancellationToken)
                .ConfigureAwait(true);

            if (result is null)
            {

                LastError = "Validation failed — the server rejected the request.";

                StatusText = "Validation failed.";

                _foundryFloor.AppendLine($"Spell validate failed: {SpellName}.");

                _whispers.Show(WhisperSeverity.Error, "Spell validation failed.");

                return;

            }

            ValidationSummary = BuildValidationSummary(result);

            StatusText = result.IsValid ? "Valid." : "Validation issues found.";

            _foundryFloor.AppendLine($"Spell validate ({SpellName}): {ValidationSummary}");

            _whispers.Show(
                result.IsValid ? WhisperSeverity.Success : WhisperSeverity.Warning,
                result.IsValid ? "Spell valid." : "Spell validation issues.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task ExportAsync(CancellationToken cancellationToken)
    {

        string suggestedFileName = $"{SpellName}.json";

        string? path = await _fileDialog
            .PickSaveJsonPathAsync(suggestedFileName, cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            SpellExportDto? export = await _dataSource
                .ExportAsync(SpellName, Workspace, cancellationToken)
                .ConfigureAwait(true);

            if (export is null)
            {

                LastError = "Export failed — the server rejected the request.";

                StatusText = "Export failed.";

                _foundryFloor.AppendLine($"Spell export failed: {SpellName}.");

                _whispers.Show(WhisperSeverity.Error, "Spell export failed.");

                return;

            }

            string json = JsonSerializer.Serialize(export, TheForgeJsonContext.Default.SpellExportDto);

            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(true);

            StatusText = "Exported.";

            _foundryFloor.AppendLine($"Spell exported: {SpellName} → {path}.");

            _whispers.Show(WhisperSeverity.Success, "Spell exported.");

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Spell export error: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Spell export failed.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task ImportAsync(CancellationToken cancellationToken)
    {

        string? path = await ArtifactImportExportHelper
            .PickOpenPathOrNullAsync(_fileDialog, cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            (SpellExportDto? payload, string? readError) = await ArtifactImportExportHelper
                .ReadJsonAsync(path, TheForgeJsonContext.Default.SpellExportDto, cancellationToken)
                .ConfigureAwait(true);

            if (payload is null)
            {

                LastError = readError ?? "Failed to read spell export JSON.";

                _foundryFloor.AppendLine($"Spell import read failed: {LastError}");

                _whispers.Show(WhisperSeverity.Error, "Spell import failed.");

                return;

            }

            SpellImportRequest request = new(payload, Workspace, null);

            DataSourceResult<SpellSummary> result = await _dataSource
                .ImportAsync(request, cancellationToken)
                .ConfigureAwait(true);

            if (!result.Success || result.Data is null)
            {

                string detail = FormatImportError(result.ErrorCode, result.ErrorMessage, "Spell import failed.");

                LastError = detail;

                _foundryFloor.AppendLine($"Spell import failed: {detail}");

                string whisper = string.Equals(result.ErrorCode, ErrorCodes.Spell.NameCollision, StringComparison.Ordinal)
                    ? "Spell name collision."
                    : "Spell import failed.";

                _whispers.Show(WhisperSeverity.Error, whisper);

                return;

            }

            StatusText = $"Imported {result.Data.Name}.";

            _foundryFloor.AppendLine($"Spell imported: {result.Data.Name} from {path}.");

            _whispers.Show(WhisperSeverity.Success, "Spell imported.");

            _navigation.OpenDocument(DocumentKind.Spell, result.Data.Name, Workspace);

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Spell import error: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Spell import failed.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    private static string FormatImportError(string? code, string? message, string fallback)
    {

        if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(message))
        {

            return $"{code}: {message}";

        }

        if (!string.IsNullOrWhiteSpace(code))
        {

            return code;

        }

        return message ?? fallback;

    }

    [RelayCommand]
    public async Task CloneAsync(CancellationToken cancellationToken)
    {

        string? newName = await _textInputDialog
            .PromptAsync("Clone spell", "New name", $"{SpellName}-copy", cancellationToken)
            .ConfigureAwait(true);

        if (newName is null)
        {

            return;

        }

        if (string.IsNullOrWhiteSpace(newName))
        {

            LastError = "A new name is required to clone.";

            return;

        }

        string? cloneWorkspace = Workspace;

        if (cloneWorkspace is null)
        {

            string? workspaceInput = await _textInputDialog
                .PromptAsync("Clone spell", "Target workspace path", null, cancellationToken)
                .ConfigureAwait(true);

            if (workspaceInput is null)
            {

                return;

            }

            cloneWorkspace = WorkspacePathHelper.ForApi(workspaceInput);

            if (cloneWorkspace is null)
            {

                LastError = "A target workspace path is required to clone a built-in spell.";

                return;

            }

        }

        IsBusy = true;

        LastError = null;

        try
        {

            CloneSpellRequest request = new(newName.Trim(), cloneWorkspace);

            SpellSummary? cloned = await _dataSource
                .CloneAsync(SpellName, request, cancellationToken)
                .ConfigureAwait(true);

            if (cloned is null)
            {

                LastError = "Clone failed — the server rejected the request.";

                _foundryFloor.AppendLine($"Spell clone failed: {SpellName}.");

                _whispers.Show(WhisperSeverity.Error, "Spell clone failed.");

                return;

            }

            _navigation.OpenDocument(DocumentKind.Spell, cloned.Name, cloneWorkspace);

            StatusText = "Cloned.";

            _foundryFloor.AppendLine($"Spell cloned: {SpellName} → {cloned.Name} ({cloneWorkspace}).");

            _whispers.Show(WhisperSeverity.Success, "Spell cloned.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    public async Task DeleteAsync(CancellationToken cancellationToken)
    {

        if (IsBuiltIn || Workspace is null)
        {

            return;

        }

        bool confirmed = await _confirmationDialog
            .ConfirmAsync(
                "Delete spell",
                $"Delete spell \"{SpellName}\"? This cannot be undone.",
                cancellationToken)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            bool deleted = await _dataSource.DeleteAsync(SpellName, Workspace, cancellationToken).ConfigureAwait(true);

            if (!deleted)
            {

                LastError = "Delete failed — the server rejected the request.";

                _foundryFloor.AppendLine($"Spell delete failed: {SpellName}.");

                _whispers.Show(WhisperSeverity.Error, "Spell delete failed.");

                return;

            }

            _navigation.CloseDocument(DocumentKind.Spell, SpellName, Workspace);

            _foundryFloor.AppendLine($"Spell deleted: {SpellName} ({Workspace}).");

            _whispers.Show(WhisperSeverity.Success, "Spell deleted.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task CastAsync(CancellationToken cancellationToken)
    {

        SpellCastRequest request = new(Workspace, SessionId: null, CampaignId: null);

        CastPreview = await _dataSource.CastAsync(SpellName, request, cancellationToken).ConfigureAwait(true);

        if (CastPreview is null)
        {

            LastError = "Cast failed — the server rejected the request.";

            StatusText = "Cast failed.";

            _foundryFloor.AppendLine($"Spell cast failed: {SpellName}.");

            _whispers.Show(WhisperSeverity.Error, "Spell cast failed.");

            return;

        }

        StatusText = "Cast preview ready.";

        _whispers.Show(WhisperSeverity.Success, "Spell cast ready.");

    }

    [RelayCommand]
    public async Task EstimateManaAsync(CancellationToken cancellationToken)
    {

        string prompt = string.IsNullOrWhiteSpace(MarkdownBody) ? Spell?.SystemPrompt ?? string.Empty : MarkdownBody;

        ManaCountResult? result = await _dataSource
            .EstimateManaAsync(new ManaCountRequest(Prompt: prompt, Model: Spell?.Model, Tools: true), cancellationToken)
            .ConfigureAwait(true);

        ManaCount = result?.ManaCount;

    }

    [RelayCommand]
    private void CreateTrial()
    {

        _navigation.OpenOrFocusProvingGrounds(new ProvingGroundsPrefill(
            TrialTargetKind.Spell,
            SpellName,
            Workspace,
            Spell?.Model));

    }

    [RelayCommand]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {

        string prompt = string.IsNullOrWhiteSpace(ExecutionPrompt) ? MarkdownBody : ExecutionPrompt;

        SpellExecuteRequest request = new(
            Prompt: prompt,
            Model: Spell?.Model,
            Workspace: Workspace ?? Spell?.WorkingDirectory);

        ExecutionEvents.Clear();

        LastError = null;

        bool hadError = false;

        try
        {

            await foreach (IntelligenceEvent ev in _dataSource.ExecuteStreamAsync(SpellName, request, cancellationToken).ConfigureAwait(true))
            {

                ExecutionEvents.Add(ev);

                if (ev.Type == IntelligenceEventType.Error)
                {

                    hadError = true;

                    LastError = ev.Message;

                    _foundryFloor.AppendLine($"Spell execute error: {ev.Message}");

                }

                if (ev.Type == IntelligenceEventType.SessionBound && Guid.TryParse(ev.Message, out Guid sessionId))
                {

                    _navigation.OpenDocument(DocumentKind.Session, sessionId.ToString());

                }

            }

            if (hadError)
            {

                StatusText = "Execution failed.";

                _whispers.Show(WhisperSeverity.Error, "Spell execution failed.");

            }
            else
            {

                StatusText = "Executed.";

                _whispers.Show(WhisperSeverity.Success, "Spell executed.");

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            StatusText = "Execution failed.";

            _foundryFloor.AppendLine($"Spell execute error: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Spell execution failed.");

        }

    }

    [RelayCommand(CanExecute = nameof(CanActivateVersion))]
    public async Task ActivateVersionAsync(SpellVersionDto? version, CancellationToken cancellationToken)
    {

        if (version is null || !CanMutateVersions)
        {

            return;

        }

        SpellVersionDto? activated = await _dataSource
            .ActivateVersionAsync(SpellName, version.Version, Workspace, cancellationToken)
            .ConfigureAwait(true);

        if (activated is not null)
        {

            ActivatePreviousVersionNote = activated.PreviousVersion is null
                ? null
                : $"Previous SPELL.md preserved as SPELL.v{activated.PreviousVersion}.md";

            await LoadAsync(cancellationToken).ConfigureAwait(true);

            StatusText = "Version activated.";

            if (ActivatePreviousVersionNote is not null)
            {

                _foundryFloor.AppendLine($"Spell activate ({SpellName}): {ActivatePreviousVersionNote}");

            }

            _whispers.Show(WhisperSeverity.Success, "Version activated.");

        }
        else
        {

            LastError = "Activate failed — the server rejected the request.";

            StatusText = "Activate failed.";

            _foundryFloor.AppendLine($"Spell activate failed: {SpellName} v{version.Version}.");

            _whispers.Show(WhisperSeverity.Error, "Version activation failed.");

        }

    }

    [RelayCommand(CanExecute = nameof(CanCreateVersion))]
    public async Task CreateVersionAsync(CancellationToken cancellationToken)
    {

        if (!CanMutateVersions)
        {

            return;

        }

        string? versionLabel = await _textInputDialog
            .PromptAsync("Create version", "Version label", null, cancellationToken)
            .ConfigureAwait(true);

        if (versionLabel is null)
        {

            return;

        }

        if (string.IsNullOrWhiteSpace(versionLabel))
        {

            LastError = "A version label is required.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            CreateSpellVersionRequest request = new(versionLabel.Trim(), MarkdownBody, Workspace);

            SpellVersionDto? created = await _dataSource
                .CreateVersionAsync(SpellName, request, cancellationToken)
                .ConfigureAwait(true);

            if (created is null)
            {

                LastError = "Create version failed — the server rejected the request.";

                StatusText = "Create version failed.";

                _foundryFloor.AppendLine($"Spell create version failed: {SpellName}.");

                _whispers.Show(WhisperSeverity.Error, "Version create failed.");

                return;

            }

            await LoadVersionsAsync(cancellationToken).ConfigureAwait(true);

            StatusText = $"Version {created.Version} created.";

            _foundryFloor.AppendLine($"Spell version created: {SpellName} v{created.Version}.");

            _whispers.Show(WhisperSeverity.Success, "Version created.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand(CanExecute = nameof(CanUpdateVersion))]
    public async Task UpdateSelectedVersionAsync(CancellationToken cancellationToken)
    {

        if (!CanMutateVersions || SelectedVersion is null)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            UpdateSpellVersionRequest request = new(MarkdownBody, Workspace);

            SpellVersionDto? updated = await _dataSource
                .UpdateVersionAsync(SpellName, SelectedVersion.Version, request, cancellationToken)
                .ConfigureAwait(true);

            if (updated is null)
            {

                LastError = "Update version failed — the server rejected the request.";

                StatusText = "Update version failed.";

                _foundryFloor.AppendLine($"Spell update version failed: {SpellName} v{SelectedVersion.Version}.");

                _whispers.Show(WhisperSeverity.Error, "Version update failed.");

                return;

            }

            await LoadVersionsAsync(cancellationToken).ConfigureAwait(true);

            await RefreshMirrorDiffAsync(cancellationToken).ConfigureAwait(true);

            StatusText = $"Version {updated.Version} updated.";

            _foundryFloor.AppendLine($"Spell version updated: {SpellName} v{updated.Version}.");

            _whispers.Show(WhisperSeverity.Success, "Version updated.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task RefreshMirrorDiffAsync(CancellationToken cancellationToken)
    {

        DiffLines.Clear();

        if (SelectedVersion is null)
        {

            return;

        }

        SpellVersionDetailDto? detail = await _dataSource
            .GetVersionDetailAsync(SpellName, SelectedVersion.Version, Workspace, cancellationToken)
            .ConfigureAwait(true);

        if (detail is null)
        {

            LastError = "Version detail unavailable — the server rejected the request.";

            StatusText = "Version detail unavailable.";

            _foundryFloor.AppendLine($"Spell version detail failed: {SpellName} v{SelectedVersion.Version}.");

            return;

        }

        foreach (DiffLineItem line in LineDiff.Compare(_persistedActiveBody, detail.Body))
        {

            DiffLines.Add(line);

        }

    }

    private async Task LoadVersionsAsync(CancellationToken cancellationToken)
    {

        Versions.Clear();

        foreach (SpellVersionDto version in await _dataSource.ListVersionsAsync(SpellName, Workspace, cancellationToken).ConfigureAwait(true))
        {

            Versions.Add(version);

        }

    }

    private static string BuildValidationSummary(SpellValidationResultDto result)
    {

        List<string> parts = [];

        if (result.Errors.Length > 0)
        {

            parts.Add($"Errors: {string.Join("; ", result.Errors)}");

        }

        if (result.Warnings.Length > 0)
        {

            parts.Add($"Warnings: {string.Join("; ", result.Warnings)}");

        }

        if (result.IsValid && parts.Count == 0)
        {

            return "Valid.";

        }

        return string.Join(" | ", parts);

    }

    private static string BuildFrontmatter(SpellDetail? spell)
    {

        if (spell is null)
        {

            return string.Empty;

        }

        return string.Join(
            Environment.NewLine,
            $"Name: {spell.Name}",
            $"Description: {spell.Description ?? string.Empty}",
            $"Source: {spell.Source}",
            $"Model: {spell.Model ?? string.Empty}",
            $"Provider: {spell.Provider ?? string.Empty}",
            $"Tags: {string.Join(", ", spell.Tags)}");

    }

    private SpellJsonSync.DesignerState CaptureDesignerState() =>
        new(
            Version: MetadataVersion,
            ActiveVersion: MetadataActiveVersion,
            Dependencies: Dependencies.ToArray(),
            DeclaredTools: DeclaredTools.ToArray(),
            InputSchemaJson: InputSchemaJson,
            OutputSchemaJson: OutputSchemaJson);

    private void ApplyDesignerState(SpellJsonSync.DesignerState state)
    {

        _syncingMetadata = true;

        try
        {

            MetadataVersion = state.Version;

            MetadataActiveVersion = state.ActiveVersion;

            InputSchemaJson = state.InputSchemaJson;

            OutputSchemaJson = state.OutputSchemaJson;

            Dependencies.Clear();

            foreach (string dependency in state.Dependencies)
            {

                Dependencies.Add(dependency);

            }

            DeclaredTools.Clear();

            foreach (string tool in state.DeclaredTools)
            {

                DeclaredTools.Add(tool);

            }

        }
        finally
        {

            _syncingMetadata = false;

        }

    }

    private void SetSpellJson(string json)
    {

        _syncingMetadata = true;

        try
        {

            SpellJson = json;

        }
        finally
        {

            _syncingMetadata = false;

        }

    }

    private void OnDesignerChanged()
    {

        if (_syncingMetadata || !CanEditMetadata || Spell is null || _rawSpellJsonInvalid)
        {

            RefreshCatalogWarnings();

            return;

        }

        SetSpellJson(SpellJsonSync.SerializeKnownFields(Spell, CaptureDesignerState()));

        MetadataValidationError = null;

        RefreshCatalogWarnings();

    }

    private void RefreshCatalogWarnings()
    {

        List<string> dependencyWarnings = [];

        foreach (string dependency in Dependencies)
        {

            if (!_spellCatalog.Contains(dependency, StringComparer.OrdinalIgnoreCase))
            {

                dependencyWarnings.Add($"Dependency \"{dependency}\" was not found in the spell catalog.");

            }

        }

        DependencyWarnings = dependencyWarnings.Count == 0
            ? null
            : string.Join(Environment.NewLine, dependencyWarnings);

        List<string> toolWarnings = [];

        foreach (string tool in DeclaredTools)
        {

            if (!_toolCatalog.Contains(tool, StringComparer.OrdinalIgnoreCase))
            {

                toolWarnings.Add($"Declared tool \"{tool}\" was not found in the available tool catalog.");

            }

        }

        ToolWarnings = toolWarnings.Count == 0
            ? null
            : string.Join(Environment.NewLine, toolWarnings);

    }

    partial void OnMetadataVersionChanged(string value)
    {

        if (!_syncingMetadata)
        {

            OnDesignerChanged();

        }

    }

    partial void OnInputSchemaJsonChanged(string value)
    {

        if (!_syncingMetadata)
        {

            OnDesignerChanged();

        }

    }

    partial void OnOutputSchemaJsonChanged(string value)
    {

        if (!_syncingMetadata)
        {

            OnDesignerChanged();

        }

    }

    partial void OnSpellJsonChanged(string value)
    {

        if (_syncingMetadata || Spell is null)
        {

            return;

        }

        if (!CanEditMetadata)
        {

            return;

        }

        if (!SpellJsonSync.TryParseRaw(value, out SkillMetadata? metadata, out string? error))
        {

            _rawSpellJsonInvalid = true;

            MetadataValidationError = error;

            return;

        }

        _rawSpellJsonInvalid = false;

        MetadataValidationError = null;

        ApplyDesignerState(new SpellJsonSync.DesignerState(
            Version: metadata!.Version,
            ActiveVersion: metadata.ActiveVersion,
            Dependencies: metadata.Dependencies,
            DeclaredTools: metadata.DeclaredTools,
            InputSchemaJson: SchemaDocumentToText(metadata.InputSchema),
            OutputSchemaJson: SchemaDocumentToText(metadata.OutputSchema)));

        RefreshCatalogWarnings();

    }

    private static string SchemaDocumentToText(JsonDocument? schema)
    {

        if (schema is null
            || schema.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {

            return "{}";

        }

        return schema.RootElement.GetRawText();

    }

}
