using System.Data;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Daemons;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The complete committed reset through the real host composition and one real SQLCipher Grimoire.
/// </summary>
[Collection("ProcessEnvironment")]
[Trait("Category", "Integration")]
public sealed class CovenantErasureSameProcessTests
{

    [Fact]
    public async Task Factory_named_operation_creates_server_identity_and_commits_requested_checkpoint_proof()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync();

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        DataRetentionPlan confirmed = await harness.PlanFactoryAsync();

        Guid requested = Guid.Parse("61616161-6161-4161-8161-616161616161");

        harness.RouteGate.ResetApplyObservations();

        Result<DataRetentionApplyResult> result = await harness.ApplyFactoryAsync(
            confirmed.PlanId,
            requested);

        Assert.True(
            result.IsSuccess,
            result.IsFailure
                ? $"{result.Error.Code}: {result.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        LongRunningOperation operation = await harness.ReadFactoryOperationAsync();

        Assert.Equal(operation.Id, result.Value.OperationId);

        Assert.NotEqual(requested, result.Value.OperationId);

        Assert.Equal(requested, result.Value.RequestedOperationId);

        Assert.Null(operation.RootOperationId);

        Assert.Null(operation.ParentOperationId);

        LongRunningOperationRequestIdentity identity =
            await harness.ReadFactoryRequestIdentityAsync(operation.Id);

        Assert.Equal(requested, identity.RequestedOperationId);

        Result<CovenantErasureCheckpointState> checkpoint =
            CovenantErasureCheckpointState.FromFactoryResetCheckpoint(
                operation.Id,
                operation.CheckpointPayload!);

        Assert.True(
            checkpoint.IsSuccess,
            checkpoint.IsFailure
                ? $"{checkpoint.Error.Code}: {checkpoint.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        Assert.Equal(identity.EffectDigest, checkpoint.Value.Owner.EffectDigest);

        Assert.Equal(operation.Id, checkpoint.Value.Owner.OperationId);

        Assert.Equal(operation.Id, harness.RouteGate.ExclusiveOwner?.OperationId);

        Assert.NotEqual(requested, harness.RouteGate.ExclusiveOwner?.OperationId);

    }

    [Fact]
    public async Task Factory_named_completed_replay_precedes_inventory_and_does_not_erase_again()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync();

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        DataRetentionPlan confirmed = await harness.PlanFactoryAsync();

        Guid requested = Guid.Parse("62626262-6262-4262-8262-626262626262");

        Result<DataRetentionApplyResult> first = await harness.ApplyFactoryAsync(
            confirmed.PlanId,
            requested);

        Assert.True(
            first.IsSuccess,
            first.IsFailure
                ? $"{first.Error.Code}: {first.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        Guid preserved = await harness.SeedOrdinarySessionAsync();

        harness.RouteGate.ResetApplyObservations();

        Result<DataRetentionApplyResult> replay = await harness.ApplyFactoryAsync(
            confirmed.PlanId,
            requested);

        Assert.True(
            replay.IsSuccess,
            replay.IsFailure
                ? $"{replay.Error.Code}: {replay.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        Assert.Equal(first.Value.OperationId, replay.Value.OperationId);

        Assert.Equal(requested, replay.Value.RequestedOperationId);

        Assert.Equal(1, await harness.CountOrdinarySessionsAsync());

        Assert.NotEqual(Guid.Empty, preserved);

        Assert.Equal(0, harness.RouteGate.InstallationReadAcquisitions);

        Assert.Null(harness.RouteGate.ExclusiveOwner);

    }

    [Fact]
    public async Task Factory_named_replay_with_a_different_plan_is_an_idempotency_conflict()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync();

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        DataRetentionPlan confirmed = await harness.PlanFactoryAsync();

        Guid requested = Guid.Parse("63636363-6363-4363-8363-636363636363");

        Result<DataRetentionApplyResult> first = await harness.ApplyFactoryAsync(
            confirmed.PlanId,
            requested);

        Assert.True(
            first.IsSuccess,
            first.IsFailure
                ? $"{first.Error.Code}: {first.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        Result<DataRetentionApplyResult> conflict = await harness.ApplyFactoryAsync(
            "a-different-confirmed-plan",
            requested);

        Assert.True(conflict.IsFailure);

        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, conflict.Error.Code);

        Assert.Equal(first.Value.OperationId, (await harness.ReadFactoryOperationAsync()).Id);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_maintains_lease_before_checkpoint(bool named)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CoordinatorPause checkpointPause = new();

        RouteStoreFaults faults = new(RouteStoreFault.None)
        {

            FactoryCheckpointPause = checkpointPause,

        };

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            fastLeaseHeartbeat: true,
            storeFaults: faults);

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        _ = await harness.SeedOrdinarySessionAsync();

        DataRetentionPlan confirmed = await harness.PlanFactoryAsync();

        Task<Result<DataRetentionApplyResult>> applying = harness.ApplyFactoryAsync(
            confirmed.PlanId,
            named ? Guid.Parse("64646464-6464-4464-8464-646464646464") : null);

        try
        {

            await checkpointPause.WaitUntilPausedAsync();

            LongRunningOperation durable = await harness.ReadFactoryOperationAsync();

            await Task.Delay(TimeSpan.FromMilliseconds(1_200));

            LongRunningOperation renewed = await harness.ReadFactoryOperationAsync();

            Assert.Equal(0, renewed.CheckpointVersion);

            Assert.Equal(durable.LeaseOwner, renewed.LeaseOwner);

            Assert.True(renewed.Revision > durable.Revision);

            Assert.True(renewed.LeaseExpiresAt > durable.LeaseExpiresAt);

            LongRunningOperationLeaseResult adoption = await harness.TryAdoptFactoryAsync(
                durable.Id,
                durable.LeaseExpiresAt!.Value.AddMilliseconds(1));

            Assert.False(adoption.Acquired);

            Assert.Equal(1, await harness.CountOrdinarySessionsAsync());

        }
        finally
        {

            checkpointPause.Release();

        }

