using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
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

    private static ProvingGroundsViewModel Create(
        InMemoryTrialSuiteStore store,
        FakeSuiteTrialDataSource? data = null) =>
        new(
            data ?? new FakeSuiteTrialDataSource(),
            new FoundryFloorViewModel(new NullLogService()),
            new FakeWhispersService(),
            new NullConfirmationDialogService(),
            store,
            new NullArtifactFileDialogService());

    private sealed class FakeSuiteTrialDataSource : ITrialDataSource
    {

        public DataSourceResult<TrialResult> RunResult { get; init; } =
            new(null, true, null, null);

        public Task<DataSourceResult<TrialResult>> RunAsync(Trial trial, CancellationToken cancellationToken) =>
            Task.FromResult(RunResult);

        public Task<DataSourceResult<IReadOnlyList<string>>> ListSpellNamesAsync(
            string? workspace,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<IReadOnlyList<string>>([], true, null, null));

        public Task<DataSourceResult<IReadOnlyList<PromptSummaryDto>>> ListPromptsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<IReadOnlyList<PromptSummaryDto>>([], true, null, null));

    }

}
