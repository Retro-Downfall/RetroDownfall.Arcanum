using System.Collections.ObjectModel;

using System.Collections.Specialized;

using System.ComponentModel;

using System.Text.Json;

using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;

using CommunityToolkit.Mvvm.Input;

using RetroDownfall.Arcanum.Core.ProvingGrounds;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.TheForge.Ux.Models;

using RetroDownfall.TheForge.Core.Models.Trials;

using RetroDownfall.TheForge.Core.Services;

using RetroDownfall.TheForge.Ux.Services;

using RetroDownfall.TheForge.Ux.Services.Whispers;

using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// Singleton Workbench document for The Proving Grounds — ephemeral Trial designer (no persistence).
/// </summary>
public sealed partial class ProvingGroundsViewModel : ViewModelBase, IDisposable
{

    public const string SingletonDocumentId = "proving-grounds";

    private readonly ITrialDataSource _dataSource;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IWhispersService _whispers;

    private readonly IConfirmationDialogService _confirmationDialog;

    private CancellationTokenSource? _runCts;

    private bool _suppressDirty;

    private bool _disposed;

    [ObservableProperty]
    private TrialTargetKind _targetKind = TrialTargetKind.Spell;

    [ObservableProperty]
    private string _target = string.Empty;

    [ObservableProperty]
    private string? _workspace;

    [ObservableProperty]
    private string? _model;

    [ObservableProperty]
    private string? _trialName;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _validationMessage;

    [ObservableProperty]
    private TrialResult? _lastResult;

    [ObservableProperty]
    private TrialVariableRowViewModel? _selectedVariable;

    [ObservableProperty]
    private InquisitorDraftViewModel? _selectedInquisitor;

    [ObservableProperty]
    private string? _selectedSpellName;

    [ObservableProperty]
    private PromptSummaryDto? _selectedPrompt;

    public ProvingGroundsViewModel(
        ITrialDataSource dataSource,
        FoundryFloorViewModel foundryFloor,
        IWhispersService whispers,
        IConfirmationDialogService confirmationDialog,
        ITrialSuiteStore suiteStore,
        IArtifactFileDialogService fileDialog,
        ITheForgeLocalMutationRunner mutationRunner)
    {

        _dataSource = dataSource;

        _foundryFloor = foundryFloor;

        _whispers = whispers;

        _confirmationDialog = confirmationDialog;

        _suiteStore = suiteStore;

        _fileDialog = fileDialog;

        _mutationRunner = mutationRunner;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _suiteDocument = new TrialSuiteStoreDocument(TrialSuiteStore.CurrentSchemaVersion, now, now, []);

        Title = "Proving Grounds";

        Variables.CollectionChanged += OnDraftCollectionChanged;

        Inquisitors.CollectionChanged += OnDraftCollectionChanged;

        _ = LoadSuitesCommand.ExecuteAsync(null);

    }

    public override DocumentKind? Kind => DocumentKind.Trial;

    public string DocumentId => SingletonDocumentId;

    public ObservableCollection<TrialVariableRowViewModel> Variables { get; } = [];

    public ObservableCollection<InquisitorDraftViewModel> Inquisitors { get; } = [];

    public ObservableCollection<string> SpellNames { get; } = [];

    public ObservableCollection<PromptSummaryDto> Prompts { get; } = [];

    public IReadOnlyList<TrialTargetKind> TargetKinds { get; } =
    [
        TrialTargetKind.Spell,
        TrialTargetKind.Prompt,
        TrialTargetKind.ApprenticeGoal,
    ];

    public bool HasResult => LastResult is not null;

    public string ResultSummary => LastResult is null
        ? string.Empty
        : LastResult.Passed
            ? $"Passed ({LastResult.InquisitorsPassed}/{LastResult.InquisitorsTotal})"
            : $"Failed ({LastResult.InquisitorsPassed}/{LastResult.InquisitorsTotal})";

    public string UsageSummary => LastResult?.Usage is { } usage
        ? $"prompt={usage.PromptTokens} completion={usage.CompletionTokens} total={usage.TotalTokens}"
        : string.Empty;

