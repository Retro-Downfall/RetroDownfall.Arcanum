using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// Workbench editor for a Prompt — The Scriptorium. Loads a prompt, edits its template and the
/// metadata supported by <see cref="UpdatePromptRequest"/>, saves through the API, renders with
/// parameters, tests (assembled system prompt, no LLM cost), and runs live execution through the
/// prompt NDJSON execute-stream. Owns a <see cref="CancellationTokenSource"/> cancelled on dispose.
/// Persisted sampling fields and parameter-schema JSON are edited on the Metadata tab; run-only
/// overrides (seed, stop, penalties, optional model/sampling) are on the Run tab and are not saved.
/// </summary>
public sealed partial class ScriptoriumViewModel : ViewModelBase, IDisposable
{

    private readonly IPromptEditorDataSource _dataSource;

    private readonly INavigationService _navigation;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IConfirmationDialogService _confirmationDialog;

    private readonly IArtifactFileDialogService _fileDialog;

    private readonly ITextInputDialogService _textInputDialog;

    private readonly IWhispersService _whispers;

    private readonly CancellationTokenSource _lifetimeCts = new();

    private CancellationTokenSource? _runCts;

    private bool _disposed;

    [ObservableProperty]
    private PromptDetailDto? _prompt;

    [ObservableProperty]
    private string _template = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    private string _tagsText = string.Empty;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _provider = string.Empty;

    [ObservableProperty]
    private string _temperatureText = string.Empty;

    [ObservableProperty]
    private string _topPText = string.Empty;

    [ObservableProperty]
    private string _maxOutputTokensText = string.Empty;

    [ObservableProperty]
    private string _parameterSchemaJson = string.Empty;

    [ObservableProperty]
    private string _defaultParametersJson = string.Empty;

    [ObservableProperty]
    private string _parametersText = string.Empty;

    [ObservableProperty]
    private string _renderedText = string.Empty;

    [ObservableProperty]
    private int _renderTokenCount;

    [ObservableProperty]
    private string _testAssembledText = string.Empty;

    [ObservableProperty]
    private int _testTokenCount;

    [ObservableProperty]
    private string? _testResolvedSpell;

    [ObservableProperty]
    private int _testMcpServerCount;

    [ObservableProperty]
    private string _runUserMessage = string.Empty;

    [ObservableProperty]
    private string _runModel = string.Empty;

    [ObservableProperty]
    private string _runTemperatureText = string.Empty;

    [ObservableProperty]
    private string _runTopPText = string.Empty;

    [ObservableProperty]
    private string _runMaxOutputTokensText = string.Empty;

    [ObservableProperty]
    private string _runSeedText = string.Empty;

    [ObservableProperty]
    private string _runStopText = string.Empty;

    [ObservableProperty]
    private string _runResponseFormat = string.Empty;

    [ObservableProperty]
    private string _runPresencePenaltyText = string.Empty;

    [ObservableProperty]
    private string _runFrequencyPenaltyText = string.Empty;

    [ObservableProperty]
    private string _runResultText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private PromptVersionDto? _selectedVersion;

    public ScriptoriumViewModel(
        Guid promptId,
        IPromptEditorDataSource dataSource,
        INavigationService navigation,
        FoundryFloorViewModel foundryFloor,
        IConfirmationDialogService confirmationDialog,
        IArtifactFileDialogService fileDialog,
        ITextInputDialogService textInputDialog,
        IWhispersService whispers)
    {

        PromptId = promptId;

        _dataSource = dataSource;

        _navigation = navigation;

        _foundryFloor = foundryFloor;

        _confirmationDialog = confirmationDialog;

        _fileDialog = fileDialog;

        _textInputDialog = textInputDialog;

        _whispers = whispers;

        Title = $"Scriptorium: {promptId:D}";

        Trace = new InferenceTraceViewModel(
            openPromptTestPreview: () => StatusText = "Use the Test tab for assembled-context preview (no LLM cost).");

    }

    public override DocumentKind? Kind => DocumentKind.Prompt;

    public Guid PromptId { get; }

    public InferenceTraceViewModel Trace { get; }

    public ObservableCollection<PromptVersionDto> Versions { get; } = [];

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {

        if (!TryGuardUnsavedEdits("Reloading"))
        {

            return;

        }

        await LoadCoreAsync(cancellationToken).ConfigureAwait(true);

    }

