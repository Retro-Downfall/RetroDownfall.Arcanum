using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Core.Models.Comparisons;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Comparisons;
using RetroDownfall.TheForge.Ux.Services.Diff;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// Workbench document for side-by-side inference comparisons (free prompt / Prompt / Spell).
/// </summary>
public sealed partial class ComparisonWorkbenchViewModel : ViewModelBase, IDisposable
{

    public const string SingletonDocumentId = "comparison-workbench";

    public const string SensitiveHistoryWarning =
        "This history may contain prompts, model outputs, tool arguments, and file snippets. It is stored locally on this machine.";

    private readonly IComparisonWorkbenchDataSource _dataSource;

    private readonly IComparisonRunStore _runStore;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IWhispersService _whispers;

    private readonly IConfirmationDialogService _confirmationDialog;

    private readonly IArtifactFileDialogService _fileDialog;

    private readonly INavigationService _navigation;

    private readonly ITheForgeLocalMutationRunner _mutationRunner;

    private CancellationTokenSource? _runCts;

    private bool _disposed;

    private PricingSettings? _pricing;

    [ObservableProperty]
    private string _sharedInput = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private ComparisonVariantDraftViewModel? _selectedVariant;

    [ObservableProperty]
    private ComparisonVariantResultViewModel? _leftResult;

    [ObservableProperty]
    private ComparisonVariantResultViewModel? _rightResult;

    [ObservableProperty]
    private ComparisonRunRecord? _selectedHistoryRun;

    public ComparisonWorkbenchViewModel(
        IComparisonWorkbenchDataSource dataSource,
        IComparisonRunStore runStore,
        FoundryFloorViewModel foundryFloor,
        IWhispersService whispers,
        IConfirmationDialogService confirmationDialog,
        IArtifactFileDialogService fileDialog,
        INavigationService navigation,
        ITheForgeLocalMutationRunner mutationRunner,
        IInferenceTraceStore? traceStore = null)
    {

        _dataSource = dataSource;

        _runStore = runStore;

        _foundryFloor = foundryFloor;

        _whispers = whispers;

        _confirmationDialog = confirmationDialog;

        _fileDialog = fileDialog;

        _navigation = navigation;

        _mutationRunner = mutationRunner;

        Trace = new InferenceTraceViewModel(
            mutationRunner,
            traceStore,
            fileDialog);

        Title = "Comparison Workbench";

        Variants.Add(new ComparisonVariantDraftViewModel { Label = "A", SourceKind = ComparisonSourceKind.FreePrompt });

        Variants.Add(new ComparisonVariantDraftViewModel { Label = "B", SourceKind = ComparisonSourceKind.FreePrompt });

        SelectedVariant = Variants[0];

    }

    public override DocumentKind? Kind => DocumentKind.Comparison;

    public string DocumentId => SingletonDocumentId;

    public string SensitiveWarning => SensitiveHistoryWarning;

    public InferenceTraceViewModel Trace { get; }

    public ObservableCollection<ComparisonVariantDraftViewModel> Variants { get; } = [];

    public ObservableCollection<ComparisonVariantResultViewModel> Results { get; } = [];

    public BulkObservableCollection<DiffLineItem> DiffLines { get; } = [];

    public ObservableCollection<ComparisonRunRecord> History { get; } = [];

    public IReadOnlyList<ComparisonSourceKind> SourceKinds { get; } =
    [
        ComparisonSourceKind.FreePrompt,
        ComparisonSourceKind.Prompt,
        ComparisonSourceKind.Spell,
    ];

    [RelayCommand]
    public void AddVariant()
    {

        string label = ((char)('A' + Variants.Count)).ToString();

        ComparisonVariantDraftViewModel draft = new() { Label = label, SourceKind = ComparisonSourceKind.FreePrompt };

        Variants.Add(draft);

        SelectedVariant = draft;

    }

    [RelayCommand]
    public void RemoveSelectedVariant()
    {

        if (SelectedVariant is null || Variants.Count <= 1)
        {

            return;

        }

        Variants.Remove(SelectedVariant);

        SelectedVariant = Variants[0];

    }

    [RelayCommand]
    public async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {

        ComparisonStoreDocument document = await _runStore.LoadAsync(cancellationToken).ConfigureAwait(true);

        History.Clear();

        foreach (ComparisonRunRecord run in document.Runs)
        {

            History.Add(run);

        }

        StatusText = $"Loaded {History.Count} comparison run(s).";

    }

