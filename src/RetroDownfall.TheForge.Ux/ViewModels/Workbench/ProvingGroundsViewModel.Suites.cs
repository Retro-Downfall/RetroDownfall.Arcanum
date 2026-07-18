using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
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

        await PersistSuitesAsync(suites, cancellationToken).ConfigureAwait(true);

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

        await PersistSuitesAsync(suites, cancellationToken).ConfigureAwait(true);

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

        await PersistSuitesAsync(suites, cancellationToken).ConfigureAwait(true);

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

        await PersistSuitesAsync(suites, cancellationToken).ConfigureAwait(true);

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

        await ArtifactImportExportHelper
            .WriteJsonAsync(path, SelectedSuite, TheForgeTrialSuitesJsonContext.Default.TrialSuiteRecord, cancellationToken)
            .ConfigureAwait(true);

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

        await PersistSuitesAsync(suites, cancellationToken).ConfigureAwait(true);

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

        try
        {

            foreach (TrialSuiteItemRecord item in items)
            {

                runToken.ThrowIfCancellationRequested();

                Stopwatch itemWatch = Stopwatch.StartNew();

                DataSourceResult<TrialResult> outcome = await _dataSource.RunAsync(item.Trial, runToken).ConfigureAwait(true);

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
                $"items={items.Count}; elapsedMs={suiteWatch.ElapsedMilliseconds}",
                results);

            List<TrialSuiteRunRecord> runs = suite.Runs.Prepend(run).ToList();

            TrialSuiteRecord updated = suite with
            {
                Runs = runs,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            List<TrialSuiteRecord> suites = _suiteDocument.Suites.Select(s => s.Id == updated.Id ? updated : s).ToList();

            await PersistSuitesAsync(suites, cancellationToken).ConfigureAwait(true);

            SelectedSuite = Suites.FirstOrDefault(s => s.Id == updated.Id);

            SelectedSuiteRun = SelectedSuiteRuns.FirstOrDefault(r => r.Id == run.Id);

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
        finally
        {

            IsBusy = false;

        }

    }

    private async Task PersistSuitesAsync(IReadOnlyList<TrialSuiteRecord> suites, CancellationToken cancellationToken)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        TrialSuiteStoreDocument document = new(
            TrialSuiteStore.CurrentSchemaVersion,
            _suiteDocument.CreatedAt == default ? now : _suiteDocument.CreatedAt,
            now,
            suites);

        await _suiteStore.SaveAsync(document, cancellationToken).ConfigureAwait(true);

        _suiteDocument = await _suiteStore.LoadAsync(cancellationToken).ConfigureAwait(true);

        Suites.Clear();

        foreach (TrialSuiteRecord suite in _suiteDocument.Suites.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase))
        {

            Suites.Add(suite);

        }

        OnPropertyChanged(nameof(SuitePassRateSummary));

    }

}