    /// <summary>
    /// Refuses a reload that would overwrite unsaved editor work, pointing the operator at save or
    /// discard rather than silently dropping the buffer. Returns <see langword="false"/> when blocked.
    /// Matches the Spell editor so the two Workbench surfaces behave identically.
    /// </summary>
    private bool TryGuardUnsavedEdits(string action)
    {

        if (!IsEditorDirty)
        {

            return true;

        }

        LastError = $"{action} would discard unsaved editor changes — save the prompt or discard them first.";

        StatusText = "Unsaved editor changes.";

        _whispers.Show(WhisperSeverity.Warning, "The prompt editor has unsaved changes.");

        return false;

    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);

            Prompt = await _dataSource.LoadPromptAsync(PromptId, linked.Token).ConfigureAwait(true);

            if (Prompt is null)
            {

                LastError = "Failed to load prompt.";

                _foundryFloor.AppendLine($"Scriptorium failed to load prompt {PromptId:D}.");

                return;

            }

            Template = Prompt.Template ?? string.Empty;

            Description = Prompt.Description ?? string.Empty;

            Version = Prompt.Version;

            TagsText = string.Join(", ", Prompt.Tags);

            Model = Prompt.Model ?? string.Empty;

            Provider = Prompt.Provider ?? string.Empty;

            TemperatureText = Prompt.Temperature?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

            TopPText = Prompt.TopP?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

            MaxOutputTokensText = Prompt.MaxOutputTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

            ParameterSchemaJson = Prompt.ParameterSchema?.RootElement.GetRawText() ?? "{}";

            DefaultParametersJson = Prompt.DefaultParameters?.RootElement.GetRawText() ?? "{}";

            Title = $"Scriptorium: {Prompt.Name} {Prompt.Version}";

            StatusText = "Loaded.";

            CapturePersistedSnapshot();