    [RelayCommand]
    public async Task ClearHistoryAsync(CancellationToken cancellationToken)
    {

        bool confirmed = await _confirmationDialog
            .ConfirmAsync(
                "Clear comparison history?",
                "This deletes local Comparison Workbench history on this machine.",
                cancellationToken)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        try
        {

            await _runStore
                .UpdateAsync(
                    (document, _) => Task.FromResult(
                        new ComparisonStoreDocument(
                            ComparisonRunStore.CurrentSchemaVersion,
                            document.CreatedAt,
                            now,
                            [])),
                    cancellationToken)
                .ConfigureAwait(true);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            LastError = ex.Message;

            StatusText = "Comparison history was not cleared.";

            _foundryFloor.AppendLine($"Comparison history clear blocked: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Comparison history clear blocked.");

            return;

        }

        History.Clear();

        StatusText = "Comparison history cleared.";

    }

    [RelayCommand]
    public void Cancel()
    {

        _runCts?.Cancel();

    }

    [RelayCommand]
    public async Task RunAsync(CancellationToken cancellationToken)
    {

        if (IsBusy || Variants.Count == 0)
        {

            return;

        }

        if (string.IsNullOrWhiteSpace(SharedInput)
            && Variants.All(static v => v.SourceKind == ComparisonSourceKind.FreePrompt))
        {

            LastError = "Shared input is required for free-prompt variants.";

            return;

        }

        _runCts?.Cancel();

        _runCts?.Dispose();

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        CancellationToken runToken = _runCts.Token;

        IsBusy = true;

        LastError = null;

        Results.Clear();

        DiffLines.Clear();

        LeftResult = null;

        RightResult = null;

        Trace.BeginCapture("comparison", SingletonDocumentId);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        Guid runId = Guid.NewGuid();

        List<ComparisonVariantResultRecord> persistedVariants = [];

        ComparisonRunRecord? completedRun = null;

        try
        {

            await _runStore
                .UpdateAsync(
                    async (document, admittedCancellationToken) =>
                    {

                        _pricing ??= await _dataSource
                            .GetPricingAsync(admittedCancellationToken)
                            .ConfigureAwait(true);

                        foreach (ComparisonVariantDraftViewModel variant in Variants.ToArray())
                        {

                            admittedCancellationToken.ThrowIfCancellationRequested();

                            ComparisonVariantResultViewModel result = await RunVariantAsync(
                                    variant,
                                    admittedCancellationToken)
                                .ConfigureAwait(true);

                            Results.Add(result);

                            persistedVariants.Add(result.ToRecord());

                        }

                        if (Results.Count > 0)
                        {

                            LeftResult = Results[0];

                        }

                        if (Results.Count > 1)
                        {

                            RightResult = Results[1];

                        }

                        await RefreshDiffAsync(admittedCancellationToken).ConfigureAwait(true);

                        ComparisonRunRecord run = new(
                            runId,
                            startedAt,
                            DateTimeOffset.UtcNow,
                            "comparison",
                            null,
                            Truncate(SharedInput, 240),
                            persistedVariants);

                        completedRun = run;

                        List<ComparisonRunRecord> runs = [run, .. document.Runs];

                        return new ComparisonStoreDocument(
                            ComparisonRunStore.CurrentSchemaVersion,
                            document.CreatedAt,
                            DateTimeOffset.UtcNow,
                            runs);

                    },
                    runToken)
                .ConfigureAwait(true);

            History.Insert(
                0,
                completedRun ?? throw new InvalidOperationException("The comparison run did not complete."));

            StatusText = $"Compared {Results.Count} variant(s).";

            _foundryFloor.AppendLine($"Comparison Workbench finished {Results.Count} variant(s).");

            _whispers.Show(WhisperSeverity.Success, "Comparison complete.");

        }
        catch (OperationCanceledException)
        {

            StatusText = "Comparison cancelled.";

            _whispers.Show(WhisperSeverity.Info, "Comparison cancelled.");

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            StatusText = "Comparison failed.";

            _foundryFloor.AppendLine($"Comparison Workbench error: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Comparison failed.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    partial void OnLeftResultChanged(ComparisonVariantResultViewModel? value) =>
        TaskUtilities.FireAndForget(RefreshDiffAsync(CancellationToken.None));

    partial void OnRightResultChanged(ComparisonVariantResultViewModel? value) =>
        TaskUtilities.FireAndForget(RefreshDiffAsync(CancellationToken.None));

    /// <summary>
    /// Monotonic ticket for diff refreshes. Selecting a different pair while one is in flight must not
    /// let the older comparison land on the surface afterwards.
    /// </summary>
    private int _diffRefreshGeneration;

    [RelayCommand]
    public async Task RefreshDiffAsync(CancellationToken cancellationToken)
    {

        int generation = ++_diffRefreshGeneration;

        string left = LeftResult?.Output ?? string.Empty;

        string right = RightResult?.Output ?? string.Empty;

        // Two model outputs can be long, and LCS over them is CPU-bound on the UI thread. Compute off
        // it, then publish the whole diff in one notification instead of one event per line.
        IReadOnlyList<DiffLineItem> lines = await Task
            .Run(() => LineDiff.Compare(left, right), cancellationToken)
            .ConfigureAwait(true);

        if (generation != _diffRefreshGeneration)
        {

            return;

        }

        DiffLines.ResetTo(lines);

    }

    [RelayCommand]
    public void PromoteLeftToEditor()
    {

        Promote(LeftResult);

    }

    [RelayCommand]
    public void PromoteRightToEditor()
    {

        Promote(RightResult);

    }

    [RelayCommand]
    public async Task ExportJsonAsync(CancellationToken cancellationToken)
    {

        if (SelectedHistoryRun is null && Results.Count == 0)
        {

            LastError = "Nothing to export.";

            return;

        }

        string? path = await _fileDialog
            .PickSaveJsonPathAsync($"comparison-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json", cancellationToken)
            .ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(path))
        {

            return;

        }

        if (!await TryWriteExportAsync(
                path,
                () =>
                {

                    ComparisonRunRecord run = SelectedHistoryRun
                        ?? new ComparisonRunRecord(
                            Guid.NewGuid(),
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            "comparison",
                            null,
                            Truncate(SharedInput, 240),
                            Results.Select(static r => r.ToRecord()).ToArray());

                    return JsonSerializer.Serialize(
                        run,
                        TheForgeComparisonsJsonContext.Default.ComparisonRunRecord);

                },
                "JSON",
                cancellationToken)
            .ConfigureAwait(true))
        {

            return;

        }

        StatusText = "Comparison exported (JSON).";

    }

    [RelayCommand]
    public async Task ExportMarkdownAsync(CancellationToken cancellationToken)
    {

        string? path = await _fileDialog
            .PickSaveJsonPathAsync($"comparison-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.md", cancellationToken)
            .ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(path))
        {

            return;

        }

        if (!await TryWriteExportAsync(
                path,
                BuildMarkdownExport,
                "Markdown",
                cancellationToken)
            .ConfigureAwait(true))
        {

            return;

        }

        StatusText = "Comparison exported (Markdown).";

    }

    [RelayCommand]
    public async Task ExportCsvAsync(CancellationToken cancellationToken)
    {

        string? path = await _fileDialog
            .PickSaveJsonPathAsync($"comparison-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv", cancellationToken)
            .ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(path))
        {

            return;

        }

        if (!await TryWriteExportAsync(
                path,
                BuildCsvExport,
                "CSV",
                cancellationToken)
            .ConfigureAwait(true))
        {

            return;

        }

        StatusText = "Comparison exported (CSV).";

    }

