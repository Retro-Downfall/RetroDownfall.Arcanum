using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.TheForge.Core.IO;
using RetroDownfall.TheForge.Core.Models.Trials;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

public sealed partial class ProvingGroundsViewModel
{

    public const string SensitiveHistoryWarning =
        "This history may contain prompts, model outputs, tool arguments, and file snippets. It is stored locally on this machine.";

    private readonly ITrialSuiteStore _suiteStore;

    private readonly IArtifactFileDialogService _fileDialog;

    private readonly ITheForgeLocalMutationRunner _mutationRunner;

    private TrialSuiteStoreDocument _suiteDocument;

    [ObservableProperty]
    private TrialSuiteRecord? _selectedSuite;

    [ObservableProperty]
    private TrialSuiteItemRecord? _selectedSuiteItem;

    [ObservableProperty]
    private TrialSuiteRunRecord? _selectedSuiteRun;

    [ObservableProperty]
    private string _newSuiteName = "New suite";

    [ObservableProperty]
    private string? _suiteStatusText;

    public ObservableCollection<TrialSuiteRecord> Suites { get; } = [];

    public ObservableCollection<TrialSuiteRunRecord> SelectedSuiteRuns { get; } = [];

    public string SuitePassRateSummary
    {

        get
        {

            if (SelectedSuite is null || SelectedSuiteRuns.Count == 0)
            {

                return "No suite runs yet.";

            }

            int totalItems = 0;

            int passedItems = 0;

            foreach (TrialSuiteRunRecord run in SelectedSuiteRuns)
            {

                foreach (TrialSuiteRunResultRecord result in run.Results)
                {

                    totalItems++;

                    if (result.Passed)
                    {

                        passedItems++;

                    }

                }

            }

            if (totalItems == 0)
            {

                return "No suite run results yet.";

            }

            double rate = 100.0 * passedItems / totalItems;

            return $"Pass rate: {passedItems}/{totalItems} ({rate:0.#}%) across {SelectedSuiteRuns.Count} run(s).";

        }

    }

    partial void OnSelectedSuiteChanged(TrialSuiteRecord? value)
    {

        SelectedSuiteRuns.Clear();

        if (value is not null)
        {

            foreach (TrialSuiteRunRecord run in value.Runs.OrderByDescending(static r => r.StartedAt))
            {

                SelectedSuiteRuns.Add(run);

            }

        }

        OnPropertyChanged(nameof(SuitePassRateSummary));

    }

    [RelayCommand]
    public async Task LoadSuitesAsync(CancellationToken cancellationToken)
    {

        _suiteDocument = await _suiteStore.LoadAsync(cancellationToken).ConfigureAwait(true);

        Suites.Clear();

        foreach (TrialSuiteRecord suite in _suiteDocument.Suites.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase))
        {

            Suites.Add(suite);

        }

        if (SelectedSuite is not null)
        {

            SelectedSuite = Suites.FirstOrDefault(s => s.Id == SelectedSuite.Id);

        }