        _ = await applying.WaitAsync(TimeSpan.FromSeconds(45));

    }

    [Fact]
    public async Task Healthy_factory_erasure_composes_protected_and_ordinary_cleanup_with_exact_public_result()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ConnectionStateObserver connections = new();

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            connectionObserver: connections);

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        _ = await harness.SeedOrdinarySessionAsync();

        DataRetentionPlan confirmed = await harness.PlanFactoryAsync();

        Assert.NotNull(confirmed.Covenant);

        harness.RouteGate.ResetApplyObservations();

        Result<DataRetentionApplyResult> result = await harness.ApplyFactoryAsync(confirmed.PlanId);

        Assert.True(
            result.IsSuccess,
            result.IsFailure
                ? $"{result.Error.Code}: {result.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        Assert.Equal(confirmed.PlanId, result.Value.PlanId);

        Assert.Equal(confirmed.Rows, result.Value.RowsDeleted);

        Assert.Equal(confirmed.Files, result.Value.FilesDeleted);

        Assert.Equal(confirmed.EstimatedBytes, result.Value.EstimatedBytesDeleted);

        Assert.Equal(confirmed.DerivedRecords, result.Value.DerivedRecordsDeleted);

        Assert.Equal(0, await harness.CountOrdinarySessionsAsync());

        Assert.Equal(0, await harness.CountCovenantEntriesAsync());

        Assert.Equal(ConnectionState.Closed, connections.StateAtHandleProof);

        Assert.Equal(1, harness.RouteGate.InstallationReadAcquisitions);

        Assert.Equal(0, harness.RouteGate.InstallationReadsAtExclusiveAdmission);

        LongRunningOperation operation = await harness.ReadFactoryOperationAsync();

        Assert.Equal(DataRetentionFactoryResetCheckpointV1.CurrentVersion, operation.CheckpointVersion);

        Assert.Equal(LongRunningOperationState.Completed, operation.State);

    }

    /// <summary>
    /// A factory erasure completes while a scope that opened the Grimoire for something other than
    /// Covenant is still holding it.
    /// </summary>
    /// <remarks>
    /// The un-enrolled shape, built the way the maintenance sweep driver builds it: a scope resolves
    /// <see cref="ArcanumDbContext"/>, opens its connection and holds it, resolving neither
    /// <c>ICovenantConnectionSource</c> nor <c>ILongRunningOperationStore</c> — the only two
    /// components that were enrolling a held handle with the drain. A handle nothing enrolled
    /// survives both the drain and the pool clear, because it is in use rather than idle, and the
    /// exclusive maintenance connection that follows then burns the full busy timeout on every
    /// wal-index lock its first transaction has to take: tens of seconds of waiting ending in
    /// <c>database is locked</c>, reported as a maintenance failure at the first reset phase.
    ///
    /// <para>Held deliberately rather than raced. On Windows x64 an unrelated scope's lifetime
    /// overlapped the erasure by scheduling accident, which is why that lane reproduced this on
    /// about half its runs and arm64 never did. Held open across the apply, the same failure is
    /// deterministic on every platform, which is what makes it fixable here.</para>
    /// </remarks>
    [Fact]
    public async Task Factory_erasure_completes_while_a_non_Covenant_scope_holds_the_Grimoire_open()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync();

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        DataRetentionPlan confirmed = await harness.PlanFactoryAsync();

        await using AsyncServiceScope holder = harness.Services.CreateAsyncScope();

        ArcanumDbContext held = holder.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await held.Database.OpenConnectionAsync();

        Assert.Equal(ConnectionState.Open, held.Database.GetDbConnection().State);

        harness.RouteGate.ResetApplyObservations();

        Result<DataRetentionApplyResult> result = await harness.ApplyFactoryAsync(confirmed.PlanId);

        Assert.True(
            result.IsSuccess,
            result.IsFailure
                ? $"{result.Error.Code}: {result.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        // The drain is what closed it, and asserting that is the difference between an erasure that
        // survived this handle and one that happened to run before the scope opened it.
        Assert.Equal(ConnectionState.Closed, held.Database.GetDbConnection().State);

    }

    [Fact]
    public async Task Factory_catalog_change_after_planning_refuses_before_exclusive_or_any_effect()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RouteStoreFaults faults = new(RouteStoreFault.None);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(storeFaults: faults);

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        _ = await harness.SeedOrdinarySessionAsync();

        DataRetentionPlan confirmed = await harness.PlanFactoryAsync();

        long ordinaryBefore = await harness.CountOrdinarySessionsAsync();

        long protectedBefore = await harness.CountCovenantEntriesAsync();

        faults.AfterFactoryStarted = harness.DamageFactoryCatalogAsync;

        harness.RouteGate.ResetApplyObservations();

        Result<DataRetentionApplyResult> result = await harness.ApplyFactoryAsync(confirmed.PlanId);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.Equal(ordinaryBefore, await harness.CountOrdinarySessionsAsync());

        Assert.Equal(protectedBefore, await harness.CountCovenantEntriesAsync());

        Assert.Null(harness.RouteGate.ExclusiveOwner);

        LongRunningOperation operation = await harness.ReadFactoryOperationAsync();

        Assert.Equal(0, operation.CheckpointVersion);

    }

    [Fact]
    public async Task Factory_coordinator_failure_blocks_ordinary_deletion()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            routeFailure: RouteFailure.KeepClosed);

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        _ = await harness.SeedOrdinarySessionAsync();

        DataRetentionPlan confirmed = await harness.PlanFactoryAsync();

        long ordinaryBefore = await harness.CountOrdinarySessionsAsync();

        Result<DataRetentionApplyResult> result = await harness.ApplyFactoryAsync(confirmed.PlanId);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, result.Error.Code);

        Assert.Equal(ordinaryBefore, await harness.CountOrdinarySessionsAsync());

    }

    [Fact]
    public async Task Factory_ordinary_cleanup_remains_inside_provider_and_writer_exclusion()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CoordinatorPause pause = new();

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            factoryContinuationPause: pause);

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        _ = await harness.SeedOrdinarySessionAsync();

        DataRetentionPlan confirmed = await harness.PlanFactoryAsync();

        Task<Result<DataRetentionApplyResult>> applying = harness.ApplyFactoryAsync(confirmed.PlanId);

        await pause.WaitUntilPausedAsync();

        ICovenantOperationGate gate = harness.Services.GetRequiredService<ICovenantOperationGate>();

        Result<CovenantWriteLease> writer = await gate.AcquireWriteAsync(
            CovenantOperationScope.Global,
            CancellationToken.None);

        Result<CovenantTurnLease> provider = await gate.AcquireTurnAsync(
            CanonicalCampaignContext.GlobalOnly,
            CancellationToken.None);

        Assert.True(writer.IsFailure);

        Assert.True(provider.IsFailure);

        pause.Release();

        Result<DataRetentionApplyResult> result = await applying.WaitAsync(TimeSpan.FromSeconds(45));

        Assert.True(
            result.IsSuccess,
            result.IsFailure
                ? $"{result.Error.Code}: {result.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

    }

    [Fact]
    public async Task Factory_daemon_history_blocks_while_running_and_clears_after_terminal()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryDaemonExecutionRepository repository = new(
            new InMemoryLogRingBuffer(),
            TimeProvider.System);

        string completed = await repository.StartAsync(
            "daemon-completed",
            "Daemon Completed",
            CancellationToken.None);

        _ = await repository.CompleteAsync(completed, CancellationToken.None);

        string running = await repository.StartAsync(
            "daemon-running",
            "Daemon Running",
            CancellationToken.None);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            serviceOverrides: services =>
            {

                services.RemoveAll<IDaemonExecutionRepository>();

                services.RemoveAll<IDaemonExecutionMutationGate>();

                services.AddSingleton<IDaemonExecutionRepository>(repository);

                services.AddSingleton<IDaemonExecutionMutationGate>(repository);

            });

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        DataRetentionPlan blocked = await harness.PlanFactoryAsync();

        Assert.Contains(
            blocked.Conflicts,
            conflict => conflict.Code == "Data.DaemonExecutionActive"
                && conflict.ResourceId == running);

        Assert.NotNull(await repository.GetAsync(running, CancellationToken.None));

        _ = await repository.CancelAsync(running, CancellationToken.None);

        DataRetentionPlan ready = await harness.PlanFactoryAsync();

        Assert.DoesNotContain(
            ready.Conflicts,
            static conflict => conflict.Code == "Data.DaemonExecutionActive");

        Result<DataRetentionApplyResult> applied = await harness.ApplyFactoryAsync(ready.PlanId);

        Assert.True(
            applied.IsSuccess,
            applied.IsFailure
                ? $"{applied.Error.Code}: {applied.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        Assert.True(applied.Value.Reconciled);

        Assert.Empty(await repository.GetHistoryAsync(null, CancellationToken.None));

    }

    [Fact]
    public async Task Factory_daemon_start_waits_for_ordinary_cleanup_gate()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryDaemonExecutionRepository repository = new(
            new InMemoryLogRingBuffer(),
            TimeProvider.System);

        DataRetentionDaemonHistoryTests.BlockingDaemonMutationGate gate = new(repository);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            serviceOverrides: services =>
            {

                services.RemoveAll<IDaemonExecutionRepository>();

                services.RemoveAll<IDaemonExecutionMutationGate>();

                services.AddSingleton<IDaemonExecutionRepository>(repository);

                services.AddSingleton<IDaemonExecutionMutationGate>(gate);

            });

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        DataRetentionPlan plan = await harness.PlanFactoryAsync();

        Task<Result<DataRetentionApplyResult>> reset = harness.ApplyFactoryAsync(plan.PlanId);

        await gate.Acquired.WaitAsync(TimeSpan.FromSeconds(10));

        Task<string> start = repository.StartAsync(
            "daemon-after-reset",
            "Daemon After Reset",
            CancellationToken.None);

        Task winner = await Task.WhenAny(start, Task.Delay(TimeSpan.FromMilliseconds(100)));

        Assert.NotSame(start, winner);

        gate.Release();

        Result<DataRetentionApplyResult> applied = await reset.WaitAsync(TimeSpan.FromSeconds(45));

        string executionId = await start;

        Assert.True(
            applied.IsSuccess,
            applied.IsFailure
                ? $"{applied.Error.Code}: {applied.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        Assert.NotNull(await repository.GetAsync(executionId, CancellationToken.None));

    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Factory_new_daemon_conflict_at_apply_boundary_terminalizes_before_deletion(
        int activateOnHistoryCall)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DataRetentionDaemonHistoryTests.ActivatingDaemonRepository repository = new(
            TimeProvider.System,
            activateOnHistoryCall);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            serviceOverrides: services =>
            {

                services.RemoveAll<IDaemonExecutionRepository>();

                services.RemoveAll<IDaemonExecutionMutationGate>();

                services.AddSingleton<IDaemonExecutionRepository>(repository);

            });

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        _ = await harness.SeedOrdinarySessionAsync();

        DataRetentionPlan plan = await harness.PlanFactoryAsync();

        Assert.Empty(plan.Conflicts);

        Result<DataRetentionApplyResult> applied = await harness.ApplyFactoryAsync(plan.PlanId);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Data.Conflict, applied.Error.Code);

        Assert.NotNull(
            await repository.GetAsync(
                DataRetentionDaemonHistoryTests.ActivatingDaemonRepository.ExecutionId,
                CancellationToken.None));

        Assert.Equal(1, await harness.CountOrdinarySessionsAsync());

        LongRunningOperation marker = await harness.ReadFactoryOperationAsync();

        Assert.Equal(LongRunningOperationState.Failed, marker.State);

        Assert.Equal(ErrorCodes.Data.Conflict, marker.TerminalErrorCode);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_when_managed_log_publication_wins_deletes_the_counted_append(
        bool guardrail)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using DataRetentionServiceTests.CoordinatedManagedLogMutationGate gate = new();

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            serviceOverrides: services =>
            {

                services.RemoveAll<IManagedLogMutationGate>();

                services.AddSingleton<IManagedLogMutationGate>(gate);

            });

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        ArcanumSettings settings = CreateManagedLogSettings();

        Task publication = PublishManagedLogAsync(
            guardrail,
            settings,
            harness.LogsRoot,
            gate);

        await gate.FirstReleaseRequested.WaitAsync(TimeSpan.FromSeconds(10));

        string pattern = guardrail
            ? "guardrails-????????.jsonl"
            : "audit-????????.jsonl";

        string publishedPath = Assert.Single(
            Directory.EnumerateFiles(harness.LogsRoot, pattern));

        DataRetentionPlan plan = await harness.PlanFactoryAsync();

        DataRetentionPlanItem logItem = Assert.Single(
            plan.Items,
            item => item.DataClass == (guardrail
                ? RetentionDataClass.GuardrailLogs
                : RetentionDataClass.AuditLogs));

        Assert.Equal(1, logItem.Files);

        Task<Result<DataRetentionApplyResult>> reset = harness.ApplyFactoryAsync(plan.PlanId);

        await gate.SecondAttempted.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reset.IsCompleted);

        gate.AllowFirstRelease();

        await publication;

        Result<DataRetentionApplyResult> result = await reset.WaitAsync(TimeSpan.FromSeconds(45));

        Assert.True(
            result.IsSuccess,
            result.IsFailure
                ? $"{result.Error.Code}: {result.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        Assert.True(result.Value.Reconciled);

        Assert.Equal(1, result.Value.FilesDeleted);

        Assert.False(File.Exists(publishedPath));

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_when_reset_wins_waiting_managed_log_publishes_after_reset(
        bool guardrail)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using DataRetentionServiceTests.CoordinatedManagedLogMutationGate gate = new();

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            serviceOverrides: services =>
            {

                services.RemoveAll<IManagedLogMutationGate>();

                services.AddSingleton<IManagedLogMutationGate>(gate);

            });

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        ArcanumSettings settings = CreateManagedLogSettings();

        DataRetentionPlan plan = await harness.PlanFactoryAsync();

        Task<Result<DataRetentionApplyResult>> reset = harness.ApplyFactoryAsync(plan.PlanId);

        await gate.FirstReleaseRequested.WaitAsync(TimeSpan.FromSeconds(10));

        Task publication = PublishManagedLogAsync(
            guardrail,
            settings,
            harness.LogsRoot,
            gate);

        await gate.SecondAttempted.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(publication.IsCompleted);

        Assert.Empty(Directory.EnumerateFiles(harness.LogsRoot, "*.jsonl"));

        gate.AllowFirstRelease();

        Result<DataRetentionApplyResult> result = await reset.WaitAsync(TimeSpan.FromSeconds(45));

        await publication;

        Assert.True(
            result.IsSuccess,
            result.IsFailure
                ? $"{result.Error.Code}: {result.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        Assert.True(result.Value.Reconciled);

        Assert.Equal(0, result.Value.FilesDeleted);

        string pattern = guardrail
            ? "guardrails-????????.jsonl"
            : "audit-????????.jsonl";

        string publishedPath = Assert.Single(
            Directory.EnumerateFiles(harness.LogsRoot, pattern));

        Assert.NotEmpty(await File.ReadAllTextAsync(publishedPath));

        LongRunningOperation marker = await harness.ReadFactoryOperationAsync();

        Assert.Equal(LongRunningOperationState.Completed, marker.State);

    }

    private static Task PublishManagedLogAsync(
        bool guardrail,
        ArcanumSettings settings,
        string logsRoot,
        IManagedLogMutationGate gate)
    {

        TestOptionsMonitor<ArcanumSettings> options = new(settings);

        if (guardrail)
        {

            GuardrailAuditLogger auditLogger = new(
                options,
                NullLogger<GuardrailAuditLogger>.Instance,
                Path.Combine(logsRoot, "guardrails.jsonl"),
                gate);

            GuardrailAuditRecord record = new(
                Timestamp: DateTimeOffset.UtcNow.ToString("O"),
                SessionId: null,
                Stage: "Input",
                ViolationType: "test",
                MatchedTextRedacted: "***",
                Model: "test-model");

            return auditLogger.LogAsync(record, CancellationToken.None);

        }

        InferenceAuditLogger inferenceLogger = new(
            options,
            NullLogger<InferenceAuditLogger>.Instance,
            Path.Combine(logsRoot, "audit.jsonl"),
            gate);

        InferenceAuditRecord inferenceRecord = new(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: null,
            RequestType: "test",
            Model: "test-model",
            Provider: "test-provider",
            PromptTokens: 1,
            CompletionTokens: 1,
            TotalTokens: 2,
            LatencyMs: 1,
            ToolCalls: 0,
            ToolNames: [],
            ToolArgumentsJson: null,
            FinishReason: "stop",
            ClientIp: null,
            SpellName: null,
            CampaignId: null);

        return inferenceLogger.LogAsync(inferenceRecord, CancellationToken.None);

    }

    private static ArcanumSettings CreateManagedLogSettings() =>
        new()
        {

            Features = new FeatureSettings
            {

                Guardrails = true,

            },

            Host = new HostSettings
            {

                AuditLog = new HostAuditPolicySettings
                {

                    Enabled = true,

                },

            },

            Security = new SecuritySettings
            {

                Guardrails = new GuardrailsPolicySettings
                {

                    AuditLog = new GuardrailsAuditPolicySettings
                    {

                        Enabled = true,

                    },

                },

            },

            Retention = new RetentionSettings
            {

                AutomaticSweepsEnabled = false,

            },

        };

    [Fact]
    public async Task Direct_retention_reset_checkpoints_the_exact_owner_before_gate_entry_and_returns_content_free_success()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync();

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        DataRetentionPlan confirmed = await harness.PlanResetAsync();

        harness.RouteGate.ResetApplyObservations();

        Task<Result<DataRetentionApplyResult>> resetTask = harness.ApplyResetAsync(confirmed.PlanId);

        Task revocation = Task.Delay(Timeout.InfiniteTimeSpan, before.ReadLease.Revocation);

        try
        {

            Task first = await Task.WhenAny(resetTask, revocation)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Same(revocation, first);

            LongRunningOperation active = await harness.ReadResetOperationAsync();

            Assert.Equal(DataRetentionMutationCheckpointV3.CurrentVersion, active.CheckpointVersion);

            Result<CovenantErasureCheckpointState> checkpoint = CovenantErasureCheckpointState
                .FromMutationCheckpoint(
                    active.Id,
                    active.CheckpointPayload!,
                    out bool describesCovenantErasure);

            Assert.True(describesCovenantErasure);

            Assert.True(checkpoint.IsSuccess, checkpoint.Error.Message);

            Assert.Equal(CovenantResetPhase.InventoryPrepared, checkpoint.Value.Phase);

            Assert.Equal(checkpoint.Value.Owner, harness.RouteGate.ExclusiveOwner);

            Assert.Equal(1, harness.RouteGate.InstallationReadAcquisitions);

            Assert.Equal(0, harness.RouteGate.InstallationReadsAtExclusiveAdmission);

        }
        finally
        {

            await before.ReadLease.DisposeAsync();

        }

        Result<DataRetentionApplyResult> reset = await resetTask.WaitAsync(TimeSpan.FromSeconds(45));

        Assert.True(
            reset.IsSuccess,
            reset.IsFailure
                ? $"{reset.Error.Code}: {reset.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        Assert.Equal(confirmed.PlanId, reset.Value.PlanId);

        Assert.Equal(0, reset.Value.RowsDeleted);

        Assert.Equal(0, reset.Value.FilesDeleted);

        Assert.Equal(0, reset.Value.EstimatedBytesDeleted);

        Assert.Equal(0, reset.Value.DerivedRecordsDeleted);

        Assert.True(reset.Value.Reconciled);

        LongRunningOperation completed = await harness.ReadResetOperationAsync();

        Assert.Equal(reset.Value.OperationId, completed.Id);

        Assert.Equal(LongRunningOperationState.Completed, completed.State);

    }

    [Fact]
    public async Task Direct_retention_reset_drain_failure_preserves_rows_artifacts_and_managed_file_state()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            drainTimeout: TimeSpan.FromMilliseconds(100));

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        CovenantRouteState stateBefore = await harness.CaptureRouteStateAsync();

        DataRetentionPlan confirmed = await harness.PlanResetAsync();

        Result<DataRetentionApplyResult> reset = await harness
            .ApplyResetAsync(confirmed.PlanId)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(reset.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, reset.Error.Code);

        Assert.Equal(stateBefore, await harness.CaptureRouteStateAsync());

        LongRunningOperation operation = await harness.ReadResetOperationAsync();

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, operation.State);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, operation.TerminalErrorCode);

        Assert.Equal(DataRetentionMutationCheckpointV3.CurrentVersion, operation.CheckpointVersion);

        await before.ReadLease.DisposeAsync();

    }

    [Theory]
    [InlineData(
        RouteFailure.Rollback,
        LongRunningOperationState.Failed,
        ErrorCodes.Covenant.IntegrityFailure)]
    [InlineData(
        RouteFailure.KeepClosed,
        LongRunningOperationState.ReconciliationRequired,
        ErrorCodes.Covenant.ErasureIncomplete)]
    public async Task Direct_retention_reset_maps_noncommit_dispositions_to_typed_failure_and_durable_state(
        RouteFailure failure,
        LongRunningOperationState expectedState,
        string expectedError)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(routeFailure: failure);

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        DataRetentionPlan confirmed = await harness.PlanResetAsync();

        Result<DataRetentionApplyResult> reset = await harness.ApplyResetAsync(confirmed.PlanId);

        Assert.True(reset.IsFailure);

        Assert.Equal(expectedError, reset.Error.Code);

        LongRunningOperation operation = await harness.ReadResetOperationAsync();

        Assert.Equal(expectedState, operation.State);

        Assert.Equal(expectedError, operation.TerminalErrorCode);

        Assert.Equal(DataRetentionMutationCheckpointV3.CurrentVersion, operation.CheckpointVersion);

        Result<CovenantErasureCheckpointState> checkpoint = CovenantErasureCheckpointState
            .FromMutationCheckpoint(
                operation.Id,
                operation.CheckpointPayload!,
                out bool describesCovenantErasure);

        Assert.True(describesCovenantErasure);

        Assert.True(checkpoint.IsSuccess, checkpoint.Error.Message);

    }

    [Fact]
    public async Task Direct_retention_reset_rejects_the_original_expected_plan_mismatch_before_starting_an_operation()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync();

        Result<DataRetentionApplyResult> reset = await harness.ApplyResetAsync("not-the-confirmed-plan");

        Assert.True(reset.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, reset.Error.Code);

        Assert.Null(harness.RouteGate.ExclusiveOwner);

        Assert.Empty(await harness.ReadResetOperationsAsync());

    }

    [Fact]
    public async Task Direct_retention_reset_caller_cancellation_after_proof_still_terminalizes_the_reopened_operation()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using CancellationTokenSource caller = new();

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            routeFailure: RouteFailure.CancelAfterProof,
            cancelAfterProof: caller);

        DataRetentionPlan confirmed = await harness.PlanResetAsync();

        Result<DataRetentionApplyResult> reset = await harness.ApplyResetAsync(
            confirmed.PlanId,
            caller.Token);

        Assert.True(caller.IsCancellationRequested);

        Assert.True(
            reset.IsSuccess,
            reset.IsFailure
                ? $"{reset.Error.Code}: {reset.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        LongRunningOperation operation = await harness.ReadResetOperationAsync();

        Assert.Equal(LongRunningOperationState.Completed, operation.State);

    }

    [Fact]
    public async Task Direct_retention_reset_rejects_an_installation_coverage_lease_of_the_wrong_kind()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync();

        DataRetentionPlan confirmed = await harness.PlanResetAsync();

        harness.RouteGate.ReportInstallationCoverageAsReadKind = true;

        Result<DataRetentionApplyResult> reset = await harness.ApplyResetAsync(confirmed.PlanId);

        Assert.True(reset.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, reset.Error.Code);

        Assert.Null(harness.RouteGate.ExclusiveOwner);

        Assert.Empty(await harness.ReadResetOperationsAsync());

    }

    [Fact]
    public async Task Direct_retention_reset_renews_its_durable_lease_while_the_coordinator_is_running()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CoordinatorPause pause = new();

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            routeFailure: RouteFailure.Rollback,
            coordinatorPause: pause,
            fastLeaseHeartbeat: true);

        DataRetentionPlan confirmed = await harness.PlanResetAsync();

        Task<Result<DataRetentionApplyResult>> resetTask = harness.ApplyResetAsync(confirmed.PlanId);

        await pause.WaitUntilPausedAsync();

        LongRunningOperation before = await harness.ReadResetOperationAsync();

        try
        {

            LongRunningOperation renewed = await harness.WaitForResetRevisionAfterAsync(before.Revision);

            Assert.True(renewed.LeaseExpiresAt > before.LeaseExpiresAt);

        }
        finally
        {

            pause.Release();

        }

        Result<DataRetentionApplyResult> reset = await resetTask.WaitAsync(TimeSpan.FromSeconds(45));

        Assert.True(reset.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, reset.Error.Code);

    }

    [Fact]
    public async Task Direct_retention_reset_cancellation_does_not_park_an_owner_adopted_by_recovery()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RouteStoreFaults faults = new(RouteStoreFault.AdoptBeforeCheckpointCancellation);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(storeFaults: faults);

        DataRetentionPlan confirmed = await harness.PlanResetAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.ApplyResetAsync(confirmed.PlanId));

        LongRunningOperation operation = await harness.ReadResetOperationAsync();

        Assert.Equal(LongRunningOperationState.Running, operation.State);

        Assert.Equal(RouteStoreFaults.AdoptedOwner, operation.LeaseOwner);

    }

    [Theory]
    [InlineData(RouteStoreFault.ThrowBeforeCheckpoint, LongRunningOperationState.Failed, 0)]
    [InlineData(
        RouteStoreFault.ThrowAfterCheckpoint,
        LongRunningOperationState.ReconciliationRequired,
        DataRetentionMutationCheckpointV3.CurrentVersion)]
    public async Task Direct_retention_reset_normalizes_unexpected_exceptions_by_durable_effect_boundary(
        RouteStoreFault fault,
        LongRunningOperationState expectedState,
        int expectedCheckpointVersion)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            storeFaults: new RouteStoreFaults(fault));

        DataRetentionPlan confirmed = await harness.PlanResetAsync();

        Result<DataRetentionApplyResult> reset = await harness.ApplyResetAsync(confirmed.PlanId);

        Assert.True(reset.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, reset.Error.Code);

        LongRunningOperation operation = await harness.ReadResetOperationAsync();

        Assert.Equal(expectedState, operation.State);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, operation.TerminalErrorCode);

        Assert.Equal(expectedCheckpointVersion, operation.CheckpointVersion);

    }

    [Fact]
    public async Task Direct_retention_reset_normalizes_planning_lease_release_failure_after_checkpoint()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync();

        DataRetentionPlan confirmed = await harness.PlanResetAsync();

        harness.RouteGate.ThrowOnNextInstallationRelease = true;

        Result<DataRetentionApplyResult> reset = await harness.ApplyResetAsync(confirmed.PlanId);

        Assert.True(reset.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, reset.Error.Code);

        Assert.Null(harness.RouteGate.ExclusiveOwner);

        LongRunningOperation operation = await harness.ReadResetOperationAsync();

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, operation.State);

        Assert.Equal(DataRetentionMutationCheckpointV3.CurrentVersion, operation.CheckpointVersion);

    }

    [Fact]
    public async Task Direct_retention_reset_retries_completed_cas_after_committed_reopen()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RouteStoreFaults faults = new(RouteStoreFault.FailFirstCompletedTransition);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(storeFaults: faults);

        DataRetentionPlan confirmed = await harness.PlanResetAsync();

        Result<DataRetentionApplyResult> reset = await harness.ApplyResetAsync(confirmed.PlanId);

        Assert.True(
            reset.IsSuccess,
            reset.IsFailure
                ? $"{reset.Error.Code}: {reset.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        Assert.True(faults.CompletedTransitionAttempts >= 2);

        LongRunningOperation operation = await harness.ReadResetOperationAsync();

        Assert.Equal(LongRunningOperationState.Completed, operation.State);

    }

    [Fact]
    public async Task Reopened_verified_recovery_reacquires_an_already_reopened_gate_after_finalizer_failure()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RouteStoreFaults faults = new(RouteStoreFault.FailAllCompletedTransitions);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(storeFaults: faults);

        DataRetentionPlan confirmed = await harness.PlanResetAsync();

        Result<DataRetentionApplyResult> reset = await harness.ApplyResetAsync(confirmed.PlanId);

        Assert.True(reset.IsFailure);

        LongRunningOperation parked = await harness.ReadResetOperationAsync();

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Result<CovenantErasureCheckpointState> checkpoint = CovenantErasureCheckpointState
            .FromMutationCheckpoint(
                parked.Id,
                parked.CheckpointPayload!,
                out bool describesCovenantErasure);

        Assert.True(describesCovenantErasure);

        Assert.True(checkpoint.IsSuccess, checkpoint.Error.Message);

        Assert.Equal(CovenantResetPhase.ReopenedVerified, checkpoint.Value.Phase);

        LongRunningOperationRecoveryResult recovered = await harness.AdoptAndRecoverResetAsync();

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);

    }

    [Fact]
    public async Task Direct_retention_reset_drains_an_inflight_heartbeat_before_completed_releases_the_lease()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using CancellationTokenSource proofSignal = new();

        CoordinatorPause pause = new();

        RouteStoreFaults faults = new(RouteStoreFault.TerminalHeartbeatRace);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            routeFailure: RouteFailure.CancelAfterProof,
            cancelAfterProof: proofSignal,
            coordinatorPause: pause,
            fastLeaseHeartbeat: true,
            storeFaults: faults);

        DataRetentionPlan confirmed = await harness.PlanResetAsync();

        Task<Result<DataRetentionApplyResult>> resetTask = harness.ApplyResetAsync(confirmed.PlanId);

        await pause.WaitUntilPausedAsync();

        await faults.WaitUntilRenewalStartedAsync();

        pause.Release();

        Task firstTerminalSignal = await Task.WhenAny(
            faults.CompletedTransitionStarted,
            faults.ExclusiveLeaseReleased);

        if (ReferenceEquals(firstTerminalSignal, faults.CompletedTransitionStarted))
        {

            faults.AllowCompletedTransition();

            await faults.WaitUntilCompletedTransitionCommittedAsync();

        }
        else
        {

            faults.AllowCompletedTransition();

        }

        faults.AllowRenewal();

        Result<DataRetentionApplyResult> reset = await resetTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(
            reset.IsSuccess,
            reset.IsFailure
                ? $"{reset.Error.Code}: {reset.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        LongRunningOperation operation = await harness.ReadResetOperationAsync();

        Assert.Equal(LongRunningOperationState.Completed, operation.State);

    }

    [Fact]
    public async Task Successful_erasure_reopens_status_crud_inference_and_disclosure_on_the_fresh_dataset()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync();

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await using PausedTurn paused = harness.PauseBeforeLease(before.OldInvocation);

        await paused.WaitUntilPausedAsync();

        Task<Result<CovenantErasureCompletion>> resetTask = harness.RunAsync();

        Task revocation = Task.Delay(Timeout.InfiniteTimeSpan, before.ReadLease.Revocation);

        try
        {

            Task first = await Task.WhenAny(resetTask, revocation)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Same(revocation, first);

            Assert.True(before.ReadLease.Revocation.IsCancellationRequested);

            Assert.False(resetTask.IsCompleted);

        }
        finally
        {

            await before.ReadLease.DisposeAsync();

        }

        Result<CovenantErasureCompletion> reset = await resetTask.WaitAsync(TimeSpan.FromSeconds(45));

        Assert.True(
            reset.IsSuccess,
            reset.IsFailure
                ? $"{reset.Error.Code}: {reset.Error.Message}{harness.CoordinatorDiagnostics()}"
                : null);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, reset.Value.Disposition);

        Assert.True(reset.Value.CanonicalResetApplied);

        Assert.True(reset.Value.LocalSecureErasureComplete);

        Assert.Equal(3, reset.Value.Exposure.PossibleAttempts);

        Assert.Equal(CovenantDisclosureCountKind.Exact, reset.Value.Exposure.CountKind);

        Assert.True(reset.Value.ExternalDisclosuresNotRevocable);

        Assert.NotEqual(before.DatasetGeneration, harness.Availability.Current.DatasetGeneration);

        Result<CovenantTurnContext> raced = await paused.ReleaseAsync();

        await harness.AssertEveryOldCapabilityRejectedAsync(before, raced);

        await harness.AssertFreshStatusAsync();

        await harness.AssertFreshCrudAsync();

        await harness.AssertFreshInferenceContextAsync(before.OldContent);

        await harness.AssertFreshDisclosureWriteAsync();

    }

    private sealed class SameProcessHarness : IAsyncDisposable
    {

        private const string Owner = "task-9-same-process";

        private const string FreshKey = "task9.fresh";

        private const string FreshContent = "fresh generation only";

        private readonly ArcanumWebApplicationFactory _factory;

        private readonly HttpClient _client;

        private readonly AsyncServiceScope _operationScope;

        private readonly CovenantCanonicalErasureFixture _fixture;

        private SameProcessBefore? _before;

        private SameProcessHarness(
            ArcanumWebApplicationFactory factory,
            HttpClient client,
            AsyncServiceScope operationScope,
            CovenantCanonicalErasureFixture fixture)
        {

            _factory = factory;

            _client = client;

            _operationScope = operationScope;

            _fixture = fixture;

        }

        internal IServiceProvider Services => _factory.Services;

        internal CovenantAvailability Availability => Services.GetRequiredService<CovenantAvailability>();

        internal RecordingRouteGate RouteGate => Services.GetRequiredService<RecordingRouteGate>();

        internal string LogsRoot => Path.Combine(_factory.TempHome, ".config", "arcanum");

        /// <summary>
        /// The coordinator's own warnings, for an assertion a refused erasure can fail.
        /// </summary>
        /// <remarks>
        /// A refusal reaches the caller as one error code over a message that names no step, and
        /// <c>Covenant.ErasureIncomplete</c> has seven emitters spread across five phases. The phase is
        /// written down only in the coordinator's warning, so an assertion that can fail on a refusal
        /// carries it — two Windows-only investigations of this class stalled for want of exactly that.
        /// </remarks>
        internal string CoordinatorDiagnostics()
        {

            TestCapturingLogger<CovenantErasureCoordinator> captured =
                Services.GetRequiredService<TestCapturingLogger<CovenantErasureCoordinator>>();

            string[] warnings =
            [
                .. captured.Entries
                    .Where(static entry => entry.Level >= LogLevel.Warning)
                    .Select(static entry => entry.Message),
            ];

            return warnings.Length == 0 ? string.Empty : $" [{string.Join("; ", warnings)}]";

        }

        internal static async Task<SameProcessHarness> CreateAsync(
            TimeSpan? drainTimeout = null,
            RouteFailure routeFailure = RouteFailure.None,
            CancellationTokenSource? cancelAfterProof = null,
            CoordinatorPause? coordinatorPause = null,
            CoordinatorPause? factoryContinuationPause = null,
            ConnectionStateObserver? connectionObserver = null,
            bool fastLeaseHeartbeat = false,
            RouteStoreFaults? storeFaults = null,
            Action<IServiceCollection>? serviceOverrides = null)
        {

            ArcanumWebApplicationFactory factory = new()
            {

                SettingsOverride = static settings => settings with
                {

                    Features = settings.Features with { Covenant = true },

                },

            };

            factory.ServiceOverrides = services =>
            {

                // Registered for every harness, not only the ones expected to refuse: the phase a
                // refused erasure stopped at exists nowhere else, and a run that fails is exactly the
                // run that cannot be re-armed afterwards.
                TestCapturingLogger<CovenantErasureCoordinator> coordinatorLog = new();

                services.AddSingleton(coordinatorLog);

                services.AddSingleton<ILogger<CovenantErasureCoordinator>>(coordinatorLog);

                if (storeFaults is not null)
                {

                    services.RemoveAll<ILongRunningOperationStore>();

                    services.AddScoped<ILongRunningOperationStore>(
                        provider => new RouteOperationStore(
                            ActivatorUtilities.CreateInstance<LongRunningOperationStore>(provider),
                            provider.GetRequiredService<TimeProvider>(),
                            storeFaults));

                }

                if (drainTimeout is { } timeout)
                {

                    services.RemoveAll<CovenantOperationGate>();

                    services.AddSingleton(
                        provider => new CovenantOperationGate(
                            provider.GetRequiredService<CovenantRuntimeGenerationProvider>(),
                            provider.GetRequiredService<ICovenantCampaignScopeProbe>(),
                            timeout));

                }

                services.RemoveAll<ICovenantOperationGate>();

                services.AddSingleton(
                    provider => new RecordingRouteGate(
                        provider.GetRequiredService<CovenantOperationGate>(),
                        storeFaults));

                services.AddSingleton<ICovenantOperationGate>(
                    static provider => provider.GetRequiredService<RecordingRouteGate>());

                if (fastLeaseHeartbeat)
                {

                    services.AddScoped(
                        provider => new DataRetentionLeaseMaintainer(
                            provider.GetRequiredService<ILongRunningOperationStore>().RenewLeaseAsync,
                            provider.GetRequiredService<TimeProvider>(),
                            leaseDuration: DataRetentionLeaseMaintainer.DefaultLeaseDuration,
                            heartbeatInterval: TimeSpan.FromMilliseconds(500)));

                }

                if (coordinatorPause is not null)
                {

                    services.RemoveAll<ICovenantErasureInventorySource>();

                    services.AddScoped<ICovenantErasureInventorySource>(
                        provider => new PausingRouteInventory(
                            coordinatorPause,
                            routeFailure is RouteFailure.Rollback
                                ? new RouteFailureInventory(routeFailure)
                                : provider.GetRequiredService<CovenantErasureInventorySource>()));

                }

                if (coordinatorPause is null
                    && routeFailure is (RouteFailure.Rollback
                        or RouteFailure.KeepClosed
                        or RouteFailure.CancelAfterProof))
                {

                    services.RemoveAll<ICovenantErasureInventorySource>();

                    services.AddScoped<ICovenantErasureInventorySource>(
                        _ => new RouteFailureInventory(routeFailure));

                }

                if (factoryContinuationPause is not null)
                {

                    services.RemoveAll<IManagedLogMutationGate>();

                    services.AddSingleton<IManagedLogMutationGate>(
                        _ => new PausingManagedLogMutationGate(factoryContinuationPause));

                }

                if (routeFailure is RouteFailure.KeepClosed)
                {

                    services.RemoveAll<ICovenantErasureTransition>();

                    services.AddScoped<ICovenantErasureTransition>(
                        static _ => new RouteFailureTransition());

                }

                else if (routeFailure is RouteFailure.CancelAfterProof)
                {

                    services.RemoveAll<ICovenantErasureTransition>();

                    services.AddScoped<ICovenantErasureTransition>(
                        _ => new RouteCancellationTransition(
                            cancelAfterProof
                                ?? throw new InvalidOperationException(
                                    "The cancellation source is required.")));

                }

                if (connectionObserver is not null)
                {

                    services.RemoveAll<ICovenantErasureTransition>();

                    services.AddScoped<ICovenantErasureTransition>(
                        provider => new ConnectionObservingTransition(
                            provider.GetRequiredService<CovenantErasureTransition>(),
                            provider.GetRequiredService<ArcanumDbContext>(),
                            connectionObserver));

                }

                serviceOverrides?.Invoke(services);

            };

            try
            {

                HttpClient client = factory.CreateAuthenticatedClient();

                AsyncServiceScope operationScope = factory.Services.CreateAsyncScope();

                IServiceProvider services = operationScope.ServiceProvider;

                CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.AttachAsync(
                    factory.Services.GetRequiredService<ICovenantMaintenanceConnectionFactory>(),
                    factory.Services.GetRequiredService<ICovenantSqliteConnectionInitializer>(),
                    factory.Services.GetRequiredService<ICovenantConnectionDrain>(),
                    CancellationToken.None);

                return new SameProcessHarness(factory, client, operationScope, fixture);

            }
            catch
            {

                await factory.DisposeAsync();

                throw;

            }

        }

        internal async Task<Guid> SeedOrdinarySessionAsync()
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            ArcanumDbContext database = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            Guid sessionId = Guid.NewGuid();

            database.Sessions.Add(
                new Session
                {

                    Id = sessionId,

                    Status = "archived",

                    CreatedAt = DateTimeOffset.UnixEpoch,

                    UpdatedAt = DateTimeOffset.UnixEpoch,

                });

            await database.SaveChangesAsync();

            await database.Database.CloseConnectionAsync();

            return sessionId;

        }

        internal async Task<DataRetentionPlan> PlanFactoryAsync()
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            return await scope.ServiceProvider
                .GetRequiredService<IDataRetentionService>()
                .PlanAsync(
                    new DataRetentionRequest(DataRetentionOperation.FactoryReset),
                    CancellationToken.None);

        }

        internal async Task<Result<DataRetentionApplyResult>> ApplyFactoryAsync(
            string expectedPlanId,
            Guid? requestedOperationId = null)
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            return await scope.ServiceProvider
                .GetRequiredService<IDataRetentionService>()
                .ApplyAsync(
                    new DataRetentionApplyRequest(
                        new DataRetentionRequest(DataRetentionOperation.FactoryReset),
                        expectedPlanId,
                        requestedOperationId),
                    CancellationToken.None);

        }

        internal async Task<LongRunningOperationRequestIdentity> ReadFactoryRequestIdentityAsync(
            Guid operationId)
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            LongRunningOperationRequestIdentity? identity = await scope.ServiceProvider
                .GetRequiredService<ILongRunningOperationStore>()
                .FindRequestIdentityAsync(operationId, CancellationToken.None);

            return Assert.IsType<LongRunningOperationRequestIdentity>(identity);

        }

        internal async Task<LongRunningOperation> ReadFactoryOperationAsync()
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            IReadOnlyList<LongRunningOperation> operations = await scope.ServiceProvider
                .GetRequiredService<ILongRunningOperationStore>()
                .ListAsync(
                    new LongRunningOperationQuery(
                        LongRunningOperationKinds.DataRetentionFactoryReset,
                        Limit: 10),
                    CancellationToken.None);

            return Assert.Single(operations);

        }

        internal async Task<LongRunningOperationLeaseResult> TryAdoptFactoryAsync(
            Guid operationId,
            DateTimeOffset utcNow)
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            return await scope.ServiceProvider
                .GetRequiredService<ILongRunningOperationStore>()
                .TryAcquireLeaseAsync(
                    operationId,
                    "factory-v0-recovery-probe",
                    utcNow,
                    utcNow.AddMinutes(2),
                    CancellationToken.None);

        }

        internal Task DamageFactoryCatalogAsync(CancellationToken cancellationToken) =>
            _fixture.ExecuteAsync(
                "DROP TRIGGER covenant_entries_guard_delete;",
                cancellationToken);

        internal async Task<long> CountCovenantEntriesAsync()
        {

            await _fixture.ReopenAsync(CancellationToken.None);

            return await _fixture.CountAsync(
                "covenant_entries",
                CancellationToken.None);

        }

        internal async Task<long> CountOrdinarySessionsAsync()
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            ArcanumDbContext database = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            long count = await database.Sessions.LongCountAsync();

            await database.Database.CloseConnectionAsync();

            return count;

        }

        internal async Task<DataRetentionPlan> PlanResetAsync()
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            return await scope.ServiceProvider
                .GetRequiredService<IDataRetentionService>()
                .PlanAsync(
                    new DataRetentionRequest(
                        DataRetentionOperation.ResetMemory,
                        MemoryScope: MemoryResetScope.Covenant),
                    CancellationToken.None);

        }

        internal async Task<Result<DataRetentionApplyResult>> ApplyResetAsync(
            string expectedPlanId,
            CancellationToken cancellationToken = default)
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            return await scope.ServiceProvider
                .GetRequiredService<IDataRetentionService>()
                .ApplyAsync(
                    new DataRetentionApplyRequest(
                        new DataRetentionRequest(
                            DataRetentionOperation.ResetMemory,
                            MemoryScope: MemoryResetScope.Covenant),
                        expectedPlanId),
                    cancellationToken);

        }

        internal async Task<LongRunningOperation> ReadResetOperationAsync()
        {

            IReadOnlyList<LongRunningOperation> operations = await ReadResetOperationsAsync();

            return Assert.Single(operations);

        }

        internal async Task<IReadOnlyList<LongRunningOperation>> ReadResetOperationsAsync()
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            return await scope.ServiceProvider
                .GetRequiredService<ILongRunningOperationStore>()
                .ListAsync(
                    new LongRunningOperationQuery(
                        LongRunningOperationKinds.DataRetentionMutation,
                        Limit: 10),
                    CancellationToken.None);

        }

        internal async Task<LongRunningOperation> WaitForResetRevisionAfterAsync(long revision)
        {

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

            while (true)
            {

                LongRunningOperation operation = await ReadResetOperationAsync();

                if (operation.Revision > revision)
                {

                    return operation;

                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);

            }

        }

        internal async Task<LongRunningOperationRecoveryResult> AdoptAndRecoverResetAsync()
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            ILongRunningOperationStore store = scope.ServiceProvider
                .GetRequiredService<ILongRunningOperationStore>();

            LongRunningOperation current = await ReadResetOperationAsync();

            TimeProvider time = scope.ServiceProvider.GetRequiredService<TimeProvider>();

            DateTimeOffset now = time.GetUtcNow();

            LongRunningOperationLeaseResult adopted = await store.TryAcquireLeaseAsync(
                current.Id,
                "review-recovery-owner",
                now,
                now.Add(TimeSpan.FromMinutes(5)),
                CancellationToken.None);

            Assert.True(adopted.Acquired);

            return await scope.ServiceProvider
                .GetRequiredService<DataRetentionService>()
                .RecoverMutationAsync(adopted.Operation, CancellationToken.None);

        }

        internal async Task<CovenantRouteState> CaptureRouteStateAsync()
        {

            await _fixture.ReopenAsync(CancellationToken.None);

            return new CovenantRouteState(
                await _fixture.CountAsync("covenant_entries", CancellationToken.None),
                await _fixture.CountAsync("artifact_sensitivity", CancellationToken.None),
                await _fixture.CountAsync("managed_file_write_intents", CancellationToken.None));

        }

        internal async Task<SameProcessBefore> SeedAndCaptureAsync()
        {

            await _fixture.SeedAcceptanceStateAsync(CancellationToken.None);

            CovenantRuntimeGenerationProvider runtime = Services
                .GetRequiredService<CovenantRuntimeGenerationProvider>();

            CovenantEnvelopeMasterKeyProvider root = Services
                .GetRequiredService<CovenantEnvelopeMasterKeyProvider>();

            CovenantDisclosureWriter writer = Services.GetRequiredService<CovenantDisclosureWriter>();

            CovenantAvailabilitySnapshot availability = Availability.Current;

            Assert.True(availability.FeatureEnabled);

            Assert.Equal(CovenantCapabilityState.Healthy, availability.Canonical);

            Assert.NotEqual(Guid.Empty, availability.DatasetGeneration);

            Assert.NotNull(runtime.Current.ActiveAuthority);

            Assert.NotNull(runtime.Current.Keys);

            Guid datasetGeneration = availability.DatasetGeneration
                ?? throw new InvalidOperationException("The published Covenant dataset is empty.");

            Assert.Equal(
                datasetGeneration,
                await _fixture.ReadDatasetGenerationAsync(CancellationToken.None));

            ICovenantDisclosureJournal journal = Services.GetRequiredService<ICovenantDisclosureJournal>();

            Result<CovenantDisclosureReceipt> warmed = await journal.AcknowledgeAsync(
                Draft(datasetGeneration, effectSeed: 0x31),
                CovenantDisclosureEffectCategory.ProviderDispatch,
                Sensitivity(datasetGeneration),
                CancellationToken.None);

            Assert.True(warmed.IsSuccess, warmed.Error.Message);

            ICovenantEnvelopeCodec codec = Services.GetRequiredService<ICovenantEnvelopeCodec>();

            Dictionary<CovenantEnvelopePurpose, string> tokens = [];

            foreach (CovenantEnvelopePurpose purpose in Enum.GetValues<CovenantEnvelopePurpose>())
            {

                Result<string> encoded = codec.Encode(
                    purpose,
                    [(byte)purpose],
                    TimeSpan.FromMinutes(10));

                Assert.True(
                    encoded.IsSuccess,
                    $"{purpose}: {encoded.Error.Code} {encoded.Error.Message}");

                tokens.Add(purpose, encoded.Value);

            }

            IOperatorAuthorityContextIssuer issuer = Services
                .GetRequiredService<IOperatorAuthorityContextIssuer>();

            OperatorAuthorityContext operatorContext = issuer
                .Issue(CovenantAuthorityRequirement.CovenantManage).Value;

            CovenantReadAuthorityEpoch readEpoch = issuer.IssueReadEpoch().Value;

            ICovenantOperationGate gate = Services.GetRequiredService<ICovenantOperationGate>();

            CovenantReadLease readLease = (await gate.AcquireReadAsync(
                CovenantOperationScope.Global,
                CancellationToken.None)).Value;

            ArcanumInvocationContext oldInvocation = CreateInvocation(readEpoch);

            _before = new SameProcessBefore(
                datasetGeneration,
                runtime,
                root,
                writer,
                tokens,
                operatorContext,
                readEpoch,
                readLease,
                oldInvocation,
                "be brief");

            return _before;

        }

        internal async Task<Result<CovenantErasureCompletion>> RunAsync()
        {

            SameProcessBefore before = _before
                ?? throw new InvalidOperationException("The old generation must be captured before reset.");

            ILongRunningOperationCoordinator operations = _operationScope.ServiceProvider
                .GetRequiredService<ILongRunningOperationCoordinator>();

            LongRunningOperationLeaseResult started = await operations.StartAsync(
                new LongRunningOperationCreateRequest(
                    LongRunningOperationKinds.DataRetentionMutation,
                    LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                    "Task 9 same-process Covenant reset.",
                    DateTimeOffset.UtcNow),
                Owner,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);

            Assert.True(started.Acquired);

            CovenantResetCheckpointInitiator initiator = _operationScope.ServiceProvider
                .GetRequiredService<CovenantResetCheckpointInitiator>();

            Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await initiator
                .PrepareCovenantResetInventoryAsync(
                    started.Operation,
                    Owner,
                    new CovenantErasureEffectDigestInput(
                        CovenantExclusiveOperation.CovenantReset,
                        "task-9-success",
                        before.DatasetGeneration,
                        Rows: 3,
                        ManagedFiles: 0,
                        LocalArtifacts: 0,
                        AffectedSessions: 0,
                        PossibleDisclosures: 3,
                        CovenantDisclosureCountKind.Exact),
                    requestedOperationId: null,
                    MemoryResetScope.Covenant,
                    CancellationToken.None);

            Assert.True(admitted.IsSuccess, admitted.Error.Message);

            ILongRunningOperationStore store = _operationScope.ServiceProvider
                .GetRequiredService<ILongRunningOperationStore>();

            LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
                await store.GetAsync(started.Operation.Id, CancellationToken.None));

            Result<CovenantErasureCheckpointState> checkpoint = CovenantErasureCheckpointState
                .FromMutationCheckpoint(
                    operation.Id,
                    operation.CheckpointPayload!,
                    out bool describesCovenantErasure);

            Assert.True(describesCovenantErasure);

            Assert.True(checkpoint.IsSuccess, checkpoint.Error.Message);

            CovenantErasureCoordinator coordinator = _operationScope.ServiceProvider
                .GetRequiredService<CovenantErasureCoordinator>();

            return await coordinator.RunAsync(
                operation,
                checkpoint.Value,
                Owner,
                CancellationToken.None);

        }

        internal PausedTurn PauseBeforeLease(ArcanumInvocationContext invocation)
        {

            TaskCompletionSource<bool> paused = new(TaskCreationOptions.RunContinuationsAsynchronously);

            TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<Result<CovenantTurnContext>> result = Task.Run(
                async () =>
                {

                    await using AsyncServiceScope scope = Services.CreateAsyncScope();

                    ICovenantContextProvider context = scope.ServiceProvider
                        .GetRequiredService<ICovenantContextProvider>();

                    _ = paused.TrySetResult(true);

                    await release.Task;

                    return await context.BeginTurnAsync(
                        invocation,
                        Guid.NewGuid(),
                        CancellationToken.None);

                });

            return new PausedTurn(paused.Task, release, result);

        }

        internal async Task AssertEveryOldCapabilityRejectedAsync(
            SameProcessBefore before,
            Result<CovenantTurnContext> raced)
        {

            Assert.Same(before.Runtime, Services.GetRequiredService<CovenantRuntimeGenerationProvider>());

            Assert.Same(before.Root, Services.GetRequiredService<CovenantEnvelopeMasterKeyProvider>());

            Assert.Same(before.Writer, Services.GetRequiredService<CovenantDisclosureWriter>());

            Assert.Same(
                before.Writer,
                Services.GetRequiredService<ICovenantDisclosureJournal>());

            Assert.Same(
                before.Writer,
                Services.GetRequiredService<ICovenantDisclosureWriterLifecycle>());

            ICovenantEnvelopeCodec codec = Services.GetRequiredService<ICovenantEnvelopeCodec>();

            foreach ((CovenantEnvelopePurpose purpose, string token) in before.Tokens)
            {

                Assert.True(codec.Decode(purpose, token).IsFailure);

                Result<string> issued = codec.Encode(
                    purpose,
                    [(byte)(0x80 + (byte)purpose)],
                    TimeSpan.FromMinutes(10));

                Assert.True(issued.IsSuccess, issued.Error.Message);

                Assert.True(codec.Decode(purpose, issued.Value).IsSuccess);

            }

            IOperatorAuthorityContextIssuer issuer = Services
                .GetRequiredService<IOperatorAuthorityContextIssuer>();

            Assert.True(issuer.Revalidate(before.OperatorContext).IsFailure);

            ICovenantAuthoritySnapshotProvider authority = Services
                .GetRequiredService<ICovenantAuthoritySnapshotProvider>();

            Assert.False(before.ReadEpoch.Matches(authority.Current));

            Assert.True((await before.ReadLease.RevalidateAsync(CancellationToken.None)).IsFailure);

            Assert.True(raced.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, raced.Error.Code);

            await using CovenantReadLease fresh = (await Services
                .GetRequiredService<ICovenantOperationGate>()
                .AcquireReadAsync(CovenantOperationScope.Global, CancellationToken.None)).Value;

            Assert.Equal(Availability.Current.DatasetGeneration, fresh.Snapshot.DatasetGeneration);

        }

        internal async Task AssertFreshStatusAsync()
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            DataRetentionStatus status = await scope.ServiceProvider
                .GetRequiredService<IDataRetentionService>()
                .GetStatusAsync(CancellationToken.None);

            DataRetentionCovenantInventory covenant = Assert.IsType<DataRetentionCovenantInventory>(
                status.Covenant);

            Assert.Equal(0, covenant.ManagedFiles);

            Assert.Equal(0, covenant.LocalArtifacts);

            Assert.Equal(0, covenant.AffectedSessions);

            Assert.Equal(3, covenant.PossibleDisclosures);

            Assert.Equal(CovenantDisclosureCountKind.Exact, covenant.DisclosureCountKind);

        }

        internal async Task AssertFreshCrudAsync()
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            ICovenantOperationGate gate = Services.GetRequiredService<ICovenantOperationGate>();

            await using (CovenantWriteLease lease = (await gate.AcquireWriteAsync(
                CovenantOperationScope.Global,
                CancellationToken.None)).Value)
            {

                ICovenantConnectionSource connections = scope.ServiceProvider
                    .GetRequiredService<ICovenantConnectionSource>();

                SqliteConnection connection = await connections.GetOpenConnectionAsync(CancellationToken.None);

                await using SqliteTransaction transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(IsolationLevel.Serializable, CancellationToken.None);

                long keyEpoch = await ScalarAsync(
                    connection,
                    transaction,
                    "SELECT KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;");

                long registryEpoch = await ScalarAsync(
                    connection,
                    transaction,
                    "SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;");

                CovenantMutationBatch batch = new(
                    Availability.Current.DatasetGeneration
                        ?? throw new InvalidOperationException("The fresh Covenant dataset is empty."),
                    keyEpoch,
                    registryEpoch,
                    DateTimeOffset.UtcNow,
                    [
                        CovenantMutationFixture.OperatorSet(
                            CovenantOperationScope.Global,
                            FreshKey,
                            FreshContent,
                            expectedRevision: 0,
                            expectedKeyEpoch: 0),
                    ]);

                Result<IReadOnlyList<CovenantMutationReceipt>> applied = await scope.ServiceProvider
                    .GetRequiredService<CovenantMutationKernel>()
                    .ApplyBatchAsync(
                        batch,
                        new CovenantMutationTransaction(connection, transaction),
                        CancellationToken.None);

                Assert.True(applied.IsSuccess, applied.Error.Message);

                await transaction.CommitAsync(CancellationToken.None);

            }

            await using CovenantReadLease readLease = (await gate.AcquireReadAsync(
                CovenantOperationScope.Global,
                CancellationToken.None)).Value;

            Result<CovenantTurnSnapshot> snapshot = await scope.ServiceProvider
                .GetRequiredService<ICovenantStore>()
                .ReadTurnSnapshotAsync(
                    CanonicalCampaignContext.GlobalOnly,
                    readLease,
                    CancellationToken.None);

            Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

            CovenantSnapshotCandidate fresh = Assert.Single(snapshot.Value.Candidates);

            Assert.Equal(FreshKey, fresh.NormalizedKey.Value);

            Assert.Equal(
                CovenantMutationFixture.Artifact(FreshKey, FreshContent).CompiledContent,
                Encoding.UTF8.GetString(fresh.CompiledFragment.ToArray()));

        }

        internal async Task AssertFreshInferenceContextAsync(string oldContent)
        {

            IOperatorAuthorityContextIssuer issuer = Services
                .GetRequiredService<IOperatorAuthorityContextIssuer>();

            ArcanumInvocationContext invocation = CreateInvocation(issuer.IssueReadEpoch().Value);

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            Result<CovenantTurnContext> begun = await scope.ServiceProvider
                .GetRequiredService<ICovenantContextProvider>()
                .BeginTurnAsync(invocation, Guid.NewGuid(), CancellationToken.None);

            Assert.True(begun.IsSuccess, begun.Error.Message);

            await using CovenantTurnContext context = begun.Value;

            Assert.True(context.HasPlan);

            Assert.Contains(FreshContent, context.PlanContent.GlobalConfirmed, StringComparison.Ordinal);

            Assert.DoesNotContain(oldContent, context.PlanContent.GlobalConfirmed, StringComparison.Ordinal);

        }

        internal async Task AssertFreshDisclosureWriteAsync()
        {

            Guid dataset = Availability.Current.DatasetGeneration
                ?? throw new InvalidOperationException("The fresh Covenant dataset is empty.");

            Result<CovenantDisclosureReceipt> acknowledged = await Services
                .GetRequiredService<ICovenantDisclosureJournal>()
                .AcknowledgeAsync(
                    Draft(dataset, effectSeed: 0x42),
                    CovenantDisclosureEffectCategory.ProviderDispatch,
                    Sensitivity(dataset),
                    CancellationToken.None);

            Assert.True(acknowledged.IsSuccess, acknowledged.Error.Message);

        }

        public async ValueTask DisposeAsync()
        {

            await _fixture.DisposeAsync();

            await _operationScope.DisposeAsync();

            _client.Dispose();

            await _factory.DisposeAsync();

        }

        private static ArcanumInvocationContext CreateInvocation(CovenantReadAuthorityEpoch epoch) =>
            ArcanumInvocationContext.Create(
                ArcanumExecutionSurface.StatelessOperatorTurn,
                CanonicalCampaignContext.GlobalOnly,
                InvocationAttendance.Attended,
                CovenantContextPolicy.Default,
                ToolPolicy.AllTools,
                epoch).Value;

        private static ProviderCallSensitivity Sensitivity(Guid dataset)
        {

            GenerationProvenance provenance = GenerationProvenance.CreateExact([dataset]);

            return new ProviderCallSensitivity(
                ContentSensitivity.CovenantDerived,
                provenance,
                CovenantDigests.Sensitivity(provenance.ToDigestInput(ContentSensitivity.CovenantDerived)));

        }

        private static CovenantDisclosureDraft Draft(Guid dataset, byte effectSeed)
        {

            ProviderCallSensitivity sensitivity = Sensitivity(dataset);

            return new CovenantDisclosureDraft(
                new Guid("6f1c0b2e-9a44-4e1d-8b7a-2c5d3f6a8e90"),
                CovenantDisclosureSubjectKind.Operation,
                new Guid("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
                CovenantOperationGateFixture.Digest(effectSeed),
                CovenantEgressDestination.Provider,
                CovenantDisclosureRevocability.Nonrevocable,
                CovenantOperationGateFixture.Digest(0x51),
                sensitivity.Digest,
                wardEvidenceDigest: null,
                CovenantOperationGateFixture.Digest(0x52),
                backupEvidenceDigest: null,
                timestamp: 1_700_000_000_000L + effectSeed);

        }

        private static async Task<long> ScalarAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql)
        {

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = sql;

            object? value = await command.ExecuteScalarAsync(CancellationToken.None);

            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

        }

    }

    private sealed record CovenantRouteState(
        long CovenantRows,
        long ProtectedArtifacts,
        long ManagedFiles);

    public enum RouteFailure
    {

        None,

        Rollback,

        KeepClosed,

        CancelAfterProof,

    }

    internal sealed class CoordinatorPause
    {

        private readonly TaskCompletionSource _paused = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        internal Task WaitUntilPausedAsync() => _paused.Task.WaitAsync(TimeSpan.FromSeconds(5));

        internal async Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {

            _paused.TrySetResult();

            await _release.Task.WaitAsync(cancellationToken);

        }

    }

    internal sealed class ConnectionStateObserver
    {

        internal ConnectionState? StateAtHandleProof { get; set; }

    }

    private sealed class PausingManagedLogMutationGate(CoordinatorPause pause)
        : IManagedLogMutationGate
    {

        public async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(
            CancellationToken cancellationToken = default)
        {

            await pause.WaitForReleaseAsync(cancellationToken);

            return NoopAsyncDisposable.Instance;

        }

    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {

        internal static NoopAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

    private sealed class ConnectionObservingTransition(
        ICovenantErasureTransition inner,
        ArcanumDbContext database,
        ConnectionStateObserver observer) : ICovenantErasureTransition
    {

        public Task<Result<Guid>> ApplyCanonicalErasureAsync(
            CovenantExclusiveOperation operation,
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken) =>
            inner.ApplyCanonicalErasureAsync(operation, capability, cancellationToken);

        public Task<Result> CloseHandlesAsync(CancellationToken cancellationToken)
        {

            observer.StateAtHandleProof = database.Database.GetDbConnection().State;

            return inner.CloseHandlesAsync(cancellationToken);

        }

        public Task<Result> TruncateWalAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            inner.TruncateWalAsync(capability, cancellationToken);

        public Task<Result> CompactAsync(CovenantV3CompactionCapabilities capabilities, CancellationToken cancellationToken) =>
            inner.CompactAsync(capabilities, cancellationToken);

        public Task<Result> InitializeAcceleratorAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            inner.InitializeAcceleratorAsync(capability, cancellationToken);

        public Task<Result> VerifySidecarAbsenceAsync(CancellationToken cancellationToken) =>
            inner.VerifySidecarAbsenceAsync(cancellationToken);

        public Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken) =>
            inner.VerifyReopenAsync(capability, cancellationToken);

        public Task<Result> PublishCommittedAsync(
            ICovenantExclusiveOperationLease lease,
            CovenantVerifiedCandidateState candidate,
            CancellationToken cancellationToken) =>
            inner.PublishCommittedAsync(lease, candidate, cancellationToken);

    }

    private sealed class PausingRouteInventory(
        CoordinatorPause pause,
        ICovenantErasureInventorySource inner) : ICovenantErasureInventorySource
    {

        public async Task<Result<CovenantErasureInventorySummary>> PreflightBeforeCanonicalAsync(
            CovenantExclusiveOperation operation,
            Guid datasetGeneration,
            CancellationToken cancellationToken)
        {

            await pause.WaitForReleaseAsync(cancellationToken);

            return await inner
                .PreflightBeforeCanonicalAsync(operation, datasetGeneration, cancellationToken)
                .ConfigureAwait(false);

        }

        public Task<Result> PreflightRemainingManagedAsync(CancellationToken cancellationToken) =>
            inner.PreflightRemainingManagedAsync(cancellationToken);

        public Task<Result<CovenantDatabaseErasureBatch>> ReadNextDatabaseBatchAsync(
            Guid datasetGeneration,
            Guid? afterLabelId,
            CancellationToken cancellationToken) =>
            inner.ReadNextDatabaseBatchAsync(datasetGeneration, afterLabelId, cancellationToken);

        public Task<Result<CovenantManagedFileErasureBatch>> ReadNextManagedFileBatchAsync(
            Guid operationId,
            Guid? afterLabelId,
            CancellationToken cancellationToken) =>
            inner.ReadNextManagedFileBatchAsync(operationId, afterLabelId, cancellationToken);

        public Task<Result<CovenantDisclosureExposure>> ReadDisclosureExposureAsync(
            CancellationToken cancellationToken) =>
            inner.ReadDisclosureExposureAsync(cancellationToken);

    }

    private sealed class RouteFailureInventory(RouteFailure failure) : ICovenantErasureInventorySource
    {

        public Task<Result<CovenantErasureInventorySummary>> PreflightBeforeCanonicalAsync(
            CovenantExclusiveOperation operation,
            Guid datasetGeneration,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                failure is RouteFailure.Rollback
                    ? Result<CovenantErasureInventorySummary>.Failure(
                        new Error(
                            ErrorCodes.Covenant.IntegrityFailure,
                            "The direct reset inventory was refused."))
                    : Result<CovenantErasureInventorySummary>.Success(
                        new CovenantErasureInventorySummary(
                            0,
                            0,
                            new CovenantDisclosureExposure(
                                0,
                                CovenantDisclosureCountKind.Exact))));

        public Task<Result> PreflightRemainingManagedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<CovenantDatabaseErasureBatch>> ReadNextDatabaseBatchAsync(
            Guid datasetGeneration,
            Guid? afterLabelId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<CovenantDatabaseErasureBatch>.Success(
                    new CovenantDatabaseErasureBatch(afterLabelId, true, page: null)));

        public Task<Result<CovenantManagedFileErasureBatch>> ReadNextManagedFileBatchAsync(
            Guid operationId,
            Guid? afterLabelId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<CovenantManagedFileErasureBatch>.Success(
                    new CovenantManagedFileErasureBatch(afterLabelId, true, [])));

        public Task<Result<CovenantDisclosureExposure>> ReadDisclosureExposureAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<CovenantDisclosureExposure>.Success(
                    new CovenantDisclosureExposure(0, CovenantDisclosureCountKind.Exact)));

    }

    private sealed class RouteFailureTransition : ICovenantErasureTransition
    {

        public Task<Result<Guid>> ApplyCanonicalErasureAsync(
            CovenantExclusiveOperation operation,
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<Guid>.Failure(
                    new Error(
                        ErrorCodes.Covenant.ErasureIncomplete,
                        "The direct reset transition was refused.")));

        public Task<Result> CloseHandlesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> TruncateWalAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> CompactAsync(CovenantV3CompactionCapabilities capabilities, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> InitializeAcceleratorAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> VerifySidecarAbsenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result> PublishCommittedAsync(
            ICovenantExclusiveOperationLease lease,
            CovenantVerifiedCandidateState candidate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class RouteCancellationTransition(CancellationTokenSource caller)
        : ICovenantErasureTransition
    {

        public Task<Result<Guid>> ApplyCanonicalErasureAsync(
            CovenantExclusiveOperation operation,
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<Guid>.Success(Guid.Parse("99999999-9999-4999-8999-999999999999")));

        public Task<Result> CloseHandlesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> TruncateWalAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> CompactAsync(CovenantV3CompactionCapabilities capabilities, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> InitializeAcceleratorAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> VerifySidecarAbsenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken)
        {

            caller.Cancel();

            return Task.FromResult(
                Result<CovenantVerifiedCandidateState>.Success(
                    new CovenantVerifiedCandidateState(
                        new CovenantCandidateDatasetState(
                            Guid.Parse("99999999-9999-4999-8999-999999999999"),
                            0,
                            0,
                            null,
                            null,
                            0,
                            0,
                            1,
                            CovenantFtsRebuildState.FullRebuildRequired,
                            1,
                            new byte[32],
                            1),
                        new CovenantCandidateAuthorityState(
                            "direct-route-cancellation-test",
                            1,
                            1,
                            new byte[32],
                            1,
                            CovenantHostToolsState.Clean,
                            null),
                        new CovenantCandidateCapabilityState(0, 0, false))));

        }

        public Task<Result> PublishCommittedAsync(
            ICovenantExclusiveOperationLease lease,
            CovenantVerifiedCandidateState candidate,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

    }

    private sealed class RecordingRouteGate(
        CovenantOperationGate inner,
        RouteStoreFaults? storeFaults = null) : ICovenantOperationGate
    {

        private int _installationReadAcquisitions;

        private int _liveInstallationReads;

        internal CovenantExclusiveRecoveryOwner? ExclusiveOwner { get; private set; }

        internal int InstallationReadAcquisitions => Volatile.Read(ref _installationReadAcquisitions);

        internal int InstallationReadsAtExclusiveAdmission { get; private set; }

        internal bool ReportInstallationCoverageAsReadKind { get; set; }

        internal bool ThrowOnNextInstallationRelease { get; set; }

        internal void ResetApplyObservations()
        {

            _ = Interlocked.Exchange(ref _installationReadAcquisitions, 0);

            ExclusiveOwner = null;

            InstallationReadsAtExclusiveAdmission = -1;

        }

        public async ValueTask<Result<CovenantInstallationReadLease>> AcquireInstallationReadAsync(
            CancellationToken cancellationToken)
        {

            Result<CovenantInstallationReadLease> acquired = await inner
                .AcquireInstallationReadAsync(cancellationToken);

            if (acquired.IsFailure)
            {

                return acquired;

            }

            _ = Interlocked.Increment(ref _installationReadAcquisitions);

            _ = Interlocked.Increment(ref _liveInstallationReads);

            return Result<CovenantInstallationReadLease>.Success(
                new CovenantInstallationReadLease(
                    new RecordingInstallationRegistration(this, acquired.Value)));

        }

        public async ValueTask<Result<CovenantExclusiveLease>> ResumeOrAcquireExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken)
        {

            ExclusiveOwner = owner;

            InstallationReadsAtExclusiveAdmission = Volatile.Read(ref _liveInstallationReads);

            Result<CovenantExclusiveLease> acquired = await inner
                .ResumeOrAcquireExclusiveAsync(owner, cancellationToken);

            return acquired.IsFailure || storeFaults?.Fault is not RouteStoreFault.TerminalHeartbeatRace
                ? acquired
                : Result<CovenantExclusiveLease>.Success(
                    new CovenantExclusiveLease(
                        new RecordingExclusiveRegistration(acquired.Value, storeFaults)));

        }

        public ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.AcquireExclusiveAsync(owner, cancellationToken);

        public ValueTask<Result<CovenantExclusiveLease>> ResumeExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.ResumeExclusiveAsync(owner, cancellationToken);

        public ValueTask<Result<CovenantReadLease>> AcquireReadAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) =>
            inner.AcquireReadAsync(scope, cancellationToken);

        public ValueTask<Result<CovenantWriteLease>> AcquireWriteAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) =>
            inner.AcquireWriteAsync(scope, cancellationToken);

        public ValueTask<Result<CovenantTurnLease>> AcquireTurnAsync(
            CanonicalCampaignContext campaign,
            CancellationToken cancellationToken) =>
            inner.AcquireTurnAsync(campaign, cancellationToken);

        public ValueTask<Result<CovenantMcpLease>> AcquireMcpAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) =>
            inner.AcquireMcpAsync(scope, cancellationToken);

        public ValueTask<Result<CovenantAcceleratorLease>> AcquireAcceleratorAsync(
            CancellationToken cancellationToken) =>
            inner.AcquireAcceleratorAsync(cancellationToken);

        public ValueTask<Result<CovenantCleanupLease>> AcquireCleanupAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) =>
            inner.AcquireCleanupAsync(scope, cancellationToken);

        public ValueTask<Result<CovenantCampaignExclusiveLease>> AcquireCampaignExclusiveAsync(
            Guid campaignId,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.AcquireCampaignExclusiveAsync(campaignId, owner, cancellationToken);

        public ValueTask<Result<CovenantProtectedTransferLease>> AcquireProtectedTransferAsync(
            ProtectedTransferScope scope,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.AcquireProtectedTransferAsync(scope, owner, cancellationToken);

        public ValueTask<Result<CovenantCampaignExclusiveLease>> ResumeCampaignExclusiveAsync(
            Guid campaignId,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.ResumeCampaignExclusiveAsync(campaignId, owner, cancellationToken);

        public ValueTask<Result<CovenantProtectedTransferLease>> ResumeProtectedTransferAsync(
            ProtectedTransferScope scope,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.ResumeProtectedTransferAsync(scope, owner, cancellationToken);

        private sealed class RecordingInstallationRegistration(
            RecordingRouteGate owner,
            CovenantInstallationReadLease inner) : ICovenantLeaseRegistration
        {

            private int _released;

            public CovenantOperationLeaseSnapshot Snapshot =>
                owner.ReportInstallationCoverageAsReadKind
                    ? inner.Snapshot with { Kind = CovenantLeaseKind.Read }
                    : inner.Snapshot;

            public CancellationToken Revocation => inner.Revocation;

            public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
                inner.RevalidateAsync(cancellationToken);

            public async ValueTask ReleaseAsync()
            {

                if (Interlocked.Exchange(ref _released, 1) != 0)
                {

                    return;

                }

                _ = Interlocked.Decrement(ref owner._liveInstallationReads);

                await inner.DisposeAsync();

                if (owner.ThrowOnNextInstallationRelease)
                {

                    owner.ThrowOnNextInstallationRelease = false;

                    throw new InvalidOperationException("Injected planning lease release failure.");

                }

            }

        }

        private sealed class RecordingExclusiveRegistration(
            CovenantExclusiveLease inner,
            RouteStoreFaults faults) : ICovenantExclusiveLeaseRegistration
        {

            public CovenantOperationLeaseSnapshot Snapshot => inner.Snapshot;

            public CancellationToken Revocation => inner.Revocation;

            public Result ExecuteWhileHeld(Func<Result> callback) => inner.ExecuteWhileHeld(callback);

            public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
                inner.RevalidateAsync(cancellationToken);

            public ValueTask<Result> CompleteAsync(
                CovenantExclusiveLeaseDisposition disposition,
                CancellationToken cancellationToken) =>
                inner.CompleteAsync(disposition, cancellationToken);

            public async ValueTask ReleaseAsync()
            {

                await inner.DisposeAsync();

                faults.SignalExclusiveLeaseReleased();

            }

        }

    }

    public enum RouteStoreFault
    {

        None,

        AdoptBeforeCheckpointCancellation,

        ThrowBeforeCheckpoint,

        ThrowAfterCheckpoint,

        FailFirstCompletedTransition,

        FailAllCompletedTransitions,

        TerminalHeartbeatRace,

    }

    internal sealed class RouteStoreFaults(RouteStoreFault fault)
    {

        internal const string AdoptedOwner = "review-adopted-owner";

        private int _completedTransitionAttempts;

        private int _throwNextGet;

        private readonly TaskCompletionSource _renewalStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowRenewal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _completedTransitionStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowCompletedTransition = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _completedTransitionCommitted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _exclusiveLeaseReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int CompletedTransitionAttempts => Volatile.Read(ref _completedTransitionAttempts);

        internal Func<CancellationToken, Task>? AfterFactoryStarted { get; set; }

        internal CoordinatorPause? FactoryCheckpointPause { get; init; }

        internal RouteStoreFault Fault { get; } = fault;

        internal Task CompletedTransitionStarted => _completedTransitionStarted.Task;

        internal Task ExclusiveLeaseReleased => _exclusiveLeaseReleased.Task;

        internal bool TakeThrowNextGet() => Interlocked.Exchange(ref _throwNextGet, 0) != 0;

        internal void ArmThrowNextGet() => _ = Interlocked.Exchange(ref _throwNextGet, 1);

        internal int RecordCompletedTransitionAttempt() =>
            Interlocked.Increment(ref _completedTransitionAttempts);

        internal void AllowCompletedTransition() => _allowCompletedTransition.TrySetResult();

        internal void AllowRenewal() => _allowRenewal.TrySetResult();

        internal void SignalCompletedTransitionCommitted() =>
            _completedTransitionCommitted.TrySetResult();

        internal void SignalCompletedTransitionStarted() =>
            _completedTransitionStarted.TrySetResult();

        internal void SignalExclusiveLeaseReleased() => _exclusiveLeaseReleased.TrySetResult();

        internal void SignalRenewalStarted() => _renewalStarted.TrySetResult();

        internal Task WaitForCompletedTransitionAsync(CancellationToken cancellationToken) =>
            _allowCompletedTransition.Task.WaitAsync(cancellationToken);

        internal Task WaitForRenewalAsync(CancellationToken cancellationToken) =>
            _allowRenewal.Task.WaitAsync(cancellationToken);

        internal Task WaitUntilCompletedTransitionCommittedAsync() =>
            _completedTransitionCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        internal Task WaitUntilRenewalStartedAsync() =>
            _renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    }

    private sealed class RouteOperationStore(
        LongRunningOperationStore inner,
        TimeProvider timeProvider,
        RouteStoreFaults faults) : ILongRunningOperationStore, IDisposable
    {

        public void Dispose() => inner.Dispose();

        public Task<LongRunningOperation> CreateAsync(
            LongRunningOperationCreateRequest request,
            CancellationToken cancellationToken = default) =>
            inner.CreateAsync(request, cancellationToken);

        public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(
            LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity,
            CancellationToken cancellationToken = default) =>
            inner.ResolveOrCreateAsync(request, identity, cancellationToken);

        public async Task<LongRunningOperation?> TryStartSingleFlightAsync(
            LongRunningOperationCreateRequest request,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default)
        {

            LongRunningOperation? started = await inner.TryStartSingleFlightAsync(
                request,
                ownerId,
                utcNow,
                leaseExpiresAt,
                cancellationToken);

            if (started is not null
                && string.Equals(
                    request.Kind,
                    LongRunningOperationKinds.DataRetentionFactoryReset,
                    StringComparison.Ordinal)
                && faults.AfterFactoryStarted is { } afterFactoryStarted)
            {

                await afterFactoryStarted(cancellationToken);

            }

            return started;

        }

        public Task<LongRunningOperation?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            faults.TakeThrowNextGet()
                ? throw new InvalidOperationException("Injected post-checkpoint ledger read failure.")
                : inner.GetAsync(operationId, cancellationToken);

        public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            inner.FindRequestIdentityAsync(operationId, cancellationToken);

        public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(
            Guid requestedOperationId,
            CancellationToken cancellationToken = default) =>
            inner.FindByRequestedOperationIdAsync(requestedOperationId, cancellationToken);

        public Task<IReadOnlyList<LongRunningOperation>> ListAsync(
            LongRunningOperationQuery query,
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(query, cancellationToken);

        public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(
            DateTimeOffset utcNow,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.FindExpiredAsync(utcNow, limit, cancellationToken);

        public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            inner.TryAcquireLeaseAsync(operationId, ownerId, utcNow, leaseExpiresAt, cancellationToken);

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(operationId, ownerId, utcNow, leaseExpiresAt, cancellationToken);

        public async Task<bool> RenewLeaseAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default)
        {

            if (faults.Fault is RouteStoreFault.TerminalHeartbeatRace)
            {

                faults.SignalRenewalStarted();

                await faults.WaitForRenewalAsync(cancellationToken);

            }

            return await inner.RenewLeaseAsync(
                operationId,
                ownerId,
                utcNow,
                leaseExpiresAt,
                cancellationToken);

        }

        public async Task<bool> SaveCheckpointAsync(
            Guid operationId,
            string ownerId,
            int expectedCheckpointVersion,
            int checkpointVersion,
            byte[]? checkpointPayload,
            string? checkpointReference,
            string publicSummary,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default)
        {

            if (expectedCheckpointVersion == 0
                && checkpointVersion == DataRetentionFactoryResetCheckpointV1.CurrentVersion
                && faults.FactoryCheckpointPause is { } factoryCheckpointPause)
            {

                await factoryCheckpointPause.WaitForReleaseAsync(cancellationToken);

            }

            if (expectedCheckpointVersion == 0
                && checkpointVersion == DataRetentionMutationCheckpointV3.CurrentVersion)
            {

                if (faults.Fault is RouteStoreFault.ThrowBeforeCheckpoint)
                {

                    throw new InvalidOperationException("Injected pre-checkpoint failure.");

                }

                if (faults.Fault is RouteStoreFault.AdoptBeforeCheckpointCancellation)
                {

                    LongRunningOperation current = Assert.IsType<LongRunningOperation>(
                        await inner.GetAsync(operationId, CancellationToken.None));

                    Assert.True(await inner.TryTransitionAsync(
                        operationId,
                        current.Revision,
                        ownerId,
                        LongRunningOperationState.ReconciliationRequired,
                        timeProvider.GetUtcNow(),
                        ErrorCodes.Covenant.MaintenanceFailed,
                        CancellationToken.None));

                    LongRunningOperationLeaseResult adopted = await inner.TryAcquireLeaseAsync(
                        operationId,
                        RouteStoreFaults.AdoptedOwner,
                        timeProvider.GetUtcNow(),
                        timeProvider.GetUtcNow().Add(TimeSpan.FromMinutes(5)),
                        CancellationToken.None);

                    Assert.True(adopted.Acquired);

                    throw new OperationCanceledException(cancellationToken);

                }

            }

            bool saved = await inner.SaveCheckpointAsync(
                operationId,
                ownerId,
                expectedCheckpointVersion,
                checkpointVersion,
                checkpointPayload,
                checkpointReference,
                publicSummary,
                utcNow,
                cancellationToken);

            if (saved
                && expectedCheckpointVersion == 0
                && checkpointVersion == DataRetentionMutationCheckpointV3.CurrentVersion
                && faults.Fault is RouteStoreFault.ThrowAfterCheckpoint)
            {

                faults.ArmThrowNextGet();

            }

            return saved;

        }

        public async Task<bool> TryTransitionAsync(
            Guid operationId,
            long expectedRevision,
            string? ownerId,
            LongRunningOperationState state,
            DateTimeOffset utcNow,
            string? terminalErrorCode = null,
            CancellationToken cancellationToken = default)
        {

            if (state is LongRunningOperationState.Completed
                && (faults.Fault is RouteStoreFault.FailAllCompletedTransitions
                    || faults.Fault is RouteStoreFault.FailFirstCompletedTransition
                        && faults.RecordCompletedTransitionAttempt() == 1))
            {

                if (faults.Fault is RouteStoreFault.FailAllCompletedTransitions)
                {

                    _ = faults.RecordCompletedTransitionAttempt();

                }

                return false;

            }

            if (state is LongRunningOperationState.Completed
                && faults.Fault is RouteStoreFault.TerminalHeartbeatRace)
            {

                faults.SignalCompletedTransitionStarted();

                await faults.WaitForCompletedTransitionAsync(cancellationToken);

            }

            if (state is LongRunningOperationState.Completed)
            {

                _ = faults.RecordCompletedTransitionAttempt();

            }

            bool transitioned = await inner.TryTransitionAsync(
                operationId,
                expectedRevision,
                ownerId,
                state,
                utcNow,
                terminalErrorCode,
                cancellationToken);

            if (transitioned
                && state is LongRunningOperationState.Completed
                && faults.Fault is RouteStoreFault.TerminalHeartbeatRace)
            {

                faults.SignalCompletedTransitionCommitted();

            }

            return transitioned;

        }

        public Task<bool> RequestCancellationAsync(
            Guid operationId,
            long expectedRevision,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            inner.RequestCancellationAsync(operationId, expectedRevision, utcNow, cancellationToken);

        public Task<bool> ResetForRetryAsync(
            Guid operationId,
            long expectedRevision,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            inner.ResetForRetryAsync(operationId, expectedRevision, utcNow, cancellationToken);

        public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetCountsAsync(cancellationToken);

    }

    private sealed class PausedTurn(
        Task paused,
        TaskCompletionSource<bool> release,
        Task<Result<CovenantTurnContext>> result) : IAsyncDisposable
    {

        private int _released;

        internal Task WaitUntilPausedAsync() => paused.WaitAsync(TimeSpan.FromSeconds(5));

        internal async Task<Result<CovenantTurnContext>> ReleaseAsync()
        {

            Release();

            return await result.WaitAsync(TimeSpan.FromSeconds(5));

        }

        public async ValueTask DisposeAsync()
        {

            Release();

            try
            {

                _ = await result.WaitAsync(TimeSpan.FromSeconds(5));

            }
            catch
            {

                // The owning assertion reports the task failure; disposal only guarantees release.

            }

        }

        private void Release()
        {

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {

                _ = release.TrySetResult(true);

            }

        }

    }

    private sealed record SameProcessBefore(
        Guid DatasetGeneration,
        CovenantRuntimeGenerationProvider Runtime,
        CovenantEnvelopeMasterKeyProvider Root,
        CovenantDisclosureWriter Writer,
        IReadOnlyDictionary<CovenantEnvelopePurpose, string> Tokens,
        OperatorAuthorityContext OperatorContext,
        CovenantReadAuthorityEpoch ReadEpoch,
        CovenantReadLease ReadLease,
        ArcanumInvocationContext OldInvocation,
        string OldContent);

}