            await LoadVersionsAsync(cancellationToken).ConfigureAwait(true);

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken)
    {

        if (Prompt is null)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);

            if (!TryParseJsonDocument(ParameterSchemaJson, out JsonDocument? parameterSchema, out string? schemaError))
            {

                LastError = schemaError;

                return;

            }

            if (!TryParseJsonDocument(DefaultParametersJson, out JsonDocument? defaultParameters, out string? defaultsError))
            {

                parameterSchema?.Dispose();

                LastError = defaultsError;

                return;

            }

            (double? temperature, string? temperatureError) = ParseOptionalDouble(TemperatureText, "Temperature");

            if (temperatureError is not null)
            {

                parameterSchema?.Dispose();

                defaultParameters?.Dispose();

                LastError = temperatureError;

                return;

            }

            (double? topP, string? topPError) = ParseOptionalDouble(TopPText, "TopP");

            if (topPError is not null)
            {

                parameterSchema?.Dispose();

                defaultParameters?.Dispose();

                LastError = topPError;

                return;

            }

            (int? maxOutputTokens, string? maxTokensError) = ParseOptionalInt(MaxOutputTokensText, "MaxOutputTokens");

            if (maxTokensError is not null)
            {

                parameterSchema?.Dispose();

                defaultParameters?.Dispose();

                LastError = maxTokensError;

                return;

            }

            // The PUT /api/prompts/{id} endpoint applies a field only when non-null, so null means
            // preserve. Version + Template are non-nullable and always sent. Description/Model/Provider
            // send the edited text ("" clears). Tags send the parsed array ([] clears). Blank sampling
            // fields send null to preserve. Empty/whitespace JSON editors send {}.
            UpdatePromptRequest request = new(
                Name: null,
                Version: Version,
                Description: Description,
                Tags: ParseTags(TagsText),
                Template: Template,
                ParameterSchema: parameterSchema,
                DefaultParameters: defaultParameters,
                Model: Model,
                Provider: Provider,
                Temperature: temperature,
                TopP: topP,
                MaxOutputTokens: maxOutputTokens);

            PromptDetailDto? saved = await _dataSource.SaveAsync(Prompt.Id, request, linked.Token).ConfigureAwait(true);

            if (saved is null)
            {

                LastError = "Save failed — the server rejected the request.";

                _foundryFloor.AppendLine($"Scriptorium save failed for prompt {PromptId:D}.");

                _whispers.Show(WhisperSeverity.Error, "Prompt save failed.");

                return;

            }

            Prompt = saved;

            StatusText = "Saved.";

            CapturePersistedSnapshot();

            _whispers.Show(WhisperSeverity.Success, "Prompt saved.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task RenderAsync(CancellationToken cancellationToken)
    {

        if (Prompt is null)
        {

            return;

        }

        if (!TryParseParameters(ParametersText, out Dictionary<string, string>? parameters, out string? error))
        {

            LastError = error;

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);

            PromptRenderResultDto? result = await _dataSource
                .RenderAsync(Prompt.Id, new PromptRenderRequest(parameters), linked.Token)
                .ConfigureAwait(true);

            if (result is null)
            {

                LastError = "Render failed — the server rejected the request.";

                _whispers.Show(WhisperSeverity.Error, "Prompt render failed.");

                return;

            }

            RenderedText = result.RenderedText;

            RenderTokenCount = result.TokenCount;

            StatusText = "Rendered.";

            _whispers.Show(WhisperSeverity.Success, "Prompt rendered.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task TestAsync(CancellationToken cancellationToken)
    {

        if (Prompt is null)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);

            // /test assembles the system prompt with default parameters at no LLM cost.
            PromptTestResultDto? result = await _dataSource
                .TestAsync(Prompt.Id, new TestPromptRequest(null, null, null, null, null), linked.Token)
                .ConfigureAwait(true);

            if (result is null)
            {

                LastError = "Test failed — the server rejected the request.";

                return;

            }

            TestAssembledText = result.AssembledText;

            TestTokenCount = result.TokenCount;

            TestResolvedSpell = result.ResolvedSpell is null
                ? null
                : $"{result.ResolvedSpell.Name} {result.ResolvedSpell.Version}";

            TestMcpServerCount = result.McpServerCount;

            StatusText = "Tested.";

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {

        if (Prompt is null)
        {

            return;

        }

        string userMessage = RunUserMessage.Trim();

        if (string.IsNullOrEmpty(userMessage))
        {

            LastError = "A user message is required to run a prompt.";

            return;

        }

        if (!TryParseParameters(ParametersText, out Dictionary<string, string>? parameters, out string? error))
        {

            LastError = error;

            return;

        }

        if (!TryParseRunOverrides(
                out string? runModel,
                out float? runTemperature,
                out float? runTopP,
                out int? runMaxOutputTokens,
                out IReadOnlyList<string>? runStop,
                out long? runSeed,
                out string? runResponseFormat,
                out float? runPresencePenalty,
                out float? runFrequencyPenalty,
                out string? runError))
        {

            LastError = runError;

            return;

        }

        IsRunning = true;

        LastError = null;

        RunResultText = string.Empty;

        Trace.BeginCapture("prompt", Prompt.Id.ToString("D"));

        _runCts?.Cancel();

        _runCts?.Dispose();

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);

        CancellationToken runToken = _runCts.Token;

        try
        {

            PromptExecuteRequest request = new(
                UserMessage: userMessage,
                Parameters: parameters,
                Model: runModel,
                Temperature: runTemperature,
                TopP: runTopP,
                MaxOutputTokens: runMaxOutputTokens,
                Stop: runStop,
                Seed: runSeed,
                ResponseFormat: runResponseFormat,
                PresencePenalty: runPresencePenalty,
                FrequencyPenalty: runFrequencyPenalty,
                Workspace: null,
                CampaignId: Prompt.CampaignId,
                SessionId: null,
                ToolPolicy: null);

            await foreach (IntelligenceEvent ev in _dataSource.ExecuteStreamAsync(Prompt.Id, request, runToken).ConfigureAwait(true))
            {

                ApplyIntelligenceEvent(ev);

            }

        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {

            // Stop or tab close — leave partial output as-is.

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Scriptorium run error: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Run failed.");

        }
        finally
        {

            IsRunning = false;

        }

    }

    [RelayCommand]
    private void StopExecution()
    {

        _runCts?.Cancel();

    }

    [RelayCommand]
    private void OpenInProvingGrounds()
    {

        string? model = string.IsNullOrWhiteSpace(RunModel)
            ? (string.IsNullOrWhiteSpace(Model) ? null : Model.Trim())
            : RunModel.Trim();

        _navigation.OpenOrFocusProvingGrounds(new ProvingGroundsPrefill(
            TrialTargetKind.Prompt,
            PromptId.ToString("D"),
            Workspace: null,
            Model: model));

    }

    [RelayCommand]
    public async Task CloneAsync(CancellationToken cancellationToken)
    {

        if (Prompt is null)
        {

            return;

        }

        string? newName = await _textInputDialog
            .PromptAsync("Clone prompt", "New name", $"{Prompt.Name}-copy", cancellationToken)
            .ConfigureAwait(true);

        if (newName is null)
        {

            return;

        }

        string? newVersion = await _textInputDialog
            .PromptAsync("Clone prompt", "New version", "v1", cancellationToken)
            .ConfigureAwait(true);

        if (newVersion is null)
        {

            return;

        }

        if (string.IsNullOrWhiteSpace(newName) || string.IsNullOrWhiteSpace(newVersion))
        {

            LastError = "Name and version are required to clone.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);

            ClonePromptRequest request = new(
                NewName: newName.Trim(),
                NewVersion: newVersion.Trim(),
                CampaignId: Prompt.CampaignId);

            PromptDetailDto? cloned = await _dataSource
                .CloneAsync(Prompt.Id, request, linked.Token)
                .ConfigureAwait(true);

            if (cloned is null)
            {

                LastError = "Clone failed — the server rejected the request.";

                _foundryFloor.AppendLine($"Scriptorium clone failed for prompt {PromptId:D}.");

                _whispers.Show(WhisperSeverity.Error, "Prompt clone failed.");

                return;

            }

            _navigation.OpenDocument(DocumentKind.Prompt, cloned.Id.ToString("D"));

            StatusText = "Cloned.";

            _whispers.Show(WhisperSeverity.Success, "Prompt cloned.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task DeleteAsync(CancellationToken cancellationToken)
    {

        if (Prompt is null)
        {

            return;

        }

        bool confirmed = await _confirmationDialog
            .ConfirmAsync(
                "Delete prompt",
                $"Delete prompt \"{Prompt.Name}\" {Prompt.Version}? This cannot be undone.",
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

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);

            DeleteOutcome outcome = await _dataSource.DeleteAsync(Prompt.Id, linked.Token).ConfigureAwait(true);

            if (!outcome.Success)
            {

                LastError = outcome.ErrorMessage is { Length: > 0 } detail
                    ? $"Delete failed ({outcome.ErrorCode}): {detail}"
                    : "Delete failed — the server rejected the request.";

                _foundryFloor.AppendLine($"Scriptorium delete failed for prompt {PromptId:D} — {outcome.ErrorCode}.");

                _whispers.Show(WhisperSeverity.Error, "Prompt delete failed.");

                return;

            }

            _navigation.CloseDocument(DocumentKind.Prompt, PromptId.ToString("D"));

            _navigation.FocusPanel(PanelKind.FoundryFloor);

            _foundryFloor.AppendLine($"Deleted prompt {Prompt.Name} {Prompt.Version}.");

            _whispers.Show(WhisperSeverity.Success, "Prompt deleted.");

        }
        catch (OperationCanceledException)
        {

            // Closing the document mid-delete cancels the linked token. That is an ordinary outcome,
            // not a crash: unhandled it escapes onto the dispatcher from a fire-and-forget command.
            StatusText = "Delete cancelled.";

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task ExportAsync(CancellationToken cancellationToken)
    {

        if (Prompt is null)
        {

            return;

        }

        string suggestedFileName = $"{Prompt.Name}-{Prompt.Version}.json";

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

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);

            PromptExportDto? export = await _dataSource.ExportAsync(Prompt.Id, linked.Token).ConfigureAwait(true);

            if (export is null)
            {

                LastError = "Export failed — the server rejected the request.";

                _whispers.Show(WhisperSeverity.Error, "Prompt export failed.");

                return;

            }

            string json = JsonSerializer.Serialize(export, TheForgeJsonContext.Default.PromptExportDto);

            await File.WriteAllTextAsync(path, json, linked.Token).ConfigureAwait(true);

            StatusText = "Exported.";

            _whispers.Show(WhisperSeverity.Success, "Prompt exported.");

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Scriptorium export error: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Prompt export failed.");

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

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);

            (PromptExportDto? payload, string? readError) = await ArtifactImportExportHelper
                .ReadJsonAsync(path, TheForgeJsonContext.Default.PromptExportDto, linked.Token)
                .ConfigureAwait(true);

            if (payload is null)
            {

                LastError = readError ?? "Failed to read prompt export JSON.";

                _foundryFloor.AppendLine($"Scriptorium import read failed: {LastError}");

                _whispers.Show(WhisperSeverity.Error, "Prompt import failed.");

                return;

            }

            PromptImportRequest request = new(payload, Prompt?.CampaignId);

            DataSourceResult<PromptSummaryDto> result = await _dataSource
                .ImportAsync(request, linked.Token)
                .ConfigureAwait(true);

            if (!result.Success || result.Data is null)
            {

                string detail = FormatImportError(result.ErrorCode, result.ErrorMessage, "Prompt import failed.");

                LastError = detail;

                _foundryFloor.AppendLine($"Scriptorium import failed: {detail}");

                string whisper = string.Equals(result.ErrorCode, ErrorCodes.Prompt.DuplicateVersion, StringComparison.Ordinal)
                    ? "Prompt duplicate version."
                    : "Prompt import failed.";

                _whispers.Show(WhisperSeverity.Error, whisper);

                return;

            }

            StatusText = $"Imported {result.Data.Name} {result.Data.Version}.";

            _foundryFloor.AppendLine($"Prompt imported: {result.Data.Name} {result.Data.Version} from {path}.");

            _whispers.Show(WhisperSeverity.Success, "Prompt imported.");

            _navigation.OpenDocument(DocumentKind.Prompt, result.Data.Id.ToString());

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Scriptorium import error: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Prompt import failed.");

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
    public async Task LoadVersionsAsync(CancellationToken cancellationToken)
    {

        if (Prompt is null)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);

            IReadOnlyList<PromptVersionDto> versions = await _dataSource
                .ListVersionsAsync(Prompt.Name, Prompt.CampaignId, linked.Token)
                .ConfigureAwait(true);

            Versions.Clear();

            foreach (PromptVersionDto version in versions)
            {

                Versions.Add(version);

            }

            StatusText = $"Loaded {versions.Count} version(s).";

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    private void OpenVersion(PromptVersionDto? version)
    {

        if (version is null)
        {

            return;

        }

        _navigation.OpenDocument(DocumentKind.Prompt, version.Id.ToString("D"));

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        _lifetimeCts.Cancel();

        _lifetimeCts.Dispose();

        _runCts?.Cancel();

        _runCts?.Dispose();

        GC.SuppressFinalize(this);

    }

    private void ApplyIntelligenceEvent(IntelligenceEvent ev)
    {

        Trace.Capture(ev);

        switch (ev.Type)
        {

            case IntelligenceEventType.Token:
                RunResultText += ev.Data ?? string.Empty;
                break;

            case IntelligenceEventType.Error:
                LastError = ev.Message;
                _foundryFloor.AppendLine($"Scriptorium run error: {ev.Message}");
                _whispers.Show(WhisperSeverity.Error, "Run failed.");
                break;

            case IntelligenceEventType.Result:
                StatusText = "Run complete.";
                _whispers.Show(WhisperSeverity.Success, "Run complete.");
                break;

            case IntelligenceEventType.SessionBound:
                StatusText = string.IsNullOrWhiteSpace(ev.Message)
                    ? "Session bound."
                    : $"Session bound: {ev.Message}";

                if (Guid.TryParse(ev.Message, out Guid boundSessionId))
                {

                    _navigation.OpenDocument(DocumentKind.Session, boundSessionId.ToString("D"));

                }
                break;

            default:
                // Tool/ward/status events are not surfaced in this slice.
                break;

        }

    }

    private bool TryParseRunOverrides(
        out string? model,
        out float? temperature,
        out float? topP,
        out int? maxOutputTokens,
        out IReadOnlyList<string>? stop,
        out long? seed,
        out string? responseFormat,
        out float? presencePenalty,
        out float? frequencyPenalty,
        out string? error)
    {

        model = string.IsNullOrWhiteSpace(RunModel) ? null : RunModel.Trim();

        (double? temperatureDouble, string? temperatureError) = ParseOptionalDouble(RunTemperatureText, "Run temperature");

        if (temperatureError is not null)
        {

            temperature = null;

            topP = null;

            maxOutputTokens = null;

            stop = null;

            seed = null;

            responseFormat = null;

            presencePenalty = null;

            frequencyPenalty = null;

            error = temperatureError;

            return false;

        }

        temperature = temperatureDouble is null ? null : (float)temperatureDouble.Value;

        (double? topPDouble, string? topPError) = ParseOptionalDouble(RunTopPText, "Run TopP");

        if (topPError is not null)
        {

            topP = null;

            maxOutputTokens = null;

            stop = null;

            seed = null;

            responseFormat = null;

            presencePenalty = null;

            frequencyPenalty = null;

            error = topPError;

            return false;

        }

        topP = topPDouble is null ? null : (float)topPDouble.Value;

        (int? maxTokens, string? maxTokensError) = ParseOptionalInt(RunMaxOutputTokensText, "Run MaxOutputTokens");

        if (maxTokensError is not null)
        {

            maxOutputTokens = null;

            stop = null;

            seed = null;

            responseFormat = null;

            presencePenalty = null;

            frequencyPenalty = null;

            error = maxTokensError;

            return false;

        }

        maxOutputTokens = maxTokens;

        (long? parsedSeed, string? seedError) = ParseOptionalLong(RunSeedText, "Run seed");

        if (seedError is not null)
        {

            stop = null;

            seed = null;

            responseFormat = null;

            presencePenalty = null;

            frequencyPenalty = null;

            error = seedError;

            return false;

        }

        seed = parsedSeed;

        stop = ParseStopList(RunStopText);

        responseFormat = string.IsNullOrWhiteSpace(RunResponseFormat) ? null : RunResponseFormat.Trim();

        (float? presence, string? presenceError) = ParseOptionalFloat(RunPresencePenaltyText, "Run presence penalty");

        if (presenceError is not null)
        {

            presencePenalty = null;

            frequencyPenalty = null;

            error = presenceError;

            return false;

        }

        presencePenalty = presence;

        (float? frequency, string? frequencyError) = ParseOptionalFloat(RunFrequencyPenaltyText, "Run frequency penalty");

        if (frequencyError is not null)
        {

            frequencyPenalty = null;

            error = frequencyError;

            return false;

        }

        frequencyPenalty = frequency;

        error = null;

        return true;

    }

    private static bool TryParseJsonDocument(string text, out JsonDocument? document, out string? error)
    {

        if (string.IsNullOrWhiteSpace(text))
        {

            document = JsonDocument.Parse("{}");

            error = null;

            return true;

        }

        try
        {

            document = JsonDocument.Parse(text);

            error = null;

            return true;

        }
        catch (JsonException ex)
        {

            document = null;

            error = $"Invalid JSON: {ex.Message}";

            return false;

        }

    }

    private static (double? Value, string? Error) ParseOptionalDouble(string text, string fieldName)
    {

        if (string.IsNullOrWhiteSpace(text))
        {

            return (null, null);

        }

        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {

            return (null, $"{fieldName} is not a valid number.");

        }

        return (value, null);

    }

    private static (int? Value, string? Error) ParseOptionalInt(string text, string fieldName)
    {

        if (string.IsNullOrWhiteSpace(text))
        {

            return (null, null);

        }

        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {

            return (null, $"{fieldName} is not a valid integer.");

        }

        return (value, null);

    }

    private static (long? Value, string? Error) ParseOptionalLong(string text, string fieldName)
    {

        if (string.IsNullOrWhiteSpace(text))
        {

            return (null, null);

        }

        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {

            return (null, $"{fieldName} is not a valid integer.");

        }

        return (value, null);

    }

    private static (float? Value, string? Error) ParseOptionalFloat(string text, string fieldName)
    {

        if (string.IsNullOrWhiteSpace(text))
        {

            return (null, null);

        }

        if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {

            return (null, $"{fieldName} is not a valid number.");

        }

        return (value, null);

    }

    private static IReadOnlyList<string>? ParseStopList(string text)
    {

        if (string.IsNullOrWhiteSpace(text))
        {

            return null;

        }

        string[] stops = text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static stop => stop.Length > 0)
            .ToArray();

        return stops.Length == 0 ? null : stops;

    }

    private static string[] ParseTags(string tagsText) =>
        tagsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static tag => tag.Length > 0)
            .ToArray();

    private static bool TryParseParameters(string text, out Dictionary<string, string>? parameters, out string? error)
    {

        parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {

            return true;

        }

        string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

        foreach (string line in lines)
        {

            string trimmed = line.Trim();

            if (trimmed.Length == 0)
            {

                continue;

            }

            int eq = trimmed.IndexOf('=');

            if (eq <= 0)
            {

                parameters = null;

                error = $"Malformed parameter line (expected key=value): {trimmed}";

                return false;

            }

            string key = trimmed[..eq].Trim();

            string value = trimmed[(eq + 1)..].Trim();

            if (key.Length == 0)
            {

                parameters = null;

                error = $"Malformed parameter line (empty key): {trimmed}";

                return false;

            }

            parameters[key] = value;

        }

        return true;

    }

}
