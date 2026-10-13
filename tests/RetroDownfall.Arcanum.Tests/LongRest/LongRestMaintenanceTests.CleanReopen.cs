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
    [InlineData("repeated", true)]
    [InlineData("broken-lineage", false)]
    [InlineData("undrained", false)]
    [InlineData("configuration", false)]
    [InlineData("capability", false)]
    [InlineData("authority", false)]
    [InlineData("protected", false)]
    public async Task Clean_reopen_registered_recovery_requires_drained_abort_and_unchanged_clean_authority(
        string change, bool allowed)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: change == "protected");

        LongRestDiscoveryGeneration original = LongRestSweepHarness.Require(
            await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        PaidReopenStage retained = await RetainPaidSummaryAcrossClosingAsync(f, drained: change != "undrained");

        if (change == "repeated")
        {
            await AdditionalReopenEdgeAsync(f, drained: true);

            Assert.Equal(original.Fence.AdmissionGeneration + 2, f.Admission.CurrentGeneration);
        }
        else if (change == "broken-lineage")
        {
            await AdditionalReopenEdgeAsync(f, drained: false);

            await AdditionalReopenEdgeAsync(f, drained: true);

            Assert.Equal(original.Fence.AdmissionGeneration + 3, f.Admission.CurrentGeneration);
        }

        Assert.Equal(original.Fence.Configuration, f.Epoch.Capture().Stamp);

        Assert.True(f.Admission.CurrentGeneration > original.Fence.AdmissionGeneration);

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 3;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 4 AND CompletionJson IS NOT NULL;"));

        LongRunningOperation deferred = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Running, deferred.State);

        Assert.Null(deferred.TerminalErrorCode);

        Assert.NotNull(deferred.LeaseOwner);

        Assert.Equal(1, deferred.CheckpointVersion);

        if (change is "configuration" or "capability" or "authority") await MutateAsync(f, change);

        Assert.Equal(original.Fence.Configuration.BootId, f.Epoch.Capture().Stamp.BootId);

        if (change == "configuration")
        {
            Assert.Equal(original.Fence.Configuration.ValueDigest, f.Epoch.Capture().Stamp.ValueDigest);

            Assert.NotEqual(original.Fence.Configuration, f.Epoch.Capture().Stamp);
        }

        // Only the durable lease deadline is advanced after the real old worker and group drained.
        // The same process, gate, epoch, runtime, stores and registered recovery handler remain alive.
        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation;",
            ("$operation", f.Operation.ToString("N")));

        f.Payloads.Clear();

        int leasesBefore = f.RecordedAdmission.InnerWorkLeases.Count;

        string? actualRecoveryOwner = null;

        f.Handler.InsideSend = async () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            Assert.Equal(f.Admission.CurrentGeneration, f.WorkAdmission.Lease.Generation);

            Assert.False(f.WorkAdmission.Lease.MaintenanceRevocation.IsCancellationRequested);

            actualRecoveryOwner = (await f.Operations.GetAsync(f.Operation))!.LeaseOwner;

            Assert.NotNull(actualRecoveryOwner);

            Assert.NotEqual(deferred.LeaseOwner, actualRecoveryOwner);
        };

        LongRunningOperationStartupHostedService recovery = new(new PaidRecoveryScopes(f), TimeProvider.System,
            new LongRunningOperationReconciliationStatus(), f.RecordedAdmission,
            NullLogger<LongRunningOperationStartupHostedService>.Instance);

        LongRunningOperationReconciliationSummary summary;

        try
        {
            summary = await recovery.RunBackgroundPassAsync(DateTimeOffset.UtcNow, "same-boot-clean-reopen", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await recovery.StopAsync(CancellationToken.None);
        }

        Assert.Contains(f.RecordedAdmission.InnerWorkLeases.Skip(leasesBefore), lease =>
            lease.Kind == GrimoireWorkKind.LongRunningOperationRecovery && lease.Generation == f.Admission.CurrentGeneration);

        Assert.Equal(retained.Evidence, await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"));

        Assert.Equal(retained.WorkDigest, await f.H.ScalarAsync("SELECT hex(WorkDigest) FROM long_rest_accepted_summaries;"));

        LongRunningOperation final = (await f.Operations.GetAsync(f.Operation))!;

        LongRestDiscoveryGeneration current = LongRestSweepHarness.Require(
            await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        if (allowed)
        {
            Assert.Equal(LongRunningOperationState.Completed, final.State);

            Assert.Null(final.TerminalErrorCode);

            Assert.Equal(1, summary.Completed);

            Assert.Equal(0, summary.RequiresAttention);

            Assert.Equal("same-boot-clean-reopen", actualRecoveryOwner);

            Assert.NotEmpty(f.Payloads);

            Assert.Equal(LongRestProviderPayloadKind.Embedding, f.Payloads[0].Kind);

            Assert.DoesNotContain(f.Payloads, payload => payload.Kind == LongRestProviderPayloadKind.Summary);

            Assert.Equal(original.Fence with { AdmissionGeneration = f.Admission.CurrentGeneration }, current.Fence);

            Assert.Equal(original.Recipe, current.Recipe);

            Assert.Equal(original.GenerationId, current.GenerationId);

            Assert.Equal(LongRestSweepPhase.Completed, current.Phase);

            Assert.Equal((long)f.Handler.SendCount, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

            Assert.Equal((long)f.Handler.SendCount, await f.H.ScalarAsync("SELECT sum(PaidCount) FROM long_rest_provider_effects;"));
        }
        else
        {
            Assert.NotEqual(LongRunningOperationState.Completed, final.State);

            Assert.Equal(0, summary.Completed);

            Assert.Equal(1, f.Handler.SendCount);

            Assert.Empty(f.Payloads);

            Assert.Equal(original.Fence, current.Fence);

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));

            Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

            Assert.Equal(1L, await f.H.ScalarAsync("SELECT sum(PaidCount) FROM long_rest_provider_effects;"));
        }
    }

    private static async Task<PaidReopenStage> RetainPaidSummaryAcrossClosingAsync(MaintenanceFixture f, bool drained)
    {
        using CancellationTokenSource stopping = new();

        using LongRestSweepHostedService host = PaidOwnershipHost(f);

        TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IGrimoireClosingOwner? closing = null;

        PaidReopenStage? retained = null;

        bool reopened = false;

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        f.RecordedAdmission.BeforeEffectGroupDisposalAsync = async () =>
        {
            if (f.Handler.SendCount != 1) return;

            retained = new((string)(await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"))!,
                (string)(await f.H.ScalarAsync("SELECT hex(WorkDigest) FROM long_rest_accepted_summaries;"))!);

            closing = f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()).Value;

            committed.TrySetResult();

            await release.Task;
        };

        LongRestMaintenanceCapability? capability = f.Partition.ProtectedPartitionDigest is null ? null
            : LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(f.Operation,
                f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None));

        Task<Exception?> run = Record.ExceptionAsync(() => capability is null
            ? host.RunOneAsync(f.Operation, stopping.Token)
            : f.Coordinator.ResumeAsync(f.Operation, capability, stopping.Token));

        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(closing);

            Assert.NotNull(retained);

            Assert.False(run.IsCompleted);

            Assert.False(f.WorkAdmission.Lease.MaintenanceRevocation.IsCancellationRequested);

            if (drained)
            {
                Task<Result> drain = f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

                Assert.False(drain.IsCompleted);

                release.TrySetResult();

                Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
            }
            else
            {
                // Abort is legal before drain, but must never attest completion of this still-held group.
                Assert.True((await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

                reopened = true;
            }

            await stopping.CancelAsync();

            release.TrySetResult();

            Exception? stopped = await run.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(stopped is null or OperationCanceledException, stopped?.ToString());

            if (!reopened)
            {
                Assert.True((await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

                reopened = true;
            }

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));

            return retained ?? throw new InvalidOperationException("No accepted summary was observed.");
        }
        finally
        {
            await stopping.CancelAsync();

            release.TrySetResult();

            await run.WaitAsync(TimeSpan.FromSeconds(10));

            f.RecordedAdmission.BeforeEffectGroupDisposalAsync = () => ValueTask.CompletedTask;

            if (closing is not null)
            {
                if (!reopened) await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None);

                await closing.DisposeAsync();
            }
        }
    }

    private static async Task AdditionalReopenEdgeAsync(MaintenanceFixture f, bool drained)
    {
        IGrimoireWorkLease? lease = null;

        IGrimoireExternalEffectGroup? group = null;

        IGrimoireClosingOwner? closing = null;

        bool reopened = false;

        long prior = f.Admission.CurrentGeneration;

        try
        {
            Assert.True(f.Admission.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out lease));

            Assert.NotNull(lease);

            Assert.Equal(prior, lease.Generation);

            Assert.True(lease.TryBeginExternalEffectGroup(out group));

            Assert.NotNull(group);

            closing = f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()).Value;

            Assert.False(lease.MaintenanceRevocation.IsCancellationRequested);

            if (drained)
            {
                Task<Result> drain = f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

                Assert.False(drain.IsCompleted);

                await group.DisposeAsync();

                group = null;

                await lease.DisposeAsync();

                lease = null;

                Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
            }

            // The false branch aborts with a real live group. Finishing it afterward must not
            // retroactively make this edge safe, nor may a later safe edge bridge across it.
            Assert.True((await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

            reopened = true;

            Assert.Equal(prior + 1, f.Admission.CurrentGeneration);
        }
        finally
        {
            if (group is not null) await group.DisposeAsync();

            if (lease is not null) await lease.DisposeAsync();

            if (closing is not null)
            {
                if (!reopened) await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None);

                await closing.DisposeAsync();
            }
        }
    }

    private sealed record PaidReopenStage(string Evidence, string WorkDigest);
}
