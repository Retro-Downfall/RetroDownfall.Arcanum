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
using RetroDownfall.Arcanum.Tests.Support;

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

    // At the default window the request budget is 5328 bytes, so no two 2.5 KiB memories fit one request: no summary fold and no
    // adjudication pack can exist, and the sweep makes no request at all. That is the honest outcome of this corpus, not a stall: the sweep
    // completes with nothing left prepared, every neighbour it could not pack is counted, and nothing was sent. (A larger window, or smaller
    // memories, is what lets it consolidate; the sweep over 1.2 KiB memories below does.)
    [Fact]
    public async Task A_default_window_sweep_over_sixteen_large_memories_completes_with_no_request_and_counts_every_neighbour_it_could_not_pack()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, sourceCount: 16,
            configure: Window(8192), sourceText: index => Lorem(index, 2560));

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        using AsyncFlowMetricCapture metrics = new("arcanum.long_rest.sweep.skipped_candidates", "arcanum.long_rest.hierarchy.oversize_children");

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(0L, await UnsettledEffectsAsync(f));

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM InferenceRuns;"));

        // The recipe carried the budget the 8192-token window allows (floor((8192 - 2048 - 16) * 100 / 115)).
        Assert.Equal(5328, LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!.Recipe.ProviderInputUtf8Bytes);

        // Nothing was asked of the model: no request, no summary, no adjudication, no effect row, no accounting run.
        Assert.Equal(0, f.Handler.SendCount);

        Assert.Empty(f.Payloads);

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer > 0;"));

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_provider_effects;"));

        // Sixteen anchors, each with the eight ranked neighbours the lexical and vector lanes gave it, none of which fits beside it.
        Assert.Equal(16L * 8, metrics.Sum("arcanum.long_rest.sweep.skipped_candidates", "reason", "oversize"));

        Assert.Equal(0, metrics.Sum("arcanum.long_rest.sweep.skipped_candidates", "reason", "pack_limit"));

        // Each memory fits a request alone, so none is an oversize hierarchy child.
        Assert.Equal(0, metrics.Sum("arcanum.long_rest.hierarchy.oversize_children"));
    }

    [Fact]
    public async Task At_an_8192_window_every_paid_request_fits_the_window_and_no_effect_is_left_unsettled()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, sourceCount: 10,
            configure: Window(8192), sourceText: index => Lorem(index, 800));

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
    public async Task Exact_duplicates_too_large_for_a_request_are_still_consolidated_locally_at_a_small_window()
    {
        // Two identical 3 KiB memories: a request holding both would be about 7 KiB, over the 5328-byte budget, but exact duplicates are
        // committed without any request, so the window does not decide them.
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, exact: true,
            configure: Window(8192), sourceText: _ => Lorem(7, 3000));

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_receipts WHERE OutcomeCode = 1;"));

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_provider_effects;"));
    }

    // A memory no request can hold is never summarized and never folded: it is carried into the forest on its own, once, and the sweep goes
    // on without it. It used to end the generation for an operator, after the summaries around it had been paid for.
    [Fact]
    public async Task A_memory_the_window_cannot_hold_is_carried_unsummarized_and_the_sweep_still_completes_without_sending_its_text()
    {
        // 5.4 KiB of content cannot sit in a summary request of the 5328-byte budget, alone or with anything else.
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, sourceCount: 6,
            configure: Window(8192), sourceText: index => index == 0 ? Lorem(0, 5400) : $"Beta observation number {index}.");

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        using AsyncFlowMetricCapture metrics = new("arcanum.long_rest.hierarchy.oversize_children");

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        // The other five memories were folded and paid for; the big one was not, and its text never left the process.
        Assert.Contains(f.Payloads, static payload => payload.Kind == LongRestProviderPayloadKind.Summary);

        Assert.DoesNotContain(f.Payloads.Where(static payload => payload.Kind != LongRestProviderPayloadKind.Embedding),
            static payload => LongRestProviderCallFreezer.RenderChat(payload).Value.UserPrompt.Contains("lorem ipsum dolor", StringComparison.Ordinal));

        // It is a captured leaf and a node of the forest that covers each leaf once, and it has no parent: it was carried, not summarized.
        string big = (string)(await f.H.ScalarAsync("SELECT Id FROM saga_memories WHERE Content LIKE 'Memory 0 %';"))!;

        Assert.Equal(1L, await ScalarLongAsync(f, $"SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 0 AND MemoryId = '{big}';"));

        Assert.Equal(0L, await ScalarLongAsync(f, $"SELECT count(*) FROM long_rest_discovery_edges edge JOIN long_rest_discovery_nodes node ON node.NodeId = edge.ChildNodeId WHERE node.MemoryId = '{big}';"));

        Assert.True(await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer > 0;") >= 1);

        // Counted once, by what it is and never by whose it is.
        Assert.Equal(1, metrics.Sum("arcanum.long_rest.hierarchy.oversize_children"));

        Assert.All(metrics.Measurements, static measurement => Assert.Empty(measurement.Tags));

        Assert.Equal(0L, await UnsettledEffectsAsync(f));
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

    // The residual the design states rather than closes: the window cannot be checked before the model answers. A summary refused at its embedding
    // stays accepted within its generation, but the only remedy is a wider window, which is a configuration change and so a new generation, and
    // the new generation pays for the summary again.
    [Fact]
    public async Task Widening_the_window_after_a_refused_embedding_starts_a_new_generation_that_pays_for_the_summary_again()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, configure: Window(8192));

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"" + new string('s', 7900) + "\"}";

            return Task.CompletedTask;
        };

        LongRestSweepProgress first = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(ErrorCodes.LongRest.ModelContextTooSmall, first.DeferredReason);

        Assert.Equal(1, f.Payloads.Count(static payload => payload.Kind == LongRestProviderPayloadKind.Summary));

        ArcanumSettings wider = LongRestConfigurationEpochTests.Settings();

        wider.Providers[0].ContextWindowLimit = 16384;

        f.Options.Set(wider);

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(request.IsSuccess);

        Assert.NotEqual(f.Operation, request.Value.Id);

        LongRestSweepProgress second = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(request.Value.Id, CancellationToken.None));

        Assert.True(second.Phase == LongRestSweepPhase.Completed, second.DeferredReason);

        Assert.Equal(2, f.Payloads.Count(static payload => payload.Kind == LongRestProviderPayloadKind.Summary));
    }

    [Fact]
    public async Task A_unit_the_local_window_gate_refuses_leaves_no_effect_row_so_a_drained_reopen_still_readmits_the_clean_sweep()
    {
        // The summary is a valid 7.9 KiB answer, but the shared estimator counts its embedding at more than the 8192-token window.
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, configure: Window(8192));

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"" + new string('s', 7900) + "\"}";

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
