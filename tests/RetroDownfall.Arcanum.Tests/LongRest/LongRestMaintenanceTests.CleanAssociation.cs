using Microsoft.Data.Sqlite;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData("unchanged", true)]
    [InlineData("unit", false)]
    [InlineData("artifact", false)]
    public async Task Clean_reopen_stored_hierarchy_association_survives_later_unsafe_abort_but_refuses_tampering(string change, bool valid)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        PaidReopenStage retained = await RetainPaidSummaryAcrossClosingAsync(f, drained: true);

        f.Payloads.Clear();

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        RecoveredSweep recovered = await RecoverCleanAssociationAsync(f);

        Assert.Equal(1, recovered.Completed);

        Assert.Equal(0, recovered.RequiresAttention);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.DoesNotContain(f.Payloads, payload => payload.Kind == LongRestProviderPayloadKind.Summary);

        Assert.Equal(retained.Evidence, await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"));

        Assert.Equal(retained.WorkDigest, await f.H.ScalarAsync("SELECT hex(WorkDigest) FROM long_rest_accepted_summaries;"));

        LongRestDiscoveryGeneration generation = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        LongRestTarget anchor = await f.H.TargetAsync(f.Source);

        Result<LongRestCandidateGroup[]> before = await f.H.Store.DiscoverAsync(generation.GenerationId, anchor, null, CancellationToken.None);

        Assert.True(before.IsSuccess);

        Assert.NotEmpty(before.Value);

        int sends = f.Handler.SendCount;

        // No worker or continuation proof is retained by this ordinary read. A later unsafe edge
        // invalidates new execution lineage, while the already committed association stays readable.
        await AdditionalReopenEdgeAsync(f, drained: false);

        Assert.True(f.Admission.CurrentGeneration > generation.Fence.AdmissionGeneration);

        Assert.Equal(generation, LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None)));

        if (change == "unit")
        {
            await f.H.ExecuteAsync("UPDATE long_rest_work_items SET WorkDigest = zeroblob(32) WHERE KindCode = 2;");
        }
        else if (change == "artifact")
        {
            await CorruptCleanSummaryDigestAsync(f);
        }

        Result<LongRestCandidateGroup[]> after = await f.H.Store.DiscoverAsync(generation.GenerationId, anchor, null, CancellationToken.None);

        Assert.Equal(valid, after.IsSuccess);

        if (valid)
        {
            Assert.Equal(before.Value.Select(group => string.Join(",", group.Targets.Select(target => target.MemoryId))),
                after.Value.Select(group => string.Join(",", group.Targets.Select(target => target.MemoryId))));
        }

        Assert.Equal(sends, f.Handler.SendCount);

        Assert.Equal(retained.Evidence, await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"));
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("artifact")]
    public async Task Clean_reopen_reassociation_refuses_corrupt_stored_ownership_before_any_adoption(string change)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        PaidReopenStage retained = await RetainPaidSummaryAcrossClosingAsync(f, drained: true);

        if (change == "unit")
        {
            await f.H.ExecuteAsync("UPDATE long_rest_work_items SET WorkDigest = zeroblob(32) WHERE KindCode = 2;");
        }
        else
        {
            await CorruptCleanSummaryDigestAsync(f);
        }

        object? generation = await f.H.ScalarAsync(CleanGenerationSnapshotSql);

        object? unit = await f.H.ScalarAsync(CleanUnitSnapshotSql);

        object? effect = await f.H.ScalarAsync(CleanEffectSnapshotSql);

        RecoveredSweep recovered = await RecoverCleanAssociationAsync(f);

        Assert.Equal(0, recovered.Completed);

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(generation, await f.H.ScalarAsync(CleanGenerationSnapshotSql));

        Assert.Equal(unit, await f.H.ScalarAsync(CleanUnitSnapshotSql));

        Assert.Equal(effect, await f.H.ScalarAsync(CleanEffectSnapshotSql));

        Assert.Equal(retained.Evidence, await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));
    }

    [Fact]
    public async Task Clean_reopen_unknown_embedding_Sent_preserves_exact_transport_and_paid_summary_without_adoption()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        using CancellationTokenSource stopping = new();

        using LongRestSweepHostedService host = PaidOwnershipHost(f);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        f.Handler.InsideSendWithCancellation = async token =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            if (f.Handler.SendCount != 2) return;

            entered.TrySetResult();

            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        Task<Exception?> run = Record.ExceptionAsync(() => host.RunOneAsync(f.Operation, stopping.Token));

        IGrimoireClosingOwner? closing = null;

        bool reopened = false;

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

            Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 3 AND PayloadKindCode = 3 AND CompletionJson IS NULL;"));

            closing = f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()).Value;

            Task<Result> drain = f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            Assert.False(drain.IsCompleted);

            await stopping.CancelAsync();

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(10))) is null or OperationCanceledException);

            Assert.True((await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

            reopened = true;
        }
        finally
        {
            await stopping.CancelAsync();

            await run.WaitAsync(TimeSpan.FromSeconds(10));

            if (closing is not null)
            {
                if (!reopened) await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None);

                await closing.DisposeAsync();
            }
        }

        Assert.Equal(LongRunningOperationState.Running, (await f.Operations.GetAsync(f.Operation))!.State);

        object? generation = await f.H.ScalarAsync(CleanGenerationSnapshotSql);

        object? unit = await f.H.ScalarAsync(CleanUnitSnapshotSql);

        object? effect = await f.H.ScalarAsync(CleanEffectSnapshotSql);

        object? summary = await f.H.ScalarAsync(CleanSummarySnapshotSql);

        object? bills = await f.H.ScalarAsync(CleanBillingSnapshotSql);

        f.Handler.InsideSendWithCancellation = null;

        RecoveredSweep recovered = await RecoverCleanAssociationAsync(f);

        Assert.Equal(0, recovered.Completed);

        Assert.Equal(2, f.Handler.SendCount);

        Assert.Equal(generation, await f.H.ScalarAsync(CleanGenerationSnapshotSql));

        Assert.Equal(unit, await f.H.ScalarAsync(CleanUnitSnapshotSql));

        Assert.Equal(effect, await f.H.ScalarAsync(CleanEffectSnapshotSql));

        Assert.Equal(summary, await f.H.ScalarAsync(CleanSummarySnapshotSql));

        Assert.Equal(bills, await f.H.ScalarAsync(CleanBillingSnapshotSql));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));
    }

    private static async Task CorruptCleanSummaryDigestAsync(MaintenanceFixture f)
    {
        const string mutation = "UPDATE long_rest_accepted_summaries SET WorkDigest = zeroblob(32);";

        string definition = (string)(await f.H.ScalarAsync("SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'long_rest_accepted_summaries_guard_update';"))!;

        Assert.Equal(CleanSummaryGuardSql.Replace(" IF NOT EXISTS", string.Empty, StringComparison.Ordinal).Trim().TrimEnd(';'),
            definition.Replace(" IF NOT EXISTS", string.Empty, StringComparison.Ordinal).Trim().TrimEnd(';'));

        object? before = await f.H.ScalarAsync(CleanSummarySnapshotSql);

        SqliteException guarded = await Assert.ThrowsAsync<SqliteException>(() => f.H.ExecuteAsync(mutation));

        Assert.Equal(19, guarded.SqliteErrorCode);

        Assert.Equal(before, await f.H.ScalarAsync(CleanSummarySnapshotSql));

        // Deliberate fixture corruption, with the exact canonical guard restored before the behavior under test.
        await f.H.ExecuteAsync("DROP TRIGGER long_rest_accepted_summaries_guard_update;");

        try
        {
            await f.H.ExecuteAsync(mutation);
        }
        finally
        {
            await f.H.ExecuteAsync(definition);
        }

        Assert.Equal(definition, await f.H.ScalarAsync("SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'long_rest_accepted_summaries_guard_update';"));

        object? corrupted = await f.H.ScalarAsync(CleanSummarySnapshotSql);

        Assert.NotEqual(before, corrupted);

        guarded = await Assert.ThrowsAsync<SqliteException>(() => f.H.ExecuteAsync(mutation));

        Assert.Equal(19, guarded.SqliteErrorCode);

        Assert.Equal(corrupted, await f.H.ScalarAsync(CleanSummarySnapshotSql));
    }

    private const string CleanSummaryGuardSql = """
        CREATE TRIGGER IF NOT EXISTS long_rest_accepted_summaries_guard_update
        BEFORE UPDATE ON long_rest_accepted_summaries
        BEGIN
            SELECT RAISE(ABORT, 'Long Rest accepted evidence is immutable.');
        END;
        """;

    private static Task<RecoveredSweep> RecoverCleanAssociationAsync(MaintenanceFixture f) =>
        RecoverThenReadmitAsync(f, "clean-association-recovery");

    private const string CleanEffectSnapshotSql = """
        SELECT json_group_array(json(Row)) FROM (
            SELECT json_array(effect.WorkItemId, hex(effect.WorkDigest), effect.OriginalWorkJson, effect.AttemptId, effect.PayloadKindCode, effect.PhysicalOrdinal,
                effect.Revision, effect.StateCode, hex(effect.PayloadDigest), hex(effect.DestinationDigest), hex(effect.DisclosureReceiptDigest),
                effect.SentCount, effect.PaidCount, effect.InvalidCount, effect.FailureCount, effect.InputTokens, effect.OutputTokens,
                effect.TotalTokens, effect.CachedInputTokens, effect.ReasoningTokens, effect.CostUsd, effect.CompletionJson, effect.FailureCode,
                effect.UpdatedAtUtc, unit.OperationId, unit.GenerationId, unit.KindCode, unit.ItemId, hex(unit.SourceDigest), hex(unit.RecipeDigest),
                generation.RecipeJson) AS Row
            FROM long_rest_provider_effects effect
            LEFT JOIN long_rest_work_items unit ON unit.WorkItemId = effect.WorkItemId
            LEFT JOIN long_rest_discovery_generations generation ON generation.GenerationId = unit.GenerationId
            ORDER BY effect.WorkItemId);
        """;

    private const string CleanGenerationSnapshotSql = """
        SELECT json_group_array(json(Row)) FROM (
            SELECT json_array(GenerationId, OperationId, ScopeKindCode, CampaignId, hex(PartitionDigest), InsertionHighWatermark,
                FenceJson, RecipeJson, hex(RecipeDigest), PhaseCode, Revision, CaptureCursor, FoldLayer, FoldGroup, Published, CreatedAtUtc) AS Row
            FROM long_rest_discovery_generations ORDER BY GenerationId);
        """;

    private const string CleanUnitSnapshotSql = """
        SELECT json_group_array(json(Row)) FROM (
            SELECT json_array(WorkItemId, hex(WorkDigest), GenerationId, OperationId, KindCode, ItemId, hex(SourceDigest), hex(RecipeDigest),
                Revision, DispositionCode, ReceiptId) AS Row FROM long_rest_work_items ORDER BY WorkItemId);
        """;

    private const string CleanSummarySnapshotSql = """
        SELECT json_group_array(json(Row)) FROM (
            SELECT json_array(WorkItemId, hex(WorkDigest), GenerationId, NodeId, hex(MembershipDigest), hex(SourceDigest), hex(RecipeDigest),
                hex(PayloadDigest), Summary, hex(SummaryDigest), EvidenceJson, CreatedAtUtc) AS Row
            FROM long_rest_accepted_summaries ORDER BY WorkItemId);
        """;

    private const string CleanBillingSnapshotSql = """
        SELECT json_group_array(json(Row)) FROM (
            SELECT json_array(Id, RunId, OperationType, Provider, Model, Purpose, StartedAt, CompletedAt, InputTokens, OutputTokens,
                ReasoningTokens, CachedTokens, PricingSnapshotJson, Status, ProviderRequestId, ActualCostUsd) AS Row
            FROM BillableOperations ORDER BY Id);
        """;
}