        SuiteStatusText = $"Loaded {Suites.Count} suite(s) from {_suiteStore.StorePath}.";

    }

    [RelayCommand]
    private async Task CreateSuiteAsync(CancellationToken cancellationToken)
    {

        string name = string.IsNullOrWhiteSpace(NewSuiteName) ? "New suite" : NewSuiteName.Trim();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        TrialSuiteRecord suite = new(Guid.NewGuid(), name, null, now, now, [], []);

        List<TrialSuiteRecord> suites = _suiteDocument.Suites.ToList();

        suites.Add(suite);

        if (!await PersistSuitesAsync(suites, cancellationToken).ConfigureAwait(true))
        {

            return;

        }

        SelectedSuite = Suites.FirstOrDefault(s => s.Id == suite.Id);

        _whispers.Show(WhisperSeverity.Success, $"Created suite “{name}”.");

    }

    [RelayCommand]
    private async Task DeleteSuiteAsync(CancellationToken cancellationToken)
    {

        if (SelectedSuite is null)
        {

            return;

        }

        bool confirmed = await _confirmationDialog
            .ConfirmAsync(
                "Delete suite",
                $"Delete suite “{SelectedSuite.Name}” and its local run history?",
                cancellationToken,
                confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        Guid id = SelectedSuite.Id;

        List<TrialSuiteRecord> suites = _suiteDocument.Suites.Where(s => s.Id != id).ToList();

        if (!await PersistSuitesAsync(suites, cancellationToken).ConfigureAwait(true))
        {

            return;

        }

        SelectedSuite = null;

        _whispers.Show(WhisperSeverity.Info, "Suite deleted.");

    }

    [RelayCommand]
    private async Task AddDraftToSuiteAsync(CancellationToken cancellationToken)
    {

        if (SelectedSuite is null)
        {

            SuiteStatusText = "Select a suite first.";

            return;

        }

        if (!TryBuildTrial(out Trial? trial, out string? validationError) || trial is null)
        {

            ValidationMessage = validationError;

            _whispers.Show(WhisperSeverity.Warning, validationError ?? "Trial is invalid.");

            return;

        }

        string itemName = string.IsNullOrWhiteSpace(TrialName) ? trial.Target : TrialName.Trim();

        TrialSuiteItemRecord item = new(Guid.NewGuid(), itemName, trial, [], null);

        List<TrialSuiteItemRecord> items = SelectedSuite.Trials.ToList();

        items.Add(item);

        TrialSuiteRecord updated = SelectedSuite with
        {
            Trials = items,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        List<TrialSuiteRecord> suites = _suiteDocument.Suites.Select(s => s.Id == updated.Id ? updated : s).ToList();

        if (!await PersistSuitesAsync(suites, cancellationToken).ConfigureAwait(true))
        {

            return;

        }

        SelectedSuite = Suites.FirstOrDefault(s => s.Id == updated.Id);

        _whispers.Show(WhisperSeverity.Success, $"Added “{itemName}” to suite.");

    }

    [RelayCommand]
    private async Task RunSelectedSuiteItemAsync(CancellationToken cancellationToken)
    {

        if (SelectedSuite is null || SelectedSuiteItem is null)
        {

            return;

        }

        await RunSuiteCoreAsync(SelectedSuite, [SelectedSuiteItem], cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    private async Task RunSelectedSuiteAsync(CancellationToken cancellationToken)
    {

        if (SelectedSuite is null || SelectedSuite.Trials.Count == 0)
        {

            SuiteStatusText = "Suite has no Trials.";

            return;

        }

        await RunSuiteCoreAsync(SelectedSuite, SelectedSuite.Trials, cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    private async Task ClearSuiteRunHistoryAsync(CancellationToken cancellationToken)
    {

        if (SelectedSuite is null)
        {

            return;

        }

        bool confirmed = await _confirmationDialog
            .ConfirmAsync(
                "Clear run history",
                $"Clear local run history for “{SelectedSuite.Name}”? {SensitiveHistoryWarning}",
                cancellationToken,
                confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        TrialSuiteRecord updated = SelectedSuite with { Runs = [], UpdatedAt = DateTimeOffset.UtcNow };

        List<TrialSuiteRecord> suites = _suiteDocument.Suites.Select(s => s.Id == updated.Id ? updated : s).ToList();

        if (!await PersistSuitesAsync(suites, cancellationToken).ConfigureAwait(true))
        {

            return;

        }

        SelectedSuite = Suites.FirstOrDefault(s => s.Id == updated.Id);

        _whispers.Show(WhisperSeverity.Info, "Suite run history cleared.");

    }

    [RelayCommand]
    private async Task ExportSuiteAsync(CancellationToken cancellationToken)
    {

        if (SelectedSuite is null)
        {

            return;

        }

        string? path = await ArtifactImportExportHelper
            .PickSavePathOrNullAsync(_fileDialog, $"{SelectedSuite.Name}.json", cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        string? writeError;

        try
        {

            writeError = null;

            await _mutationRunner
                .RunAsync(
                    path,
                    admittedCancellationToken => ArtifactImportExportHelper
                        .WriteJsonAlreadyAdmittedAsync(
                            path,
                            SelectedSuite
                                ?? throw new TheForgeStoreChangedException(_suiteStore.StorePath),
                            TheForgeTrialSuitesJsonContext.Default.TrialSuiteRecord,
                            admittedCancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);

        }
        catch (OperationCanceledException)
        {

            SuiteStatusText = "Suite export cancelled.";

            return;

        }
        catch (Exception ex)
        {

            writeError = ex.Message;

        }

        if (writeError is not null)
        {

            SuiteStatusText = "Suite export failed.";

            _foundryFloor.AppendLine($"Suite export error: {writeError}");

            _whispers.Show(WhisperSeverity.Error, "Suite export failed.");

            return;

        }

        _whispers.Show(WhisperSeverity.Success, "Suite exported.");

    }

    [RelayCommand]
    private async Task ImportSuiteAsync(CancellationToken cancellationToken)
    {

        string? path = await ArtifactImportExportHelper
            .PickOpenPathOrNullAsync(_fileDialog, cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        (TrialSuiteRecord? imported, string? error) = await ArtifactImportExportHelper
            .ReadJsonAsync(path, TheForgeTrialSuitesJsonContext.Default.TrialSuiteRecord, cancellationToken)
            .ConfigureAwait(true);

        if (imported is null)
        {

            _whispers.Show(WhisperSeverity.Error, error ?? "Import failed.");

            return;

        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        TrialSuiteRecord suite = imported with
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
            Runs = [],
        };

        List<TrialSuiteRecord> suites = _suiteDocument.Suites.ToList();

        suites.Add(suite);

        if (!await PersistSuitesAsync(suites, cancellationToken).ConfigureAwait(true))
        {

            return;

        }

        SelectedSuite = Suites.FirstOrDefault(s => s.Id == suite.Id);

        _whispers.Show(WhisperSeverity.Success, $"Imported suite “{suite.Name}”.");

    }

    private async Task RunSuiteCoreAsync(
        TrialSuiteRecord suite,
        IReadOnlyList<TrialSuiteItemRecord> items,
        CancellationToken cancellationToken)
    {

        if (IsBusy)
        {

            return;

        }

        _runCts?.Cancel();

        _runCts?.Dispose();

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        CancellationToken runToken = _runCts.Token;

        IsBusy = true;

        StatusText = $"Running suite “{suite.Name}”…";

        SuiteStatusText = SensitiveHistoryWarning;

        DateTimeOffset started = DateTimeOffset.UtcNow;

        List<TrialSuiteRunResultRecord> results = [];

        Stopwatch suiteWatch = Stopwatch.StartNew();

        Guid[] requestedItemIds = items.Select(static item => item.Id).ToArray();

        TrialSuiteRunRecord? completedRun = null;

        bool suiteDeleted = false;

        try
        {

            _suiteDocument = await _suiteStore
                .UpdatePreparedAsync(
                    async (document, admittedCancellationToken) =>
                    {

                        TrialSuiteRecord current = document.Suites.FirstOrDefault(s => s.Id == suite.Id)
                            ?? throw new TheForgeStoreChangedException(_suiteStore.StorePath);

                        List<TrialSuiteItemRecord> currentItems = [];

                        foreach (Guid itemId in requestedItemIds)
                        {

                            TrialSuiteItemRecord item = current.Trials.FirstOrDefault(t => t.Id == itemId)
                                ?? throw new TheForgeStoreChangedException(_suiteStore.StorePath);

                            currentItems.Add(item);

                        }

                        foreach (TrialSuiteItemRecord item in currentItems)
                        {

                            admittedCancellationToken.ThrowIfCancellationRequested();

                            Stopwatch itemWatch = Stopwatch.StartNew();

                            DataSourceResult<TrialResult> outcome = await _dataSource
                                .RunAsync(item.Trial, admittedCancellationToken)
                                .ConfigureAwait(true);

                            itemWatch.Stop();

                            if (!outcome.Success || outcome.Data is null)
                            {

                                results.Add(new TrialSuiteRunResultRecord(
                                    item.Id,
                                    false,
                                    string.Empty,
                                    [],
                                    null,
                                    null,
                                    null,
                                    itemWatch.ElapsedMilliseconds,
                                    outcome.ErrorMessage ?? "Trial run failed."));

                                continue;

                            }

                            TrialResult trialResult = outcome.Data;

                            results.Add(new TrialSuiteRunResultRecord(
                                item.Id,
                                trialResult.Passed,
                                trialResult.Output,
                                trialResult.Verdicts,
                                trialResult.Usage?.PromptTokens,
                                trialResult.Usage?.CompletionTokens,
                                trialResult.Usage?.TotalTokens,
                                itemWatch.ElapsedMilliseconds,
                                null));

                            LastResult = trialResult;

                        }

                        suiteWatch.Stop();

                        TrialSuiteRunRecord run = new(
                            Guid.NewGuid(),
                            suite.Id,
                            started,
                            DateTimeOffset.UtcNow,
                            Model,
                            null,
                            $"items={currentItems.Count}; elapsedMs={suiteWatch.ElapsedMilliseconds}",
                            results);

                        completedRun = run;

                        return run;

                    },
                    (document, run) =>
                    {

                        TrialSuiteRecord? current = document.Suites.FirstOrDefault(s => s.Id == suite.Id);

                        if (current is null)
                        {

                            suiteDeleted = true;

                            return document;

                        }

                        TrialSuiteRecord updated = current with
                        {
                            Runs = current.Runs.Prepend(run).ToList(),
                            UpdatedAt = DateTimeOffset.UtcNow,
                        };

                        IReadOnlyList<TrialSuiteRecord> suites = document.Suites
                            .Select(s => s.Id == updated.Id ? updated : s)
                            .ToArray();

                        return new TrialSuiteStoreDocument(
                            TrialSuiteStore.CurrentSchemaVersion,
                            document.CreatedAt,
                            DateTimeOffset.UtcNow,
                            suites);

                    },
                    runToken)
                .ConfigureAwait(true);

            ApplySuiteDocument();

            if (suiteDeleted)
            {

                StatusText = $"Suite “{suite.Name}” was deleted during the run; results were not saved.";

                _whispers.Show(WhisperSeverity.Warning, StatusText);

                return;

            }

            SelectedSuite = Suites.FirstOrDefault(s => s.Id == suite.Id);

            SelectedSuiteRun = SelectedSuiteRuns.FirstOrDefault(r => r.Id == completedRun?.Id);

            int passed = results.Count(static r => r.Passed);

            StatusText = $"Suite finished: {passed}/{results.Count} passed.";

            _whispers.Show(
                passed == results.Count ? WhisperSeverity.Success : WhisperSeverity.Warning,
                StatusText);

        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {

            StatusText = "Suite run cancelled.";

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            SuiteStatusText = "Suite run was not saved.";

            _foundryFloor.AppendLine($"Trial suite run blocked: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Suite run was not saved.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    private async Task<bool> PersistSuitesAsync(
        IReadOnlyList<TrialSuiteRecord> suites,
        CancellationToken cancellationToken)
    {

        TrialSuiteStoreDocument baseline = _suiteDocument;

        try
        {

            _suiteDocument = await _suiteStore
                .UpdateAsync(
                    (current, _) => Task.FromResult(
                        new TrialSuiteStoreDocument(
                            TrialSuiteStore.CurrentSchemaVersion,
                            current.CreatedAt,
                            DateTimeOffset.UtcNow,
                            MergeSuiteChanges(baseline.Suites, suites, current.Suites))),
                    cancellationToken)
                .ConfigureAwait(true);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            LastError = ex.Message;

            SuiteStatusText = "Suite changes were not saved.";

            _foundryFloor.AppendLine($"Trial suite save blocked: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Suite changes were not saved.");

            return false;

        }

        ApplySuiteDocument();

        return true;

    }

    private void ApplySuiteDocument()
    {

        Guid? selectedSuiteId = SelectedSuite?.Id;

        Suites.Clear();

        foreach (TrialSuiteRecord suite in _suiteDocument.Suites.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase))
        {

            Suites.Add(suite);

        }

        SelectedSuite = selectedSuiteId.HasValue
            ? Suites.FirstOrDefault(s => s.Id == selectedSuiteId.Value)
            : null;

        OnPropertyChanged(nameof(SuitePassRateSummary));

    }

    private static IReadOnlyList<TrialSuiteRecord> MergeSuiteChanges(
        IReadOnlyList<TrialSuiteRecord> baseline,
        IReadOnlyList<TrialSuiteRecord> desired,
        IReadOnlyList<TrialSuiteRecord> current)
    {

        Dictionary<Guid, TrialSuiteRecord> baselineById = baseline.ToDictionary(static s => s.Id);

        Dictionary<Guid, TrialSuiteRecord> desiredById = desired.ToDictionary(static s => s.Id);

        List<TrialSuiteRecord> merged = current.ToList();

        foreach (TrialSuiteRecord original in baseline)
        {

            TrialSuiteRecord? latest = merged.FirstOrDefault(s => s.Id == original.Id);

            if (!desiredById.TryGetValue(original.Id, out TrialSuiteRecord? replacement))
            {

                if (latest is not null && !SuiteContentEquals(latest, original))
                {

                    throw new TheForgeStoreChangedException("trial-suites.json");

                }

                merged.RemoveAll(s => s.Id == original.Id);

                continue;

            }

            if (SuiteContentEquals(original, replacement))
            {

                continue;

            }

            if (latest is null || !SuiteContentEquals(latest, original))
            {

                throw new TheForgeStoreChangedException("trial-suites.json");

            }

            int index = merged.FindIndex(s => s.Id == original.Id);

            merged[index] = replacement;

        }

        foreach (TrialSuiteRecord addition in desired.Where(s => !baselineById.ContainsKey(s.Id)))
        {

            if (merged.Any(s => s.Id == addition.Id))
            {

                throw new TheForgeStoreChangedException("trial-suites.json");

            }

            merged.Add(addition);

        }

        return merged;

    }

    private static bool SuiteContentEquals(TrialSuiteRecord left, TrialSuiteRecord right) =>
        string.Equals(
            JsonSerializer.Serialize(left, TheForgeTrialSuitesJsonContext.Default.TrialSuiteRecord),
            JsonSerializer.Serialize(right, TheForgeTrialSuitesJsonContext.Default.TrialSuiteRecord),
            StringComparison.Ordinal);

}
