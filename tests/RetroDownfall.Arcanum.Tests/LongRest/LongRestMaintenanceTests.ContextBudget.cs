using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Admit-then-Prepare. The window the recipe was derived from is the window every request is admitted against, so a sweep over a
// small context window still completes, and a unit the model cannot take is refused before any provider-effect row or accounting run
// exists. A refusal that left a Prepared row would later stop a drained clean readmission for good.
public sealed partial class LongRestMaintenanceTests
{
    // The fixture's one provider serves both the chat and the embedding model.
    private static Action<ArcanumSettings> Window(int tokens) => settings => settings.Providers[0].ContextWindowLimit = tokens;

    private static string Lorem(int index, int bytes) => "Memory " + index + " " + string.Join(' ', Enumerable.Repeat("lorem ipsum dolor", bytes / 18));

    private static Task<long> UnsettledEffectsAsync(MaintenanceFixture f) => ScalarLongAsync(f, "SELECT count(*) FROM long_rest_provider_effects WHERE StateCode IN (1, 2, 3);");

    private static async Task<long> ScalarLongAsync(MaintenanceFixture f, string sql) => (long)(await f.H.ScalarAsync(sql))!;

    [Fact]
    public async Task A_default_window_sweep_over_sixteen_large_memories_completes_and_leaves_no_prepared_row()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, sourceCount: 16,
            configure: Window(8192), sourceText: index => Lorem(index, 2560));

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(0L, await UnsettledEffectsAsync(f));

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM InferenceRuns WHERE Status = 2;"));

        // The recipe carried the budget the 8192-token window allows (floor((8192 - 2048 - 16) * 100 / 115)).
        Assert.Equal(5328, LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!.Recipe.ProviderInputUtf8Bytes);

        Assert.All(f.Payloads.Where(static payload => payload.Kind != LongRestProviderPayloadKind.Embedding),
            payload => Assert.True(LongRestProviderCallFreezer.RenderChat(payload).IsSuccess));
    }

    [Fact]
    public async Task At_an_8192_window_every_paid_request_fits_the_window_and_no_effect_is_left_unsettled()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, sourceCount: 10,
            configure: Window(8192), sourceText: index => Lorem(index, 1200));

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        // Paid summary, embedding and adjudication calls all happened under the small window.
        Assert.Contains(f.Payloads, static payload => payload.Kind == LongRestProviderPayloadKind.Summary);

        Assert.Contains(f.Payloads, static payload => payload.Kind == LongRestProviderPayloadKind.Embedding);

        Assert.Contains(f.Payloads, static payload => payload.Kind == LongRestProviderPayloadKind.Adjudication && payload.Inputs.Length > 2);

        Assert.All(f.Payloads.Where(static payload => payload.Kind != LongRestProviderPayloadKind.Embedding), payload =>
        {
            LongRestChatRequest request = LongRestProviderCallFreezer.RenderChat(payload).Value;

            Assert.True(LongRestPromptLayout.RenderedBytes(request.SystemPrompt, request.UserPrompt, request.SchemaJson, request.SchemaName, request.SchemaDescription) <= 5328);

            Assert.Equal(5328, payload.InputBudgetBytes);
        });

        Assert.Equal(0L, await UnsettledEffectsAsync(f));

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM InferenceRuns WHERE Status = 2;"));

        // Every paid return is one billed operation: a fold's summary and embedding share one work unit's effect row.
        Assert.Equal(await ScalarLongAsync(f, "SELECT sum(PaidCount) FROM long_rest_provider_effects;"), await ScalarLongAsync(f, "SELECT count(*) FROM BillableOperations;"));
    }

    [Fact]
    public async Task A_memory_the_window_cannot_hold_ends_the_generation_for_an_operator_before_any_effect_row_exists()
    {
        // 5.4 KiB of content cannot sit in a summary request of the 5328-byte budget, alone or with anything else.
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false,
            configure: Window(8192), sourceText: index => index == 0 ? Lorem(0, 5400) : "Beta observation.");

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, progress.DeferredReason);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, parked.TerminalErrorCode);

        Assert.Equal(LongRestFaultClass.NeedsOperator, LongRestFaultPolicy.Classify(parked.TerminalErrorCode));

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM InferenceRuns;"));

        Assert.Equal(0, f.Handler.SendCount);

        // A new request for the scope is refused, naming the parked operation, instead of queueing behind it; the clean turn policy skips it.
        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(request.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.SweepParked, request.Error.Code);
    }

    [Fact]
    public async Task A_window_that_cannot_hold_the_smallest_recipe_is_refused_before_any_effect_row()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, configure: Window(8192));

        ArcanumSettings narrower = LongRestConfigurationEpochTests.Settings();

        narrower.Providers[0].ContextWindowLimit = LongRestPromptLayout.MinimumChatWindowTokens - 1;

        f.Options.Set(narrower);

        Assert.True(((ILongRestConfigurationProvider)f.Epoch).Capture().Recipe.IsFailure);

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(request.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.Unavailable, request.Error.Code);

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM InferenceRuns;"));

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task A_unit_the_local_window_gate_refuses_leaves_no_effect_row_so_a_drained_reopen_still_readmits_the_clean_sweep()
    {
        // The summary is a valid 7.9 KiB answer, but the shared estimator counts its embedding at more than the 8192-token window.
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, configure: Window(8192));

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = f.Handler.LastBody!.Contains("long_rest_adjudication", StringComparison.Ordinal)
                ? "{\"kind\":\"NoChange\",\"survivorVersionId\":null}" : "{\"summary\":\"" + new string('s', 7900) + "\"}";

            return Task.CompletedTask;
        };

        LongRestDiscoveryGeneration original = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        LongRestSweepProgress first = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, first.DeferredReason);

        // The summary was paid and kept; its embedding was refused locally, before any attempt, effect row or accounting run.
        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_provider_effects;"));

        Assert.Equal((long)LongRestProviderPayloadKind.Summary, await ScalarLongAsync(f, "SELECT PayloadKindCode FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await UnsettledEffectsAsync(f));

        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT count(*) FROM InferenceRuns;"));

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM InferenceRuns WHERE Status = 2;"));

        // A same-boot drained maintenance abort (a backup or a reset) reopens admission at a later generation.
        await AdditionalReopenEdgeAsync(f, drained: true);

        Assert.True(f.Admission.CurrentGeneration > original.Fence.AdmissionGeneration);

        // An operator retry returns the parked sweep to the worker, which readmits it past the drained interval.
        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.True(await f.Operations.ResetForRetryAsync(f.Operation, parked.Revision, DateTimeOffset.UtcNow));

        LongRunningOperationReconciler reconciler = new(f.Operations, [new LongRestSweepRecoveryHandler()], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        Assert.Equal(1, (await reconciler.ReconcileNowAsync("window-gate-retry")).RequiresAttention);

        Assert.Equal(LongRestParkCodes.ReadmitRequested, (await f.Operations.GetAsync(f.Operation))!.TerminalErrorCode);

        LongRestSweepProgress second = LongRestSweepHarness.Require(await ResumeAdmittedWithAsync(f.Coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

        // The clean readmission was not blocked by an unsettled effect: the generation adopted the current admission, and the paid
        // summary was reused rather than paid again.
        LongRestDiscoveryGeneration readmitted = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        Assert.Equal(f.Admission.CurrentGeneration, readmitted.Fence.AdmissionGeneration);

        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, second.DeferredReason);

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(0L, await UnsettledEffectsAsync(f));
    }
}
