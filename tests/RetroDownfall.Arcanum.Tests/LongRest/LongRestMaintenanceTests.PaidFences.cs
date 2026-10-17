using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Fact]
    public async Task Paid_fence_direct_reconcile_while_admission_closes_still_parks_without_entering_the_coordinator()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z';");

        using CancellationTokenSource caller = new();

        IGrimoireClosingOwner? closing = null;

        LongRunningOperation? acquired = null;

        PaidFenceRecoveryAcquisition acquisition = new(f.Operations, operation =>
        {
            acquired = operation;

            // Real classified acquisition is committed and admission then closes while the caller stays active. The
            // registered handler is pure, so it neither acquires ordinary work nor depends on the gate: it parks.
            closing = LongRestSweepHarness.Require(f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()));
        });

        LongRunningOperationReconciler reconciler = new(f.Operations, [new LongRestSweepRecoveryHandler()],
            TimeProvider.System, NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership(),
            f.Operations, acquisition, scopeFactory: null);

        try
        {
            LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync(
                "fixture-direct-paid-recovery", maxOperations: 1, maxConcurrency: 1, cancellationToken: caller.Token);

            Assert.False(caller.IsCancellationRequested);

            Assert.Equal(1, summary.RequiresAttention);

            Assert.NotNull(acquired);

            Assert.Equal(LongRunningOperationState.Running, acquired.State);

            Assert.Equal("fixture-direct-paid-recovery", acquired.LeaseOwner);

            LongRunningOperation latest = (await f.Operations.GetAsync(f.Operation))!;

            Assert.Equal(LongRunningOperationState.ReconciliationRequired, latest.State);

            Assert.Equal(LongRestParkCodes.Interrupted, latest.TerminalErrorCode);

            Assert.NotEqual(LongRunningOperationErrorCodes.RecoveryFailed, latest.TerminalErrorCode);

            Assert.Null(latest.LeaseOwner);

            Assert.Equal(acquired.CheckpointPayload, latest.CheckpointPayload);

            Assert.Equal(0, f.Handler.SendCount);

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects;"));
        }
        finally
        {
            if (closing is not null) await closing.DisposeAsync();
        }
    }

    [Fact]
    public async Task Paid_fence_closing_before_revocation_notification_refuses_send_without_terminal_reconciliation()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        Assert.True(f.Admission.TryAcquireRequestLease(GrimoireRequestKind.QuiesceableStream, out IGrimoireRequestLease? request));

        await using IGrimoireRequestLease earlier = request!;

        using ManualResetEventSlim allowNotification = new();

        TaskCompletionSource closingLinearized = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenRegistration blocked = earlier.MaintenanceRevocation.Register(() =>
        {
            // The gate revokes quiesceable requests before work leases, outside its lock.
            // Hold this callback so Closing is real while the paid lease is still unnotified.
            closingLinearized.TrySetResult();

            if (!allowNotification.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture revocation barrier did not release.");
        });

        Task<IGrimoireClosingOwner>? close = null;

        bool paidLeaseUnnotified = false;

        f.RecordedAdmission.BeforeEffectGroupAdmission = () =>
        {
            close = Task.Run(() => LongRestSweepHarness.Require(f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner())));

            closingLinearized.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

            paidLeaseUnnotified = !f.SweepLease.MaintenanceRevocation.IsCancellationRequested;

            // Return to the recording wrapper's REAL inner effect-group CAS while Closing.
        };

        try
        {
            Exception? failure = await Record.ExceptionAsync(() => f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(failure is null or OperationCanceledException);

            Assert.True(paidLeaseUnnotified);

            Assert.Equal(1, f.RecordedAdmission.EffectGroupAttempts);

            Assert.Equal(0, f.Handler.SendCount);

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects;"));

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

            LongRunningOperation latest = (await f.Operations.GetAsync(f.Operation))!;

            // Maintenance refused the send, so the worker yielded: a park under the free yield code, never a terminal failure.
            Assert.Equal(LongRunningOperationState.ReconciliationRequired, latest.State);

            Assert.Equal(LongRestParkCodes.MaintenanceDeferred, latest.TerminalErrorCode);
        }
        finally
        {
            allowNotification.Set();

            if (close is not null)
            {
                IGrimoireClosingOwner closing = await close.WaitAsync(TimeSpan.FromSeconds(10));

                try
                {
                    await earlier.DisposeAsync();

                    Assert.True((await f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None)
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
                }
                finally
                {
                    await closing.DisposeAsync();
                }
            }
        }
    }

    [Theory]
    [InlineData(LongRestProviderPayloadKind.Summary, "cancel", "long_rest_accepted_summaries", 1, 1, 17L, 3L, 20L)]
    [InlineData(LongRestProviderPayloadKind.Embedding, "owner", "long_rest_discovery_embeddings", 1, 2, 34L, 3L, 37L)]
    [InlineData(LongRestProviderPayloadKind.Adjudication, "expiry", "long_rest_adjudications", 3, 3, 51L, 6L, 57L)]
    public async Task Paid_fence_worker_loss_after_final_validation_prevents_atomic_acceptance(
        LongRestProviderPayloadKind kind, string mutation, string artifact, int precedingUnits, int expectedSends,
        long expectedInput, long expectedOutput, long expectedTotal)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        for (int unit = 0; unit < precedingUnits; unit++)
        {
            LongRestSweepHarness.Require(await f.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));
        }

        int loss = 0;

        object? foldCursor = null;

        object? sweepCursor = null;

        object? sweepWork = null;

        f.AfterAttemptValidated = async (attempt, token) =>
        {
            if (attempt.PayloadKind != kind || attempt.State != LongRestDispatchAttemptState.Sent
                || f.Handler.SendCount != expectedSends || Interlocked.Exchange(ref loss, 1) != 0) return;

            // After REAL post-return EnsureCurrent + exact attempt validation; before the
            // owning Accepted/artifact transaction. No provider or accounting result is replaced.
            Assert.False(token.IsCancellationRequested);

            Assert.Equal((long)expectedSends, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

            foldCursor = await f.H.ScalarAsync("SELECT FoldGroup FROM long_rest_discovery_generations;");

            sweepCursor = await f.H.ScalarAsync("SELECT CommittedItemCursor FROM long_rest_sweeps;");

            sweepWork = await f.H.ScalarAsync("SELECT hex(CommittedWorkDigest) FROM long_rest_sweeps;");

            await LosePaidWorkerAsync(f, mutation, token);
        };

        Result<LongRestSweepProgress>? result = null;

        Exception? failure = await Record.ExceptionAsync(async () =>
            result = await f.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        Assert.True(failure is null or OperationCanceledException);

        Assert.Equal(1, loss);

        Assert.True(failure is OperationCanceledException || result is { IsFailure: true });

        Assert.Equal(expectedSends, f.Handler.SendCount);

        Assert.Equal((long)expectedSends, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync($"SELECT count(*) FROM {artifact};"));

        Assert.Equal(foldCursor, await f.H.ScalarAsync("SELECT FoldGroup FROM long_rest_discovery_generations;"));

        Assert.Equal(sweepCursor, await f.H.ScalarAsync("SELECT CommittedItemCursor FROM long_rest_sweeps;"));

        Assert.Equal(sweepWork, await f.H.ScalarAsync("SELECT hex(CommittedWorkDigest) FROM long_rest_sweeps;"));

        Assert.Equal((long)expectedSends, await f.H.ScalarAsync("SELECT sum(PaidCount) FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT sum(InvalidCount) FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 5 AND CompletionJson IS NOT NULL;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 3;"));

        Assert.Equal(expectedInput, await f.H.ScalarAsync("SELECT sum(InputTokens) FROM long_rest_provider_effects;"));

        Assert.Equal(expectedOutput, await f.H.ScalarAsync("SELECT sum(OutputTokens) FROM long_rest_provider_effects;"));

        Assert.Equal(expectedTotal, await f.H.ScalarAsync("SELECT sum(TotalTokens) FROM long_rest_provider_effects;"));

        if (kind == LongRestProviderPayloadKind.Embedding)
        {
            Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer > 0;"));
        }

        if (kind == LongRestProviderPayloadKind.Adjudication)
        {
            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2;"));
        }
    }

    private static async Task LosePaidWorkerAsync(MaintenanceFixture fixture, string mutation, CancellationToken ct)
    {
        Assert.True(fixture.Admission.TryAcquireWorkLease(GrimoireWorkKind.LongRunningOperationRecovery, out IGrimoireWorkLease? admitted));

        await using IGrimoireWorkLease owner = admitted!;

        // The connection string belongs to this encrypted temporary fixture, never an installation.
        // Native SQLCipher was initialized by the real fixture before its first connection opened.
        await using SqliteConnection sibling = new(fixture.H.Connection.ConnectionString);

        using IGrimoireConnectionOpenTicket opening = fixture.Admission.AcquireOrdinaryOpen(sibling);

        await sibling.OpenAsync(ct);

        Assert.True(opening.RevalidateAfterNativeOpen().IsSuccess);

        Assert.True(opening.MarkOpened().IsSuccess);

        await using SqliteCommand command = sibling.CreateCommand();

        command.CommandText = mutation switch
        {
            "cancel" => "UPDATE LongRunningOperations SET State = 3, Revision = Revision + 1 WHERE Id = $id AND State = 1 AND LeaseOwner = 'fixture-worker';",
            "owner" => "UPDATE LongRunningOperations SET LeaseOwner = 'replacement-worker', Revision = Revision + 1 WHERE Id = $id AND State = 1 AND LeaseOwner = 'fixture-worker';",
            "expiry" => "UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z', Revision = Revision + 1 WHERE Id = $id AND State = 1 AND LeaseOwner = 'fixture-worker';",
            _ => throw new InvalidOperationException("Unknown fixture worker mutation."),
        };

        command.Parameters.AddWithValue("$id", fixture.Operation.ToString("N"));

        Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
    }

    private sealed class PaidFenceRecoveryAcquisition(ILongRunningOperationClassifiedRecoveryLeaseAcquisition inner,
        Action<LongRunningOperation> acquired) : ILongRunningOperationClassifiedRecoveryLeaseAcquisition
    {
        public async Task<LongRunningOperationLeaseResult> TryAcquireClassifiedRecoveryLeaseAsync(
            LongRunningOperationRecoveryFingerprint expected, string ownerId, DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken = default)
        {
            LongRunningOperationLeaseResult result = await inner.TryAcquireClassifiedRecoveryLeaseAsync(
                expected, ownerId, utcNow, leaseExpiresAt, cancellationToken);

            if (result.Acquired) acquired(result.Operation);

            return result;
        }
    }

    private sealed class PaidFenceAttemptStore(ILongRestAttemptStore inner,
        Func<LongRestDispatchAttempt, CancellationToken, Task> validated,
        Func<LongRestDispatchAttempt, LongRestUnresolvedSend, CancellationToken, Task<Result<LongRestDispatchAttempt>>?>? settle = null) : ILongRestAttemptStore
    {
        public Task<Result<SessionTurnClaimLease>> RenewClaimAsync(SessionTurnClaimLease claim, CanonicalCampaignContext scope, CancellationToken ct) => inner.RenewClaimAsync(claim, scope, ct);

        public Task<Result<LongRestDispatchAttempt>> PrepareAttemptAsync(LongRestWorkIdentity work, LongRestProviderPayloadKind kind, CovenantDigest payload, CovenantDigest destination, CancellationToken ct) => inner.PrepareAttemptAsync(work, kind, payload, destination, ct);

        public Task<Result<LongRestDispatchAttempt>> RecordDisclosureAsync(LongRestDispatchAttempt attempt, CovenantDigest receipt, CancellationToken ct) => inner.RecordDisclosureAsync(attempt, receipt, ct);

        public Task<Result<LongRestDispatchAttempt>> MarkSentAsync(LongRestDispatchAttempt attempt, CancellationToken ct) => inner.MarkSentAsync(attempt, ct);

        public Task<Result<LongRestDispatchAttempt>> CompleteAttemptAsync(LongRestDispatchAttempt attempt, LongRestDispatchCompletion completion, CancellationToken ct) => inner.CompleteAttemptAsync(attempt, completion, ct);

        public Task<Result<LongRestDispatchAttempt>> SettleUnresolvedSendAsync(LongRestDispatchAttempt attempt, LongRestUnresolvedSend outcome, CancellationToken ct) =>
            settle?.Invoke(attempt, outcome, ct) ?? inner.SettleUnresolvedSendAsync(attempt, outcome, ct);

        public Task<Result> ReleaseUnsentAttemptAsync(LongRestDispatchAttempt attempt, CancellationToken ct) => inner.ReleaseUnsentAttemptAsync(attempt, ct);

        public async Task<Result> ValidateAttemptAsync(LongRestDispatchAttempt attempt, CancellationToken ct)
        {
            Result result = await inner.ValidateAttemptAsync(attempt, ct);

            if (result.IsSuccess) await validated(attempt, ct);

            return result;
        }
    }
}
