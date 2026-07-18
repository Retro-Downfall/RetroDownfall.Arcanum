using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Services;
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

    private static ComparisonWorkbenchViewModel Create(IComparisonWorkbenchDataSource dataSource) =>
        new(
            dataSource,
            new InMemoryComparisonRunStore(),
            new FoundryFloorViewModel(new NullLogService()),
            new FakeWhispersService(),
            new NullConfirmationDialogService(),
            new NullArtifactFileDialogService(),
            new NavigationService(),
            new InMemoryInferenceTraceStore());

    private sealed class FakeComparisonDataSource : IComparisonWorkbenchDataSource
    {

        public IReadOnlyList<IntelligenceEvent> Events { get; init; } = [];

        public PricingSettings? Pricing { get; init; }

        public bool Stall { get; init; }

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

        public Task<PricingSettings?> GetPricingAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Pricing);

    }

}
