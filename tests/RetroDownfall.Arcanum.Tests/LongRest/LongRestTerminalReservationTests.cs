using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed class LongRestTerminalReservationTests
{
    [Fact]
    public async Task Progressed_terminal_reservation_preserves_paid_and_Sent_evidence_then_allows_fresh_generation()
    {
        await using var f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: true, protectedSources: false);

        for (int step = 0; step < 3; step++)
        {
            LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));
        }

        LongRestDiscoveryGeneration original = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        Assert.Equal(LongRestSweepPhase.Discover, original.Phase);

        LongRestSweepStore sweeps = new(f.H.Saga.Context, f.Clock);

        LongRestSweep sweep = LongRestSweepHarness.Require(await sweeps.BeginOrResumeAsync(f.Operation, f.Partition, f.Epoch.Capture().Stamp, CancellationToken.None));

        LongRestSweepWork work = LongRestSweepHarness.Require(await sweeps.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        LongRestAttemptStore attempts = new(f.H.Saga.Context, f.Clock);

        CovenantDigest digest = new(new byte[32]);

        LongRestDispatchAttempt prepared = LongRestSweepHarness.Require(await attempts.PrepareAttemptAsync(work.Identity,
            LongRestProviderPayloadKind.Adjudication, digest, digest, CancellationToken.None));

        LongRestDispatchAttempt sent = LongRestSweepHarness.Require(await attempts.MarkSentAsync(prepared, CancellationToken.None));

        Assert.True((long)(await f.H.ScalarAsync("SELECT SUM(PaidCount) FROM long_rest_provider_effects;"))! > 0);

        Assert.True((long)(await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"))! > 0);

        object effects = (await f.H.ScalarAsync(EffectRows(f.Operation)))!;

        object summaries = (await f.H.ScalarAsync(SummaryRows(f.Operation)))!;

        LongRunningOperation owner = (await f.Operations.GetAsync(f.Operation))!;

        Assert.True(await f.Operations.TryTransitionAsync(owner.Id, owner.Revision, owner.LeaseOwner,
            LongRunningOperationState.Abandoned, DateTimeOffset.UtcNow, "long_rest.cancelled"));

        var fresh = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        Assert.NotEqual(f.Operation, fresh.Id);

        Assert.Equal(effects, await f.H.ScalarAsync(EffectRows(f.Operation)));

        Assert.Equal(summaries, await f.H.ScalarAsync(SummaryRows(f.Operation)));

        Assert.True((await attempts.ValidateAttemptAsync(sent, CancellationToken.None)).IsFailure);

        Assert.True((await attempts.PrepareAttemptAsync(work.Identity, LongRestProviderPayloadKind.Adjudication,
            digest, digest, CancellationToken.None)).IsFailure);

        Assert.True((await f.H.Store.StartOrResumeAsync(f.Operation, f.Partition, original.Fence,
            original.Recipe, CancellationToken.None)).IsFailure);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = f.Handler.LastBody!.Contains("survivorVersionId", StringComparison.Ordinal)
                ? "{\"kind\":\"NoChange\",\"survivorVersionId\":null}" : "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(fresh.Id, null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, progress.Phase);

        Assert.NotEqual(original.GenerationId, progress.GenerationId);

        Assert.Equal(effects, await f.H.ScalarAsync(EffectRows(f.Operation)));

        Assert.Equal(summaries, await f.H.ScalarAsync(SummaryRows(f.Operation)));

        // Retaining historical evidence does not protect it against later source taint/erasure.
        _ = await f.H.ProtectAsync(Guid.Parse(f.Source), GenerationProvenance.CreateExact([Guid.NewGuid()]));

        Assert.Equal("[]", await f.H.ScalarAsync(EffectRows(f.Operation)));

        Assert.Equal("[]", await f.H.ScalarAsync(SummaryRows(f.Operation)));
    }

    private static string EffectRows(Guid operation) => """
        SELECT json_group_array(json_array(WorkItemId, hex(WorkDigest), OriginalWorkJson, AttemptId, PayloadKindCode,
            PhysicalOrdinal, Revision, StateCode, hex(PayloadDigest), hex(DestinationDigest), hex(DisclosureReceiptDigest),
            SentCount, PaidCount, InvalidCount, FailureCount, InputTokens, OutputTokens, TotalTokens, CachedInputTokens,
            ReasoningTokens, CostUsd, CompletionJson, FailureCode, UpdatedAtUtc))
        FROM (SELECT * FROM long_rest_provider_effects WHERE WorkItemId IN
            (SELECT WorkItemId FROM long_rest_work_items WHERE OperationId = '
        """.TrimEnd() + operation.ToString("N") + "') ORDER BY WorkItemId);";

    private static string SummaryRows(Guid operation) => """
        SELECT json_group_array(json_array(WorkItemId, hex(WorkDigest), GenerationId, NodeId, hex(MembershipDigest),
            hex(SourceDigest), hex(RecipeDigest), hex(PayloadDigest), Summary, hex(SummaryDigest), EvidenceJson, CreatedAtUtc))
        FROM (SELECT * FROM long_rest_accepted_summaries WHERE GenerationId IN
            (SELECT GenerationId FROM long_rest_discovery_generations WHERE OperationId = '
        """.TrimEnd() + operation.ToString("N") + "') ORDER BY WorkItemId);";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_creation_gap_releases_same_scope_debt_for_a_fresh_operation(bool cancelBeforeWork)
    {
        TimeProvider clock = cancelBeforeWork ? TimeProvider.System : new FakeTimeProvider();

        await using var f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false, clock: clock);

        await f.H.SeedAsync(Guid.NewGuid().ToString("D"), "Clean first observation.");

        await f.H.SeedAsync(Guid.NewGuid().ToString("D"), "Clean second observation.");

        var first = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        LongRunningOperation before = (await f.Operations.GetAsync(first.Id))!;

        Assert.Equal(LongRunningOperationState.Pending, before.State);

        Assert.Equal(0, before.CheckpointVersion);

        Assert.Null(before.CheckpointPayload);

        long memories = (long)(await f.H.ScalarAsync("SELECT count(*) FROM saga_memories;"))!;

        long versions = (long)(await f.H.ScalarAsync("SELECT count(*) FROM annal_versions;"))!;

        if (cancelBeforeWork)
        {
            Assert.True(await f.Operations.RequestCancellationAsync(first.Id, before.Revision, DateTimeOffset.UtcNow));
        }
        else
        {
            DateTimeOffset now = clock.GetUtcNow();

            LongRunningOperationLeaseResult acquired = await f.Operations.TryAcquireLeaseAsync(first.Id,
                "creation-gap-fixture-worker", now, now.AddMinutes(5));

            Assert.True(acquired.Acquired);

            Assert.Equal(LongRunningOperationState.Running, acquired.Operation.State);

            Assert.Equal(0, acquired.Operation.CheckpointVersion);

            Assert.Null(acquired.Operation.CheckpointPayload);

            ((FakeTimeProvider)clock).Advance(TimeSpan.FromMinutes(6));

            Assert.True(acquired.Operation.LeaseExpiresAt <= clock.GetUtcNow());
        }

        LongRunningOperationReconciler reconciler = new(f.Operations, [new LongRestSweepRecoveryHandler(f.Coordinator)],
            clock, NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        await reconciler.ReconcileNowAsync("terminal-reservation-fixture-owner");

        LongRunningOperation terminal = (await f.Operations.GetAsync(first.Id))!;

        Assert.Equal(LongRunningOperationState.Abandoned, terminal.State);

        Assert.Equal(cancelBeforeWork ? "long_rest.cancelled" : "long_rest.not_started", terminal.TerminalErrorCode);

        Assert.Null(terminal.LeaseOwner);

        Assert.Null(terminal.LeaseExpiresAt);

        Assert.Equal(0, f.Handler.SendCount);

        var replacement = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        Assert.NotEqual(first.Id, replacement.Id);

        Assert.Equal(LongRunningOperationState.Pending, replacement.State);

        AssertOperationSnapshot(terminal, (await f.Operations.GetAsync(first.Id))!);

        Assert.Equal(memories, (long)(await f.H.ScalarAsync("SELECT count(*) FROM saga_memories;"))!);

        Assert.Equal(versions, (long)(await f.H.ScalarAsync("SELECT count(*) FROM annal_versions;"))!);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = f.Handler.LastBody!.Contains("survivorVersionId", StringComparison.Ordinal)
                ? "{\"kind\":\"NoChange\",\"survivorVersionId\":null}" : "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(replacement.Id, null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, progress.Phase);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(replacement.Id))!.State);
    }

    [Fact]
    public async Task Protected_authorized_request_parking_keeps_its_existing_reservation()
    {
        await using var f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRestSweepProgress parked = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal("long_rest.authorized_request_required", parked.DeferredReason);

        LongRunningOperation operation = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, operation.State);

        var requested = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None));

        Assert.Equal(operation.Id, requested.Id);

        AssertOperationSnapshot(operation, (await f.Operations.GetAsync(operation.Id))!);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"));
    }

    private static void AssertOperationSnapshot(LongRunningOperation expected, LongRunningOperation actual)
    {
        // The only array field is compared by content; all remaining record fields remain exact.
        Assert.Equal(expected with { CheckpointPayload = null }, actual with { CheckpointPayload = null });

        Assert.Equal(expected.CheckpointPayload is null, actual.CheckpointPayload is null);

        if (expected.CheckpointPayload is not null)
        {
            Assert.Equal<byte>(expected.CheckpointPayload, actual.CheckpointPayload!);
        }
    }
}
