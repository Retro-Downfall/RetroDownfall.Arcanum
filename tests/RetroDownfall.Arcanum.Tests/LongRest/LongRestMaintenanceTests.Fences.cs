using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData("cursor")]
    [InlineData("generation")]
    [InlineData("revision")]
    public async Task Coordinator_refuses_corrupt_committed_checkpoint_before_readmission_or_dispatch(string corruption)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        await CaptureAsync(f);

        var generation = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        LongRestSweepCheckpoint checkpoint = new(1, f.Operation, f.Partition.Kind, f.Partition.CampaignId,
            corruption == "generation" ? Guid.NewGuid() : generation.GenerationId, generation.Phase,
            corruption == "revision" ? long.MaxValue : 0, corruption == "cursor" ? "invented-coordinate" : null, null, f.Partition.ProtectedPartitionDigest);

        Assert.True(await f.Operations.SaveCheckpointAsync(f.Operation, "fixture-worker", 1, 1,
            LongRestSweepCheckpointCodec.Encode(checkpoint).Value, null, "Long Rest", DateTimeOffset.UtcNow));

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z';");

        await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None);

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task Production_physical_callback_is_one_use_for_both_protected_chat_and_embedding()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        f.RepeatDispatch = true;

        LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        Assert.Equal(2, f.Handler.SendCount);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT sum(SentCount) FROM long_rest_provider_effects;"));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("session")]
    public async Task Actual_Campaign_owner_or_path_change_during_journal_await_prevents_send(string mutation)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(campaign: true);

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        f.Journal.AfterCommit = async () =>
        {
            if (mutation == "path")
            {
                await f.H.ExecuteAsync("INSERT INTO campaign_path_identities (CampaignId, PolicyVersion, Revision, DisplayPath, Depth, PhysicalIdentityDigest, UpdatedAtUtc) VALUES ($campaign, 1, 1, '/fixture/remapped', 2, zeroblob(32), '2026-10-10T00:00:00.0000000Z');",
                    ("$campaign", f.CampaignId!.Value.ToString("D").ToUpperInvariant()));
            }
            else
            {
                using var authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(f.H.Connection, CovenantSqliteAuthorizationKind.OwnerCleanup);

                await f.H.ExecuteAsync("DELETE FROM Sessions WHERE Id = $session;", ("$session", f.SourceSession.ToString("D").ToUpperInvariant()));
            }
        };

        await Record.ExceptionAsync(() => f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }

    [Fact]
    public async Task Capability_issuance_rechecks_the_complete_frozen_source_metadata()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        await CaptureAsync(f);

        await MutateAsync(f, "label");

        var result = await f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task Fresh_request_with_stale_frozen_source_cannot_reset_a_parked_operation()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        await CaptureAsync(f);

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z';");

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        var parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        LongRestMaintenanceCapability fresh = LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(f.Operation,
            f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None));

        await MutateAsync(f, "label");

        long before = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await f.Coordinator.ResumeAsync(f.Operation, fresh, CancellationToken.None)).IsFailure);

        Assert.Equal(before, await f.H.ScalarAsync("SELECT total_changes();"));

        Assert.Equal(parked.Revision, (await f.Operations.GetAsync(f.Operation))!.Revision);
    }

    [Fact]
    public async Task Successive_short_units_renew_the_live_claim_before_its_deadline_without_waiting_for_a_long_call()
    {
        FakeTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(clock: clock);

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        DateTimeOffset? original = capability.Claim.LeaseDeadlineUtc;

        clock.Advance(TimeSpan.FromMinutes(4));

        LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        Assert.True(capability.Claim.LeaseDeadlineUtc > original);

        Assert.True(capability.Claim.LeaseDeadlineUtc > clock.GetUtcNow().AddMinutes(4));

        Assert.Equal(2, f.Handler.SendCount);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("revision")]
    [InlineData("cursor")]
    public async Task Production_worker_checkpoint_requires_exact_committed_coordinates(string corruption)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        LongRestDiscoveryGeneration generation = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        LongRestSweepCheckpoint checkpoint = new(1, f.Operation, f.Partition.Kind, f.Partition.CampaignId,
            corruption == "generation" ? null : generation.GenerationId, corruption == "generation" ? null : generation.Phase,
            corruption == "revision" ? long.MaxValue : 0, corruption == "cursor" ? "invented-coordinate" : null, null, f.Partition.ProtectedPartitionDigest);

        Assert.True(await f.Operations.SaveCheckpointAsync(f.Operation, "fixture-worker", 1, 1,
            LongRestSweepCheckpointCodec.Encode(checkpoint).Value, null, "Long Rest", DateTimeOffset.UtcNow));

        await Record.ExceptionAsync(() => f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task Production_maintenance_does_not_recapture_another_worker_between_units()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseOwner = 'stolen-between-units';");

        await Record.ExceptionAsync(() => f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }

    [Fact]
    public async Task Actual_final_accounting_failure_cannot_discard_paid_usage_or_accept_projection()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        int completed = 0;

        f.Completing = () => { if (Interlocked.Increment(ref completed) == 1) throw new IOException("fixture final accounting refusal"); };

        await Record.ExceptionAsync(() => f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        Assert.NotNull(f.ProviderResult);

        Assert.True(f.ProviderResult.IsSuccess);

        Assert.Equal(LongRestProviderResultDisposition.Invalid, f.ProviderResult.Value.Disposition);

        Assert.Equal("LongRest.AccountingCompletionRefused", f.ProviderResult.Value.InvalidReason);

        Assert.Equal(17, f.ProviderResult.Value.Usage.InputTokens);

        Assert.Equal(3, f.ProviderResult.Value.Usage.OutputTokens);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT Status FROM InferenceRuns;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("owner")]
    [InlineData("checkpoint")]
    public async Task Actual_worker_fence_loss_during_journal_await_prevents_physical_send(string mutation)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        f.Journal.AfterCommit = async () =>
        {
            LongRunningOperation operation = (await f.Operations.GetAsync(f.Operation))!;

            if (mutation == "cancel") Assert.True(await f.Operations.RequestCancellationAsync(f.Operation, operation.Revision, DateTimeOffset.UtcNow));

            else await f.H.ExecuteAsync(mutation == "owner"
                ? "UPDATE LongRunningOperations SET LeaseOwner = 'other-worker';"
                : "UPDATE LongRunningOperations SET CheckpointVersion = 0;");
        };

        await Record.ExceptionAsync(() => f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("cancellation")]
    public async Task Actual_paid_refusal_completes_exact_retained_Sent_attempt_as_invalid(string mutation)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(f.Operation,
            f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None));

        Assert.True((await capability.ConsumeAsync(f.Operation, f.Partition, f.Epoch.Capture().Stamp, CancellationToken.None)).IsSuccess);

        LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        using CancellationTokenSource cancelled = new();

        if (mutation == "cancellation") f.PaidRecorded = cancelled.Cancel;
        else f.Handler.InsideSend = () => MutateAsync(f, "configuration");

        await Record.ExceptionAsync(() => f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, cancelled.Token));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 5 AND PaidCount = 1 AND InputTokens = 17 AND OutputTokens = 3 AND CompletionJson IS NOT NULL;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));
    }

    [Fact]
    public async Task Actual_accounting_callback_failure_preserves_received_SDK_usage_without_accepted_projection()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(f.Operation,
            f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None));

        Assert.True((await capability.ConsumeAsync(f.Operation, f.Partition, f.Epoch.Capture().Stamp, CancellationToken.None)).IsSuccess);

        LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        f.PaidRecorded = () => throw new IOException("fixture accounting refusal");

        await Record.ExceptionAsync(() => f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        Assert.NotNull(f.ProviderResult);

        Assert.True(f.ProviderResult.IsSuccess);

        Assert.Equal(LongRestProviderResultDisposition.Invalid, f.ProviderResult.Value.Disposition);

        Assert.Equal("LongRest.AccountingRefused", f.ProviderResult.Value.InvalidReason);

        Assert.Equal(17, f.ProviderResult.Value.Usage.InputTokens);

        Assert.Equal(3, f.ProviderResult.Value.Usage.OutputTokens);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("label")]
    [InlineData("claim")]
    [InlineData("owner")]
    [InlineData("configuration")]
    [InlineData("dataset")]
    [InlineData("capability")]
    [InlineData("authority")]
    public async Task Production_journal_await_mutation_defers_before_sdk_send(string mutation)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        f.Journal.AfterCommit = () => MutateAsync(f, mutation);

        Result<LongRestSweepProgress>? result = null;

        Exception? failure = await Record.ExceptionAsync(async () => result = await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None));

        Assert.True(result?.IsFailure is true || failure is OperationCanceledException);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT COALESCE(sum(SentCount), 0) FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("configuration")]
    [InlineData("cancellation")]
    public async Task Production_paid_refusal_retains_independent_normal_billing_without_fabricated_effect_usage(string mutation)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        using CancellationTokenSource cancellation = new();

        if (mutation == "cancellation") f.PaidRecorded = cancellation.Cancel;

        else f.Handler.InsideSend = () => MutateAsync(f, mutation);

        Result<LongRestSweepProgress>? result = null;

        Exception? failure = await Record.ExceptionAsync(async () => result = await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, cancellation.Token));

        Assert.True(result?.IsFailure is true || failure is OperationCanceledException);

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(17L, await f.H.ScalarAsync("SELECT InputTokens FROM BillableOperations;"));

        Assert.Equal(3L, await f.H.ScalarAsync("SELECT OutputTokens FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 3;"));

        Assert.Equal(mutation == "source" ? 0L : 1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 5 AND CompletionJson IS NOT NULL;"));

        Assert.True(f.ProviderResult?.IsSuccess);

        if (mutation == "source") Assert.Equal(17, f.ProviderResult!.Value.Usage.InputTokens);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("cancellation")]
    public async Task Actual_paid_result_reaches_maintenance_as_typed_invalid_usage_when_currentness_is_lost(string mutation)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        using CancellationTokenSource cancellation = new();

        if (mutation == "cancellation") f.PaidRecorded = cancellation.Cancel;

        else f.Handler.InsideSend = () => MutateAsync(f, mutation);

        _ = await Record.ExceptionAsync(() => f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, cancellation.Token));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.NotNull(f.ProviderResult);

        Assert.True(f.ProviderResult.IsSuccess);

        Assert.Equal(LongRestProviderResultDisposition.Invalid, f.ProviderResult.Value.Disposition);

        Assert.Equal(17, f.ProviderResult.Value.Usage.InputTokens);

        Assert.Equal(3, f.ProviderResult.Value.Usage.OutputTokens);

        Assert.Null(f.ProviderResult.Value.Summary);
    }

    private static async Task<LongRestMaintenanceCapability> CaptureAsync(MaintenanceFixture f)
    {
        LongRestMaintenanceCapability capability = LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(f.Operation,
            f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None));

        Assert.True((await capability.ConsumeAsync(f.Operation, f.Partition, f.Epoch.Capture().Stamp, CancellationToken.None)).IsSuccess);

        Assert.True((await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None)).IsSuccess);

        return capability;
    }

    private static async Task MutateAsync(MaintenanceFixture f, string mutation)
    {
        switch (mutation)
        {
            case "source":
                await f.H.ExecuteAsync("UPDATE saga_memories SET Content = 'changed' WHERE Id = $id;", ("$id", f.Source));

                break;

            case "label":
                await f.H.ExecuteAsync("DROP TRIGGER artifact_sensitivity_guard_update;");

                await f.H.ExecuteAsync("UPDATE artifact_sensitivity SET SensitivityDigest = zeroblob(32) WHERE ArtifactId = $id;", ("$id", f.Source.ToUpperInvariant()));

                break;

            case "claim":
                await f.H.ExecuteAsync("UPDATE session_turn_claims SET ExecutorId = $id;", ("$id", Guid.NewGuid().ToString("D").ToUpperInvariant()));

                break;

            case "owner":
                using (CovenantSqliteConnectionInitializer.Instance.Authorize(f.H.Connection, CovenantSqliteAuthorizationKind.OwnerCleanup))
                {
                    await f.H.ExecuteAsync("DELETE FROM session_campaign_bindings WHERE SessionId = $id;", ("$id", f.SourceSession.ToString("D").ToUpperInvariant()));
                }

                break;

            case "configuration":
                ArcanumSettings changed = LongRestConfigurationEpochTests.Settings();

                changed.Features.LongRest = false;

                f.Options.Set(changed);

                f.Options.Set(LongRestConfigurationEpochTests.Settings());

                break;

            case "dataset":
                f.Availability.PublishCommittedDataset(Guid.NewGuid());

                break;

            case "capability":
                f.Runtime.PublishAvailability(current => current with { FeatureEnabled = false });

                break;

            case "authority":
                Assert.True(f.Runtime.RetireAuthorityGeneration(f.Runtime.Current.RuntimeAuthorityGeneration,
                    CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair)).IsSuccess);

                break;
        }
    }

    private sealed class ObservedProvider(ILongRestProvider inner, Action<Result<LongRestProviderResult>> observed, Func<bool> repeat, Action<LongRestProviderPayload> payloadObserved, Action<CancellationToken> dispatchObserved, Func<Task> providerReturned) : ILongRestProvider
    {
        public async Task<Result<LongRestProviderResult>> ExecuteAsync(LongRestProviderPayload payload, ITurnBudget budget,
            Func<CancellationToken, Task> beforeSend, LongRestProviderAdmission admission, CancellationToken ct)
        {
            payloadObserved(payload);

            dispatchObserved(ct);

            Result<LongRestProviderResult> result = await inner.ExecuteAsync(payload, budget, beforeSend, admission, ct);

            observed(result);

            await providerReturned();

            if (repeat()) Assert.True((await inner.ExecuteAsync(payload, budget, beforeSend, admission, ct)).IsFailure);

            return result;
        }
    }

    private sealed class PaidWriter(ITurnRunWriter inner, Action recorded, Action completing) : ITurnRunWriter
    {
        public Task<Guid> StartRunAsync(InferenceRunStart start, CancellationToken cancellationToken = default) => inner.StartRunAsync(start, cancellationToken);

        public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default) => inner.TryAbandonRunAsync(runId, cancellationToken);

        public Task CompleteRunAsync(Guid runId, InferenceRunStatus status, CancellationToken cancellationToken = default)
        {
            completing();

            return inner.CompleteRunAsync(runId, status, cancellationToken);
        }

        public async Task<Guid> RecordBillableOperationAsync(BillableOperationRecord operation, CancellationToken cancellationToken = default)
        {
            Guid id = await inner.RecordBillableOperationAsync(operation, cancellationToken);

            recorded();

            return id;
        }
    }
}
