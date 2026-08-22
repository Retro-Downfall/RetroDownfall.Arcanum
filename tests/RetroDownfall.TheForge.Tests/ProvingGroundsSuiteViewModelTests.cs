using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class ProvingGroundsSuiteViewModelTests
{

    [Fact]
    public async Task CreateSuite_AndAddDraft_PersistsItems()
    {

        InMemoryTrialSuiteStore store = new();

        ProvingGroundsViewModel vm = Create(store);

        vm.NewSuiteName = "Eval suite";

        await vm.CreateSuiteCommand.ExecuteAsync(null);

        Assert.Single(vm.Suites);

        vm.Target = "spell-a";

        vm.AddRegexInquisitorCommand.Execute(null);

        vm.SelectedInquisitor!.Pattern = "hello";

        await vm.AddDraftToSuiteCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SelectedSuite);

        Assert.Single(vm.SelectedSuite!.Trials);

        Assert.Equal("spell-a", vm.SelectedSuite.Trials[0].Trial.Target);

        vm.Dispose();

    }

    [Fact]
    public async Task RunSelectedSuite_RecordsResultsAndPassRate()
    {

        InMemoryTrialSuiteStore store = new();

        FakeSuiteTrialDataSource data = new()
        {
            RunResult = new(
                new TrialResult("t", TrialTargetKind.Spell, "s", true, "ok", [], 1, 1, null),
                true,
                null,
                null),
        };

        ProvingGroundsViewModel vm = Create(store, data);

        vm.NewSuiteName = "Suite";

        await vm.CreateSuiteCommand.ExecuteAsync(null);

        vm.Target = "s";

        vm.AddRegexInquisitorCommand.Execute(null);

        vm.SelectedInquisitor!.Pattern = ".";

        await vm.AddDraftToSuiteCommand.ExecuteAsync(null);

        await vm.RunSelectedSuiteCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SelectedSuite);

        Assert.NotEmpty(vm.SelectedSuiteRuns);

        Assert.Contains("Pass rate", vm.SuitePassRateSummary, StringComparison.Ordinal);

        Assert.Contains(ProvingGroundsViewModel.SensitiveHistoryWarning, vm.SuiteStatusText);

        vm.Dispose();

    }

    [Fact]
    public async Task RunSelectedSuite_KeepsTrialsAddedWhileTheRunIsInFlight()
    {

        InMemoryTrialSuiteStore store = new();

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeSuiteTrialDataSource data = new()
        {
            RunResult = new(
                new TrialResult("t", TrialTargetKind.Spell, "s", true, "ok", [], 1, 1, null),
                true,
                null,
                null),
            Gate = gate.Task,
        };

        ProvingGroundsViewModel vm = Create(store, data);

        vm.NewSuiteName = "Suite";

        await vm.CreateSuiteCommand.ExecuteAsync(null);

        vm.Target = "s";

        vm.AddRegexInquisitorCommand.Execute(null);

        vm.SelectedInquisitor!.Pattern = ".";

        await vm.AddDraftToSuiteCommand.ExecuteAsync(null);

        Task run = vm.RunSelectedSuiteCommand.ExecuteAsync(null)!;

        vm.Target = "s2";

        await vm.AddDraftToSuiteCommand.ExecuteAsync(null);

        gate.SetResult();

        await run;

        Assert.NotNull(vm.SelectedSuite);

        Assert.Equal(2, vm.SelectedSuite!.Trials.Count);

        Assert.NotEmpty(vm.SelectedSuiteRuns);

        vm.Dispose();

    }

    [Fact]
    public async Task RunSelectedSuite_DoesNotRestoreRunHistoryClearedWhileTheRunIsInFlight()
    {

        InMemoryTrialSuiteStore store = new();

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeSuiteTrialDataSource data = new()
        {
            RunResult = new(
                new TrialResult("t", TrialTargetKind.Spell, "s", true, "ok", [], 1, 1, null),
                true,
                null,
                null),
        };

        ProvingGroundsViewModel vm = Create(store, data, new ConfirmingDialogService());

        vm.NewSuiteName = "Suite";

        await vm.CreateSuiteCommand.ExecuteAsync(null);

        vm.Target = "s";

        vm.AddRegexInquisitorCommand.Execute(null);

        vm.SelectedInquisitor!.Pattern = ".";

        await vm.AddDraftToSuiteCommand.ExecuteAsync(null);

        await vm.RunSelectedSuiteCommand.ExecuteAsync(null);

        Assert.Single(vm.SelectedSuiteRuns);

        data.Gate = gate.Task;

        Task run = vm.RunSelectedSuiteCommand.ExecuteAsync(null)!;

        await vm.ClearSuiteRunHistoryCommand.ExecuteAsync(null);

        Assert.Empty(vm.SelectedSuiteRuns);

        gate.SetResult();

        await run;

        Assert.NotNull(vm.SelectedSuite);

        Assert.Single(vm.SelectedSuite!.Runs);

        vm.Dispose();

    }

    [Fact]
    public async Task RunSelectedSuite_ReportsWhenTheSuiteWasDeletedDuringTheRun()
    {

        InMemoryTrialSuiteStore store = new();

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeSuiteTrialDataSource data = new()
        {
            RunResult = new(
                new TrialResult("t", TrialTargetKind.Spell, "s", true, "ok", [], 1, 1, null),
                true,
                null,
                null),
            Gate = gate.Task,
        };

        ProvingGroundsViewModel vm = Create(store, data, new ConfirmingDialogService());

        vm.NewSuiteName = "Suite";

        await vm.CreateSuiteCommand.ExecuteAsync(null);

        vm.Target = "s";

        vm.AddRegexInquisitorCommand.Execute(null);

        vm.SelectedInquisitor!.Pattern = ".";

        await vm.AddDraftToSuiteCommand.ExecuteAsync(null);

        Task run = vm.RunSelectedSuiteCommand.ExecuteAsync(null)!;

        await vm.DeleteSuiteCommand.ExecuteAsync(null);

        gate.SetResult();

        await run;

        Assert.Empty(vm.Suites);

        Assert.Contains("deleted", vm.StatusText, StringComparison.OrdinalIgnoreCase);

        vm.Dispose();

    }

    [Fact]
    public async Task ExportSuite_WhenTheWriteFails_ReportsInsteadOfThrowing()
    {

        InMemoryTrialSuiteStore store = new();

        FakeWhispersService whispers = new();

        ProvingGroundsViewModel vm = Create(
            store,
            whispers: whispers,
            fileDialog: new UnwritablePathFileDialogService());

        vm.NewSuiteName = "Suite";

        await vm.CreateSuiteCommand.ExecuteAsync(null);

        whispers.Calls.Clear();

        await vm.ExportSuiteCommand.ExecuteAsync(null);

        Assert.Contains(whispers.Calls, static call => call.Severity == WhisperSeverity.Error);

        Assert.DoesNotContain(whispers.Calls, static call => call.Severity == WhisperSeverity.Success);

        vm.Dispose();

    }

    private static ProvingGroundsViewModel Create(
        InMemoryTrialSuiteStore store,
        FakeSuiteTrialDataSource? data = null,
        IConfirmationDialogService? confirmationDialog = null,
        FakeWhispersService? whispers = null,
        IArtifactFileDialogService? fileDialog = null) =>
        new(
            data ?? new FakeSuiteTrialDataSource(),
            new FoundryFloorViewModel(new NullLogService()),
            whispers ?? new FakeWhispersService(),
            confirmationDialog ?? new NullConfirmationDialogService(),
            store,
            fileDialog ?? new NullArtifactFileDialogService(),
            ImmediateTheForgeLocalMutationRunner.Instance);

    /// <summary>
    /// Hands back a path under a directory that does not exist, so the write throws the way a
    /// full volume, a read-only share, or an unmounted drive would.
    /// </summary>
    private sealed class UnwritablePathFileDialogService : IArtifactFileDialogService
    {

        private static string UnwritablePath(string suggestedFileName) =>
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no-such-directory", suggestedFileName);

        public Task<string?> PickSaveJsonPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(UnwritablePath(suggestedFileName));

        public Task<string?> PickOpenJsonPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveCsvPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(UnwritablePath(suggestedFileName));

        public Task<string?> PickOpenAnyPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveAnyPathAsync(string suggestedFileName, string? defaultExtension, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(UnwritablePath(suggestedFileName));

    }

    private sealed class ConfirmingDialogService : IConfirmationDialogService
    {

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken, bool confirmIsDefault = true) =>
            Task.FromResult(true);

    }

    private sealed class FakeSuiteTrialDataSource : ITrialDataSource
    {

        public DataSourceResult<TrialResult> RunResult { get; init; } =
            new(null, true, null, null);

        public Task? Gate { get; set; }

        public async Task<DataSourceResult<TrialResult>> RunAsync(Trial trial, CancellationToken cancellationToken)
        {

            if (Gate is not null)
            {

                await Gate.ConfigureAwait(false);

            }

            return RunResult;

        }

        public Task<DataSourceResult<IReadOnlyList<string>>> ListSpellNamesAsync(
            string? workspace,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<IReadOnlyList<string>>([], true, null, null));

        public Task<DataSourceResult<IReadOnlyList<PromptSummaryDto>>> ListPromptsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<IReadOnlyList<PromptSummaryDto>>([], true, null, null));

    }

}