    /// <summary>
    /// Writes an export to the operator's chosen path. A destination that turns out to be unwritable
    /// is reported like every other command failure rather than escaping the command.
    /// </summary>
    private async Task<bool> TryWriteExportAsync(
        string path,
        string contents,
        string format,
        CancellationToken cancellationToken) =>
        await TryWriteExportAsync(
                path,
                () => contents,
                format,
                cancellationToken)
            .ConfigureAwait(true);

    private async Task<bool> TryWriteExportAsync(
        string path,
        Func<string> contentsFactory,
        string format,
        CancellationToken cancellationToken)
    {

        try
        {

            await _mutationRunner
                .RunAsync(
                    path,
                    admittedCancellationToken => File.WriteAllTextAsync(
                        path,
                        contentsFactory(),
                        Encoding.UTF8,
                        admittedCancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);

            return true;

        }
        catch (OperationCanceledException)
        {

            StatusText = "Comparison export cancelled.";

            return false;

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            StatusText = $"Comparison export failed ({format}).";

            _foundryFloor.AppendLine($"Comparison Workbench export error: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Comparison export failed.");

            return false;

        }

    }

    private string BuildMarkdownExport()
    {

        StringBuilder sb = new();

        sb.AppendLine("# Comparison Workbench");

        sb.AppendLine();

        sb.AppendLine($"Input: {SharedInput}");

        sb.AppendLine();

        foreach (ComparisonVariantResultViewModel result in Results)
        {

            sb.AppendLine($"## {result.Label}");

            sb.AppendLine();

            sb.AppendLine($"- Model: {result.Model ?? "—"}");

            sb.AppendLine($"- Provider: {result.Provider ?? "—"}");

            sb.AppendLine($"- Tokens: prompt={result.PromptTokens} completion={result.CompletionTokens} total={result.TotalTokens} cached={result.CachedTokens}");

            sb.AppendLine($"- Latency: {result.LatencyMs} ms");

            sb.AppendLine($"- Finish: {result.FinishReason ?? "—"}");

            sb.AppendLine($"- Cost: {result.CostLabel}");

            sb.AppendLine();

            sb.AppendLine(result.Output);

            sb.AppendLine();

        }

        return sb.ToString();

    }

    private string BuildCsvExport()
    {

        StringBuilder sb = new();

        sb.AppendLine("Label,Model,Provider,PromptTokens,CompletionTokens,TotalTokens,CachedTokens,LatencyMs,FinishReason,CostLabel,CostUsd,Error");

        foreach (ComparisonVariantResultViewModel result in Results)
        {

            sb.Append(Csv(result.Label)).Append(',')
                .Append(Csv(result.Model)).Append(',')
                .Append(Csv(result.Provider)).Append(',')
                .Append(result.PromptTokens?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(result.CompletionTokens?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(result.TotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(result.CachedTokens?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(result.LatencyMs?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(Csv(result.FinishReason)).Append(',')
                .Append(Csv(result.CostLabel)).Append(',')
                .Append(result.CostUsd?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(Csv(result.Error))
                .AppendLine();

        }

        return sb.ToString();

    }

    private async Task<ComparisonVariantResultViewModel> RunVariantAsync(
        ComparisonVariantDraftViewModel variant,
        CancellationToken cancellationToken)
    {

        Stopwatch sw = Stopwatch.StartNew();

        StringBuilder output = new();

        List<string> toolNames = [];

        ChatCompletionUsage? usage = null;

        string? finishReason = null;

        string? error = null;

        string? boundModel = variant.Model;

        string? boundProvider = variant.Provider;

        try
        {

            await foreach (IntelligenceEvent ev in CreateStream(variant, cancellationToken).ConfigureAwait(true))
            {

                Trace.Capture(ev);

                if (ev.Type == IntelligenceEventType.Token && !string.IsNullOrEmpty(ev.Data))
                {

                    output.Append(ev.Data);

                }

                if (ev.Type == IntelligenceEventType.ToolCall && !string.IsNullOrWhiteSpace(ev.ToolCall?.Name))
                {

                    toolNames.Add(ev.ToolCall!.Name);

                }

                if (ev.Type == IntelligenceEventType.Result)
                {

                    usage = ev.Usage ?? usage;

                    finishReason = ev.FinishReason ?? finishReason;

                    if (!string.IsNullOrWhiteSpace(ev.Message) && output.Length == 0)
                    {

                        output.Append(ev.Message);

                    }

                }

                if (ev.Type == IntelligenceEventType.Error)
                {

                    error = ev.Message;

                }

            }

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            error = ex.Message;

        }

        sw.Stop();

        (string costLabel, decimal? costUsd) = ComparisonCostEstimator.Estimate(usage, boundModel, _pricing);

        return new ComparisonVariantResultViewModel
        {
            VariantId = Guid.NewGuid(),
            Label = variant.Label,
            SourceKind = variant.SourceKind,
            SourceId = ResolveSourceId(variant),
            Model = boundModel,
            Provider = boundProvider,
            Output = output.ToString(),
            PromptTokens = usage?.PromptTokens,
            CompletionTokens = usage?.CompletionTokens,
            TotalTokens = usage?.TotalTokens,
            CachedTokens = usage?.CachedTokens,
            LatencyMs = sw.ElapsedMilliseconds,
            FinishReason = finishReason,
            CostLabel = costLabel,
            CostUsd = costUsd,
            ToolCallNames = toolNames,
            Error = error,
        };

    }

    private IAsyncEnumerable<IntelligenceEvent> CreateStream(
        ComparisonVariantDraftViewModel variant,
        CancellationToken cancellationToken)
    {

        float? temperature = ParseFloat(variant.TemperatureText);

        float? topP = ParseFloat(variant.TopPText);

        int? maxOutputTokens = ParseInt(variant.MaxOutputTokensText);

        return variant.SourceKind switch
        {
            ComparisonSourceKind.Prompt when Guid.TryParse(variant.PromptIdText, out Guid promptId) =>
                _dataSource.RunPromptAsync(
                    promptId,
                    new PromptExecuteRequest(
                        UserMessage: SharedInput,
                        Model: NullIfWhiteSpace(variant.Model),
                        Temperature: temperature,
                        TopP: topP,
                        MaxOutputTokens: maxOutputTokens,
                        Workspace: NullIfWhiteSpace(variant.Workspace)),
                    cancellationToken),

            ComparisonSourceKind.Spell when !string.IsNullOrWhiteSpace(variant.SpellName) =>
                _dataSource.RunSpellAsync(
                    variant.SpellName.Trim(),
                    new SpellExecuteRequest(
                        Prompt: SharedInput,
                        Model: NullIfWhiteSpace(variant.Model),
                        Temperature: temperature,
                        TopP: topP,
                        MaxOutputTokens: maxOutputTokens,
                        Workspace: NullIfWhiteSpace(variant.Workspace)),
                    cancellationToken),

            _ => _dataSource.RunFreePromptAsync(
                SharedInput,
                NullIfWhiteSpace(variant.Model),
                temperature,
                topP,
                maxOutputTokens,
                cancellationToken),
        };

    }

    private void Promote(ComparisonVariantResultViewModel? result)
    {

        if (result is null)
        {

            return;

        }

        if (result.SourceKind == ComparisonSourceKind.Prompt && Guid.TryParse(result.SourceId, out Guid promptId))
        {

            _navigation.OpenDocument(DocumentKind.Prompt, promptId.ToString("D"));

            return;

        }

        if (result.SourceKind == ComparisonSourceKind.Spell && !string.IsNullOrWhiteSpace(result.SourceId))
        {

            _navigation.OpenDocument(DocumentKind.Spell, result.SourceId);

        }

    }

    private static string? ResolveSourceId(ComparisonVariantDraftViewModel variant) =>
        variant.SourceKind switch
        {
            ComparisonSourceKind.Prompt => variant.PromptIdText,
            ComparisonSourceKind.Spell => variant.SpellName,
            _ => null,
        };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static float? ParseFloat(string? text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : null;

    private static int? ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static string Csv(string? value)
    {

        string raw = value ?? string.Empty;

        if (raw.Contains('"') || raw.Contains(',') || raw.Contains('\n'))
        {

            return "\"" + raw.Replace("\"", "\"\"") + "\"";

        }

        return raw;

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

    }

}

public enum ComparisonSourceKind
{
    FreePrompt,
    Prompt,
    Spell,
}

public sealed partial class ComparisonVariantDraftViewModel : ObservableObject
{

    [ObservableProperty]
    private string _label = "A";

    [ObservableProperty]
    private ComparisonSourceKind _sourceKind = ComparisonSourceKind.FreePrompt;

    [ObservableProperty]
    private string _promptIdText = string.Empty;

    [ObservableProperty]
    private string _spellName = string.Empty;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _provider = string.Empty;

    [ObservableProperty]
    private string _workspace = string.Empty;

    [ObservableProperty]
    private string _temperatureText = string.Empty;

    [ObservableProperty]
    private string _topPText = string.Empty;

    [ObservableProperty]
    private string _maxOutputTokensText = string.Empty;

}

public sealed partial class ComparisonVariantResultViewModel : ObservableObject
{

    public Guid VariantId { get; init; }

    public string Label { get; init; } = string.Empty;

    public ComparisonSourceKind SourceKind { get; init; }

    public string? SourceId { get; init; }

    public string? Model { get; init; }

    public string? Provider { get; init; }

    public string Output { get; init; } = string.Empty;

    public int? PromptTokens { get; init; }

    public int? CompletionTokens { get; init; }

    public int? TotalTokens { get; init; }

    public int? CachedTokens { get; init; }

    public long? LatencyMs { get; init; }

    public string? FinishReason { get; init; }

    public string CostLabel { get; init; } = ComparisonCostEstimator.CostUnavailable;

    public decimal? CostUsd { get; init; }

    public IReadOnlyList<string> ToolCallNames { get; init; } = [];

    public string? Error { get; init; }

    public string MetricsSummary =>
        $"tokens p={PromptTokens} c={CompletionTokens} t={TotalTokens} cached={CachedTokens}; "
        + $"{LatencyMs} ms; finish={FinishReason ?? "—"}; {CostLabel}";

    public ComparisonVariantResultRecord ToRecord() =>
        new(
            VariantId,
            Label,
            Model,
            Provider,
            Output,
            PromptTokens,
            CompletionTokens,
            TotalTokens,
            CachedTokens,
            LatencyMs,
            FinishReason,
            CostLabel,
            CostUsd,
            ToolCallNames,
            Error);

}
