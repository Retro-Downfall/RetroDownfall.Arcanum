using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// Workbench editor for a Prompt — The Scriptorium. Loads a prompt, edits its template and the
/// metadata supported by <see cref="UpdatePromptRequest"/>, saves through the API, renders with
/// parameters, tests (assembled system prompt, no LLM cost), and runs live execution through the
/// prompt NDJSON execute-stream. Owns a <see cref="CancellationTokenSource"/> cancelled on dispose.
/// Numeric sampling fields and advanced parameter-schema JSON editing are deferred.
/// </summary>
public sealed partial class ScriptoriumViewModel : ViewModelBase, IDisposable
{

    private readonly IPromptEditorDataSource _dataSource;

    private readonly INavigationService _navigation;

    private readonly FoundryFloorViewModel _foundryFloor;

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
    private string _runResultText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    public ScriptoriumViewModel(
        Guid promptId,
        IPromptEditorDataSource dataSource,
        INavigationService navigation,
        FoundryFloorViewModel foundryFloor)
    {

        PromptId = promptId;

        _dataSource = dataSource;

        _navigation = navigation;

        _foundryFloor = foundryFloor;

        Title = $"Scriptorium: {promptId:D}";

    }

    public override DocumentKind? Kind => DocumentKind.Prompt;

    public Guid PromptId { get; }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
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

            Title = $"Scriptorium: {Prompt.Name} {Prompt.Version}";

            StatusText = "Loaded.";

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

            // The PUT /api/prompts/{id} endpoint applies a field only when non-null, so null means
            // preserve. Version + Template are non-nullable and always sent. Description/Model/Provider
            // send the edited text ("" clears). Tags send the parsed array ([] clears). ParameterSchema
            // and DefaultParameters send null to preserve (advanced JSON editing is deferred).
            UpdatePromptRequest request = new(
                Name: null,
                Version: Version,
                Description: Description,
                Tags: ParseTags(TagsText),
                Template: Template,
                ParameterSchema: null,
                DefaultParameters: null,
                Model: Model,
                Provider: Provider,
                Temperature: null,
                TopP: null,
                MaxOutputTokens: null);

            PromptDetailDto? saved = await _dataSource.SaveAsync(Prompt.Id, request, linked.Token).ConfigureAwait(true);

            if (saved is null)
            {

                LastError = "Save failed — the server rejected the request.";

                _foundryFloor.AppendLine($"Scriptorium save failed for prompt {PromptId:D}.");

                return;

            }

            Prompt = saved;

            StatusText = "Saved.";

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

                return;

            }

            RenderedText = result.RenderedText;

            RenderTokenCount = result.TokenCount;

            StatusText = "Rendered.";

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

        IsRunning = true;

        LastError = null;

        RunResultText = string.Empty;

        _runCts?.Cancel();

        _runCts?.Dispose();

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);

        CancellationToken runToken = _runCts.Token;

        try
        {

            PromptExecuteRequest request = new(
                UserMessage: userMessage,
                Parameters: parameters,
                Model: null,
                Temperature: null,
                TopP: null,
                MaxOutputTokens: null,
                Stop: null,
                Seed: null,
                ResponseFormat: null,
                PresencePenalty: null,
                FrequencyPenalty: null,
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

        switch (ev.Type)
        {

            case IntelligenceEventType.Token:
                RunResultText += ev.Data ?? string.Empty;
                break;

            case IntelligenceEventType.Error:
                LastError = ev.Message;
                _foundryFloor.AppendLine($"Scriptorium run error: {ev.Message}");
                break;

            case IntelligenceEventType.Result:
                StatusText = "Run complete.";
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