    /// <summary>
    /// Prefills the designer for Spell/Prompt/ApprenticeGoal shortcuts. Returns
    /// <see langword="false"/> when the draft is dirty (does not overwrite).
    /// </summary>
    public bool ApplyPrefill(
        TrialTargetKind kind,
        string target,
        string? workspace,
        string? model,
        Dictionary<string, string>? variables)
    {

        if (IsDirty)
        {

            StatusText = "Clear or reset the draft before applying a prefill.";

            _whispers.Show(
                WhisperSeverity.Warning,
                "Proving Grounds has unsaved draft changes.");

            return false;

        }

        _suppressDirty = true;

        try
        {

            ClearDraftCore();

            TargetKind = kind;

            Target = target ?? string.Empty;

            Workspace = string.IsNullOrWhiteSpace(workspace) ? null : workspace.Trim();

            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();

            if (variables is not null)
            {

                foreach (KeyValuePair<string, string> pair in variables)
                {

                    TrialVariableRowViewModel row = new(pair.Key, pair.Value);

                    AttachVariable(row);

                    Variables.Add(row);

                }

            }

            SyncPickerSelectionFromTarget();

            IsDirty = false;

            StatusText = "Prefill applied.";

            return true;

        }
        finally
        {

            _suppressDirty = false;

        }

    }

    [RelayCommand]
    public async Task LoadPickersAsync(CancellationToken cancellationToken)
    {

        DataSourceResult<IReadOnlyList<string>> spells =
            await _dataSource.ListSpellNamesAsync(Workspace, cancellationToken).ConfigureAwait(true);

        SpellNames.Clear();

        if (spells.Success && spells.Data is not null)
        {

            foreach (string name in spells.Data)
            {

                SpellNames.Add(name);

            }

        }

        DataSourceResult<IReadOnlyList<PromptSummaryDto>> prompts =
            await _dataSource.ListPromptsAsync(cancellationToken).ConfigureAwait(true);

        Prompts.Clear();

        if (prompts.Success && prompts.Data is not null)
        {

            foreach (PromptSummaryDto prompt in prompts.Data)
            {

                Prompts.Add(prompt);

            }

        }

        SyncPickerSelectionFromTarget();

    }

    [RelayCommand]
    private void AddVariable()
    {

        TrialVariableRowViewModel row = new(string.Empty, string.Empty);

        AttachVariable(row);

        Variables.Add(row);

        SelectedVariable = row;

        MarkDirty();

    }

    [RelayCommand]
    private void RemoveVariable()
    {

        if (SelectedVariable is null)
        {

            return;

        }

        TrialVariableRowViewModel row = SelectedVariable;

        DetachVariable(row);

        Variables.Remove(row);

        SelectedVariable = null;

        MarkDirty();

    }

    [RelayCommand]
    private void AddRegexInquisitor()
    {

        AddInquisitor(InquisitorDraftKind.Regex);

    }

    [RelayCommand]
    private void AddJsonSchemaInquisitor()
    {

        AddInquisitor(InquisitorDraftKind.JsonSchema);

    }

    [RelayCommand]
    private void AddSemanticInquisitor()
    {

        AddInquisitor(InquisitorDraftKind.Semantic);

    }

    [RelayCommand]
    private void RemoveInquisitor()
    {

        if (SelectedInquisitor is null)
        {

            return;

        }

        InquisitorDraftViewModel draft = SelectedInquisitor;

        DetachInquisitor(draft);

        Inquisitors.Remove(draft);

        SelectedInquisitor = null;

        MarkDirty();

    }

