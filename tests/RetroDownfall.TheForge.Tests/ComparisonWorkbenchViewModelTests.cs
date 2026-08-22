using System.Runtime.CompilerServices;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Models.Comparisons;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class ComparisonWorkbenchViewModelTests
{

    [Fact]
    public async Task Run_CapturesTokensCostAndDiff()
    {

        FakeComparisonDataSource dataSource = new()
        {
            Events =
            [
                new IntelligenceEvent(IntelligenceEventType.Token, "", "Hello"),
                new IntelligenceEvent(
                    IntelligenceEventType.Result,
                    "done",
                    Usage: new ChatCompletionUsage(10, 5, 15, 2),
                    FinishReason: "stop"),
            ],
            Pricing = new PricingSettings
            {
                DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
            },
        };

        ComparisonWorkbenchViewModel vm = Create(dataSource);

        vm.SharedInput = "ping";

        vm.Variants[0].Model = "m1";

        vm.Variants[1].Model = "m2";

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Results.Count);

        Assert.Equal("Hello", vm.Results[0].Output);

        Assert.Equal(10, vm.Results[0].PromptTokens);

        Assert.Equal(2, vm.Results[0].CachedTokens);

        Assert.Equal("stop", vm.Results[0].FinishReason);

        Assert.StartsWith("estimated", vm.Results[0].CostLabel, StringComparison.Ordinal);

        Assert.NotEmpty(vm.History);

        Assert.True(vm.Trace.Entries.Count > 0);

        vm.Dispose();

    }

    [Fact]
    public async Task Run_Cancel_StopsMidway()
    {

        FakeComparisonDataSource dataSource = new() { Stall = true };

        ComparisonWorkbenchViewModel vm = Create(dataSource);

        vm.SharedInput = "x";

        Task run = vm.RunCommand.ExecuteAsync(null)!;

        await Task.Delay(50);

        vm.CancelCommand.Execute(null);

        await run;

        Assert.Equal("Comparison cancelled.", vm.StatusText);

        vm.Dispose();

    }

    [Fact]
    public async Task Run_CancelDuringPricingFetch_ResetsBusyAndReportsCancellation()
    {

        FakeComparisonDataSource dataSource = new() { StallPricing = true };

        ComparisonWorkbenchViewModel vm = Create(dataSource);

        vm.SharedInput = "x";

        Task run = vm.RunCommand.ExecuteAsync(null)!;

        await Task.Delay(50);

        vm.CancelCommand.Execute(null);

        await run;

        Assert.False(vm.IsBusy);

        Assert.Equal("Comparison cancelled.", vm.StatusText);

        vm.Dispose();

    }

    [Fact]
    public async Task ExportCsv_WhenTheWriteFails_ReportsInsteadOfThrowing()
    {

        FakeWhispersService whispers = new();

        ComparisonWorkbenchViewModel vm = Create(
            new FakeComparisonDataSource(),
            new UnwritablePathFileDialogService(),
            whispers);

        await vm.ExportCsvCommand.ExecuteAsync(null);

        Assert.NotNull(vm.LastError);

        Assert.NotEqual("Comparison exported (CSV).", vm.StatusText);

        Assert.Contains(whispers.Calls, static call => call.Severity == WhisperSeverity.Error);

        vm.Dispose();

    }

    [Fact]
    public async Task ExportMarkdown_WhenTheWriteFails_ReportsInsteadOfThrowing()
    {

        FakeWhispersService whispers = new();

        ComparisonWorkbenchViewModel vm = Create(
            new FakeComparisonDataSource(),
            new UnwritablePathFileDialogService(),
            whispers);

        await vm.ExportMarkdownCommand.ExecuteAsync(null);

        Assert.NotNull(vm.LastError);

        Assert.NotEqual("Comparison exported (Markdown).", vm.StatusText);

        Assert.Contains(whispers.Calls, static call => call.Severity == WhisperSeverity.Error);

        vm.Dispose();

    }

    [Fact]
    public async Task ExportJson_WhenTheWriteFails_ReportsInsteadOfThrowing()
    {

        FakeComparisonDataSource dataSource = new()
        {
            Events =
            [
                new IntelligenceEvent(IntelligenceEventType.Result, "done", FinishReason: "stop"),
            ],
        };

        FakeWhispersService whispers = new();

        ComparisonWorkbenchViewModel vm = Create(
            dataSource,
            new UnwritablePathFileDialogService(),
            whispers);

        vm.SharedInput = "ping";

        await vm.RunCommand.ExecuteAsync(null);

        await vm.ExportJsonCommand.ExecuteAsync(null);

        Assert.NotNull(vm.LastError);

        Assert.NotEqual("Comparison exported (JSON).", vm.StatusText);

        Assert.Contains(whispers.Calls, static call => call.Severity == WhisperSeverity.Error);

        vm.Dispose();

    }

    [Fact]
    public async Task ExportJson_SnapshotsCurrentInputAfterMutationAdmission()
    {

        string path = Path.Combine(
            Path.GetTempPath(),
            $"comparison-admitted-export-{Guid.NewGuid():N}.json");

        ComparisonWorkbenchViewModel? vm = null;

        try
        {

            vm = Create(
                new FakeComparisonDataSource(),
                new FixedPathFileDialogService(path),
                mutationRunner: new BeforeMutationTheForgeLocalMutationRunner(
                    () => vm!.SharedInput = "after-admission"));

            vm.SharedInput = "before-admission";

            vm.Results.Add(new ComparisonVariantResultViewModel
            {
                VariantId = Guid.NewGuid(),
                Label = "one",
                Output = "answer",
            });

            await vm.ExportJsonCommand.ExecuteAsync(null);

            ComparisonRunRecord? written = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(path),
                TheForgeComparisonsJsonContext.Default.ComparisonRunRecord);

            Assert.Equal("after-admission", written!.InputPreview);

        }
        finally
        {

            vm?.Dispose();

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }

    }

    private static ComparisonWorkbenchViewModel Create(
        IComparisonWorkbenchDataSource dataSource,
        IArtifactFileDialogService? fileDialog = null,
        FakeWhispersService? whispers = null,
        ITheForgeLocalMutationRunner? mutationRunner = null) =>
        new(
            dataSource,
            new InMemoryComparisonRunStore(),
            new FoundryFloorViewModel(new NullLogService()),
            whispers ?? new FakeWhispersService(),
            new NullConfirmationDialogService(),
            fileDialog ?? new NullArtifactFileDialogService(),
            new NavigationService(),
            mutationRunner ?? ImmediateTheForgeLocalMutationRunner.Instance,
            new InMemoryInferenceTraceStore());

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

    private sealed class FixedPathFileDialogService(string path) : IArtifactFileDialogService
    {

        public Task<string?> PickSaveJsonPathAsync(
            string suggestedFileName,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(path);

        public Task<string?> PickOpenJsonPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveCsvPathAsync(
            string suggestedFileName,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(path);

        public Task<string?> PickOpenAnyPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveAnyPathAsync(
            string suggestedFileName,
            string? defaultExtension,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(path);

    }

    private sealed class FakeComparisonDataSource : IComparisonWorkbenchDataSource
    {

        public IReadOnlyList<IntelligenceEvent> Events { get; init; } = [];

        public PricingSettings? Pricing { get; init; }

        public bool Stall { get; init; }

        public bool StallPricing { get; init; }

        public async IAsyncEnumerable<IntelligenceEvent> RunFreePromptAsync(
            string prompt,
            string? model,
            float? temperature,
            float? topP,
            int? maxOutputTokens,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {

            if (Stall)
            {

                yield return new IntelligenceEvent(IntelligenceEventType.Token, "", "x");

                await Task.Delay(Timeout.Infinite, cancellationToken);

                yield break;

            }

            foreach (IntelligenceEvent ev in Events)
            {

                yield return ev;

                await Task.Yield();

            }

        }

        public IAsyncEnumerable<IntelligenceEvent> RunPromptAsync(
            Guid promptId,
            PromptExecuteRequest request,
            CancellationToken cancellationToken) =>
            RunFreePromptAsync(request.UserMessage, request.Model, request.Temperature, request.TopP, request.MaxOutputTokens, cancellationToken);

        public IAsyncEnumerable<IntelligenceEvent> RunSpellAsync(
            string spellName,
            SpellExecuteRequest request,
            CancellationToken cancellationToken) =>
            RunFreePromptAsync(request.Prompt, request.Model, request.Temperature, request.TopP, request.MaxOutputTokens, cancellationToken);

        public async Task<PricingSettings?> GetPricingAsync(CancellationToken cancellationToken)
        {

            if (StallPricing)
            {

                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            }

            return Pricing;

        }

    }

}