    [RelayCommand]
    public async Task RunAsync(CancellationToken cancellationToken)
    {

        if (IsBusy)
        {

            return;

        }

        ValidationMessage = null;

        LastError = null;

        if (!TryBuildTrial(out Trial? trial, out string? validationError) || trial is null)
        {

            ValidationMessage = validationError;

            StatusText = validationError;

            _whispers.Show(WhisperSeverity.Warning, validationError ?? "Trial is invalid.");

            return;

        }

        _runCts?.Cancel();

        _runCts?.Dispose();

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        CancellationToken runToken = _runCts.Token;

        IsBusy = true;

        StatusText = "Running Trial…";

        _foundryFloor.AppendLine(
            $"Proving Grounds: starting Trial ({trial.TargetKind} → {trial.Target}, {trial.Inquisitors.Count} inquisitor(s)).");

        try
        {

            DataSourceResult<TrialResult> result =
                await _dataSource.RunAsync(trial, runToken).ConfigureAwait(true);

            if (!result.Success)
            {

                LastResult = null;

                OnPropertyChanged(nameof(HasResult));

                OnPropertyChanged(nameof(ResultSummary));

                OnPropertyChanged(nameof(UsageSummary));

                LastError = result.ErrorMessage ?? result.ErrorCode ?? "Trial run failed.";

                StatusText = LastError;

                _foundryFloor.AppendLine(
                    $"Proving Grounds: API error {result.ErrorCode ?? "unknown"} — {LastError}");

                _whispers.Show(WhisperSeverity.Error, "Trial run failed.");

                return;

            }

            LastResult = result.Data;

            OnPropertyChanged(nameof(HasResult));

            OnPropertyChanged(nameof(ResultSummary));

            OnPropertyChanged(nameof(UsageSummary));

            if (LastResult is null)
            {

                LastError = "Trial returned no result.";

                StatusText = LastError;

                _whispers.Show(WhisperSeverity.Error, "Trial run failed.");

                return;

            }

            StatusText = ResultSummary;

            _foundryFloor.AppendLine(
                $"Proving Grounds: {ResultSummary}. Output length={LastResult.Output.Length}.");

            _whispers.Show(
                LastResult.Passed ? WhisperSeverity.Success : WhisperSeverity.Warning,
                LastResult.Passed ? "Trial passed." : "Trial failed.");

        }
        catch (OperationCanceledException)
        {

            StatusText = "Trial cancelled.";

            _foundryFloor.AppendLine("Proving Grounds: Trial cancelled.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    private void Cancel()
    {

        _runCts?.Cancel();

    }

    [RelayCommand]
    public async Task ResetAsync(CancellationToken cancellationToken)
    {

        bool confirmed = await _confirmationDialog
            .ConfirmAsync(
                "Reset Proving Grounds",
                "Clear the Trial draft? This cannot be undone.",
                cancellationToken)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        _suppressDirty = true;

        try
        {

            ClearDraftCore();

            IsDirty = false;

            StatusText = "Draft cleared.";

        }
        finally
        {

            _suppressDirty = false;

        }

    }

    /// <summary>Builds a <see cref="Trial"/> from the current draft, or returns validation failure.</summary>
    public bool TryBuildTrial(out Trial? trial, out string? error)
    {

        trial = null;

        error = null;

        string target = Target?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(target))
        {

            error = "Target is required.";

            return false;

        }

        if (Variables.Any(static v => string.IsNullOrWhiteSpace(v.Key)))
        {

            error = "Variable keys must be non-empty.";

            return false;

        }

        if (Inquisitors.Count == 0)
        {

            error = "Add at least one Inquisitor.";

            return false;

        }

        List<Inquisitor> built = [];

        foreach (InquisitorDraftViewModel draft in Inquisitors)
        {

            if (!draft.TryBuild(out Inquisitor? inquisitor, out string? draftError) || inquisitor is null)
            {

                error = draftError;

                return false;

            }

            built.Add(inquisitor);

        }

        Dictionary<string, string>? variables = Variables.Count == 0
            ? null
            : Variables.ToDictionary(static v => v.Key.Trim(), static v => v.Value ?? string.Empty, StringComparer.Ordinal);

        trial = new Trial(
            TargetKind,
            target,
            built,
            variables,
            string.IsNullOrWhiteSpace(Model) ? null : Model.Trim(),
            string.IsNullOrWhiteSpace(Workspace) ? null : Workspace.Trim(),
            string.IsNullOrWhiteSpace(TrialName) ? null : TrialName.Trim());

        return true;

    }

    partial void OnTargetKindChanged(TrialTargetKind value) => MarkDirty();

    partial void OnTargetChanged(string value) => MarkDirty();

    partial void OnWorkspaceChanged(string? value) => MarkDirty();

    partial void OnModelChanged(string? value) => MarkDirty();

    partial void OnTrialNameChanged(string? value) => MarkDirty();

    partial void OnSelectedSpellNameChanged(string? value)
    {

        if (_suppressDirty || TargetKind != TrialTargetKind.Spell || value is null)
        {

            return;

        }

        Target = value;

    }

    partial void OnSelectedPromptChanged(PromptSummaryDto? value)
    {

        if (_suppressDirty || TargetKind != TrialTargetKind.Prompt || value is null)
        {

            return;

        }

        Target = value.Id.ToString("D");

    }

    private void AddInquisitor(InquisitorDraftKind kind)
    {

        InquisitorDraftViewModel draft = new(kind);

        AttachInquisitor(draft);

        Inquisitors.Add(draft);

        SelectedInquisitor = draft;

        MarkDirty();

    }

    private void ClearDraftCore()
    {

        foreach (TrialVariableRowViewModel row in Variables.ToArray())
        {

            DetachVariable(row);

        }

        Variables.Clear();

        foreach (InquisitorDraftViewModel draft in Inquisitors.ToArray())
        {

            DetachInquisitor(draft);

        }

        Inquisitors.Clear();

        TargetKind = TrialTargetKind.Spell;

        Target = string.Empty;

        Workspace = null;

        Model = null;

        TrialName = null;

        SelectedVariable = null;

        SelectedInquisitor = null;

        SelectedSpellName = null;

        SelectedPrompt = null;

        LastResult = null;

        LastError = null;

        ValidationMessage = null;

        OnPropertyChanged(nameof(HasResult));

        OnPropertyChanged(nameof(ResultSummary));

        OnPropertyChanged(nameof(UsageSummary));

    }

    private void SyncPickerSelectionFromTarget()
    {

        _suppressDirty = true;

        try
        {

            if (TargetKind == TrialTargetKind.Spell)
            {

                SelectedSpellName = SpellNames.FirstOrDefault(n =>
                    string.Equals(n, Target, StringComparison.OrdinalIgnoreCase));

            }
            else if (TargetKind == TrialTargetKind.Prompt
                     && Guid.TryParse(Target, out Guid promptId))
            {

                SelectedPrompt = Prompts.FirstOrDefault(p => p.Id == promptId);

            }

        }
        finally
        {

            _suppressDirty = false;

        }

    }

    private void AttachVariable(TrialVariableRowViewModel row) =>
        row.PropertyChanged += OnDraftPropertyChanged;

    private void DetachVariable(TrialVariableRowViewModel row) =>
        row.PropertyChanged -= OnDraftPropertyChanged;

    private void AttachInquisitor(InquisitorDraftViewModel draft) =>
        draft.PropertyChanged += OnDraftPropertyChanged;

    private void DetachInquisitor(InquisitorDraftViewModel draft) =>
        draft.PropertyChanged -= OnDraftPropertyChanged;

    private void OnDraftCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        MarkDirty();

    private void OnDraftPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        MarkDirty();

    private void MarkDirty()
    {

        if (_suppressDirty)
        {

            return;

        }

        IsDirty = true;

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        _runCts?.Cancel();

        _runCts?.Dispose();

        Variables.CollectionChanged -= OnDraftCollectionChanged;

        Inquisitors.CollectionChanged -= OnDraftCollectionChanged;

        foreach (TrialVariableRowViewModel row in Variables)
        {

            DetachVariable(row);

        }

        foreach (InquisitorDraftViewModel draft in Inquisitors)
        {

            DetachInquisitor(draft);

        }

    }

}

public enum InquisitorDraftKind
{

    Regex,

    JsonSchema,

    Semantic,

}

public sealed partial class TrialVariableRowViewModel : ObservableObject
{

    [ObservableProperty]
    private string _key;

    [ObservableProperty]
    private string _value;

    public TrialVariableRowViewModel(string key, string value)
    {

        _key = key;

        _value = value;

    }

}

public sealed partial class InquisitorDraftViewModel : ObservableObject
{

    [ObservableProperty]
    private InquisitorDraftKind _kind;

    [ObservableProperty]
    private string? _label;

    [ObservableProperty]
    private string _pattern = string.Empty;

    [ObservableProperty]
    private bool _shouldMatch = true;

    [ObservableProperty]
    private bool _ignoreCase;

    [ObservableProperty]
    private string _schemaJson = "{}";

    [ObservableProperty]
    private string _question = string.Empty;

    [ObservableProperty]
    private bool _expectedAnswer = true;

    public InquisitorDraftViewModel(InquisitorDraftKind kind)
    {

        _kind = kind;

    }

    public bool TryBuild(out Inquisitor? inquisitor, out string? error)
    {

        inquisitor = null;

        error = null;

        switch (Kind)
        {

            case InquisitorDraftKind.Regex:
            {

                if (string.IsNullOrWhiteSpace(Pattern))
                {

                    error = "Regex Inquisitor requires a pattern.";

                    return false;

                }

                try
                {

                    _ = new Regex(Pattern, IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);

                }
                catch (ArgumentException ex)
                {

                    error = $"Invalid regex: {ex.Message}";

                    return false;

                }

                inquisitor = new RegexInquisitor(Pattern, ShouldMatch, IgnoreCase) { Label = BlankToNull(Label) };

                return true;

            }

            case InquisitorDraftKind.JsonSchema:
            {

                if (string.IsNullOrWhiteSpace(SchemaJson))
                {

                    error = "JSON Schema Inquisitor requires schema JSON.";

                    return false;

                }

                try
                {

                    using JsonDocument document = JsonDocument.Parse(SchemaJson);

                    inquisitor = new JsonSchemaInquisitor(document.RootElement.Clone())
                    {
                        Label = BlankToNull(Label),
                    };

                    return true;

                }
                catch (JsonException ex)
                {

                    error = $"Invalid JSON schema: {ex.Message}";

                    return false;

                }

            }

            case InquisitorDraftKind.Semantic:
            {

                if (string.IsNullOrWhiteSpace(Question))
                {

                    error = "Semantic Inquisitor requires a question.";

                    return false;

                }

                inquisitor = new SemanticInquisitor(Question.Trim(), ExpectedAnswer)
                {
                    Label = BlankToNull(Label),
                };

                return true;

            }

            default:

                error = "Unknown Inquisitor kind.";

                return false;

        }

    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
