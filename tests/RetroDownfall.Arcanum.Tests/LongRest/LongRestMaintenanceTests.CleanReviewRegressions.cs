using System.Globalization;
using System.Net;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData(LongRestDispatchAttemptState.Invalid)]
    [InlineData(LongRestDispatchAttemptState.Failed)]
    public async Task Clean_review_terminal_effect_survives_two_adoptions_before_another_Prepare(LongRestDispatchAttemptState state)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        LongRestDispatchAttempt? sentEmbedding = null;

        f.AfterAttemptValidated = (attempt, _) =>
        {
            if (attempt.PayloadKind == LongRestProviderPayloadKind.Embedding && attempt.State == LongRestDispatchAttemptState.Sent)
            {
                sentEmbedding = attempt;
            }

            return Task.CompletedTask;
        };

        f.Handler.InsideSend = () =>
        {
            if (f.Handler.SendCount == 2)
            {
                if (state == LongRestDispatchAttemptState.Invalid) f.Handler.InvalidVector = "dimensions";
                else f.Handler.Status = HttpStatusCode.BadRequest;
            }

            return Task.CompletedTask;
        };

        LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        Assert.True((await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None)).IsFailure);

        Assert.Equal(2, f.Handler.SendCount);

        Assert.NotNull(sentEmbedding);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

        if (state == LongRestDispatchAttemptState.Failed)
        {
            // The real SDK observed HTTP 400. Finish that exact known response through the
            // existing typed store, as ProviderDispatchTests does; never settle unknown transport.
            Assert.NotNull(f.ProviderResult);

            Assert.True(f.ProviderResult!.IsFailure);

            Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 3;"));

            LongRestSweepHarness.Require(await new LongRestAttemptStore(f.H.Saga.Context, f.Clock).CompleteAttemptAsync(sentEmbedding!,
                new(state, new(0, 0, 0, 0, 0), null, "LongRest.TransportFailure"), CancellationToken.None));
        }

        f.AfterAttemptValidated = null;

        Assert.Equal((long)state, await f.H.ScalarAsync("SELECT StateCode FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode IN (1, 2, 3);"));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT SentCount FROM long_rest_provider_effects;"));

        Assert.Equal(state == LongRestDispatchAttemptState.Invalid ? 2L : 1L,
            await f.H.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));

        Assert.Equal(state == LongRestDispatchAttemptState.Invalid ? 1L : 0L,
            await f.H.ScalarAsync("SELECT InvalidCount FROM long_rest_provider_effects;"));

        Assert.Equal(state == LongRestDispatchAttemptState.Failed ? 1L : 0L,
            await f.H.ScalarAsync("SELECT FailureCount FROM long_rest_provider_effects;"));

        Assert.Equal(state == LongRestDispatchAttemptState.Invalid ? 2L : 1L,
            await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));

        await CleanReviewSaveCurrentCheckpointAsync(f, "fixture-worker");

        LongRestDiscoveryGeneration previous = LongRestSweepHarness.Require(
            await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        LongRestWorkIdentity originalWork = sentEmbedding!.Work;

        string immutable = await CleanReviewImmutableAsync(f);

        byte[]? checkpointBefore = (await f.Operations.GetAsync(f.Operation))!.CheckpointPayload;

        await AdditionalReopenEdgeAsync(f, drained: true);

        CleanReviewMaintenance handoff = new(f, f.WorkAdmission, closeAtHandoff: true);

        LongRestSweepCoordinator coordinator = CleanReviewCoordinator(f, f.WorkAdmission, handoff);

        try
        {
            for (int adoption = 1; adoption <= 2; adoption++)
            {
                long admitted = f.Admission.CurrentGeneration;

                long originalPrevious = previous.Fence.AdmissionGeneration;

                int attestationsBefore = f.RecordedAdmission.DrainedAttestations.Count;

                await CleanReviewRegisteredPassAsync(f, coordinator, f.WorkAdmission, f.RecordedAdmission,
                    $"clean-review-adoption-{adoption}");

                // The pre-check plus the three store validations (two attestations each) all attest the
                // interval from the ORIGINAL previous admission to the admitted one. The third validation runs on
                // the already adopted fence, which no longer carries the original previous admission.
                RecordingGrimoireWorkAdmissionGate.DrainedAttestation[] attested = f.RecordedAdmission.DrainedAttestations.Skip(attestationsBefore).ToArray();

                Assert.True(attested.Length >= 7, $"expected the pre-check and three validations, saw {attested.Length} attestations");

                Assert.All(attested, attestation =>
                {
                    Assert.True(attestation.Attested);

                    Assert.Equal(GrimoireWorkKind.LongRunningOperationRecovery, attestation.Kind);

                    Assert.Equal(originalPrevious, attestation.Previous);

                    Assert.Equal(admitted, attestation.Current);
                });

                // Baseline compound liveness RED stops here on its first adoption. Only after
                // that guard passes can the second iteration qualify the retained terminal edge.
                Assert.Equal(adoption, handoff.Calls);

                Assert.NotNull(handoff.Closing);

                LongRestDiscoveryGeneration current = LongRestSweepHarness.Require(
                    await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

                Assert.Equal(previous with { Fence = previous.Fence with { AdmissionGeneration = admitted }, Revision = previous.Revision + 1 }, current);

                Assert.Equal(Convert.ToHexString(LongRestDiscoveryHash.Work(originalWork with { Fence = current.Fence }).Value.Bytes),
                    await f.H.ScalarAsync("SELECT hex(WorkDigest) FROM long_rest_work_items WHERE KindCode = 2;"));

                Assert.Equal(Convert.ToHexString(LongRestDiscoveryHash.Work(originalWork).Value.Bytes),
                    await f.H.ScalarAsync("SELECT hex(WorkDigest) FROM long_rest_provider_effects;"));

                Assert.Equal(immutable, await CleanReviewImmutableAsync(f));

                Assert.Equal(2, f.Handler.SendCount);

                Assert.Equal(2, f.Payloads.Count);

                LongRunningOperation operation = (await f.Operations.GetAsync(f.Operation))!;

                Assert.Equal(LongRunningOperationState.Running, operation.State);

                Assert.Null(operation.TerminalErrorCode);

                Assert.Equal($"clean-review-adoption-{adoption}", operation.LeaseOwner);

                Assert.Equal(checkpointBefore, operation.CheckpointPayload);

                Assert.Equal(previous.Fence.Configuration, f.Epoch.Capture().Stamp);

                previous = current;

                // The first handoff creates the second genuinely drained abort. The final
                // handoff is drained/reopened only for cleanup after its assertions have run.
                await CleanReviewFinishClosingAsync(f, handoff);
            }
        }
        finally
        {
            await CleanReviewFinishClosingAsync(f, handoff);
        }
    }

    [Fact]
    public async Task Clean_review_foreign_real_gate_cannot_adopt_actual_gate_unsafe_debt_with_equal_generation()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        PaidReopenStage paid = await RetainPaidSummaryAcrossClosingAsync(f, drained: false);

        LongRestDiscoveryGeneration previous = LongRestSweepHarness.Require(
            await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        GrimoireConnectionAdmissionGate foreign = new(TimeProvider.System);

        await CleanReviewSafeForeignEdgeAsync(foreign);

        Assert.Equal(previous.Fence.AdmissionGeneration + 1, f.Admission.CurrentGeneration);

        Assert.Equal(f.Admission.CurrentGeneration, foreign.CurrentGeneration);

        RecordingGrimoireWorkAdmissionGate foreignRecorded = new(foreign);

        LongRestWorkAdmission foreignWork = new(foreignRecorded);

        CleanReviewMaintenance handoff = new(f, foreignWork, closeAtHandoff: false);

        // The independent facts and store authority stay on gate A; only the lease comes from gate B.
        RecordingGrimoireWorkAdmissionGate factsGateA = new(f.Admission);

        LongRestSweepCoordinator coordinator = CleanReviewCoordinator(f, foreignWork, handoff, factsGateA);

        string immutable = await CleanReviewImmutableAsync(f);

        object? generation = await f.H.ScalarAsync(CleanGenerationSnapshotSql);

        object? units = await f.H.ScalarAsync(CleanUnitSnapshotSql);

        byte[]? checkpoint = (await f.Operations.GetAsync(f.Operation))!.CheckpointPayload;

        await CleanReviewRegisteredPassAsync(f, coordinator, foreignWork, foreignRecorded, "clean-review-foreign-gate");

        // The actual coordinator presents whatever fresh B lease it was handed, while the independent
        // facts/store authority stays gate A. Gate A is consulted and refuses at the pre-check; the
        // clean store is never reached, so nothing below may have moved.
        Assert.Contains(foreignRecorded.InnerWorkLeases, lease =>
            lease.Kind == GrimoireWorkKind.LongRunningOperationRecovery && lease.Generation == f.Admission.CurrentGeneration);

        Assert.NotEmpty(factsGateA.DrainedAttestations);

        Assert.All(factsGateA.DrainedAttestations, attestation => Assert.False(attestation.Attested));

        Assert.Equal(0, handoff.Calls);

        Assert.Equal(generation, await f.H.ScalarAsync(CleanGenerationSnapshotSql));

        Assert.Equal(units, await f.H.ScalarAsync(CleanUnitSnapshotSql));

        Assert.Equal(immutable, await CleanReviewImmutableAsync(f));

        Assert.Equal(checkpoint, (await f.Operations.GetAsync(f.Operation))!.CheckpointPayload);

        Assert.Equal(previous, LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None)));

        Assert.Equal(paid.Evidence, await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"));

        Assert.Equal(paid.WorkDigest, await f.H.ScalarAsync("SELECT hex(WorkDigest) FROM long_rest_accepted_summaries;"));

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode IN (1, 2, 3);"));
    }

    // The facts reader attests through the same gate object that minted the coordinator's lease, as the
    // production singleton does. Only the foreign-gate case deliberately points the facts at another gate.
    private static LongRestLiveFenceReader CleanReviewFacts(MaintenanceFixture f, IGrimoireConnectionAdmissionGate? admission = null)
    {
        FixedCovenantConnectionSource connections = new(f.H.Connection);

        return new(f.H.Saga.Context, f.Runtime, admission ?? f.RecordedAdmission, f.Epoch,
            new CampaignAvailabilityReader(connections), new CampaignPathIdentityReader(connections, null!));
    }

    private static LongRestSweepCoordinator CleanReviewCoordinator(MaintenanceFixture f, LongRestWorkAdmission work, ILongRestMaintenance handoff,
        IGrimoireConnectionAdmissionGate? factsAdmission = null) =>
        new(f.Operations, new LongRestSweepStore(f.H.Saga.Context, f.Clock), f.H.Store, f.Epoch, CleanReviewFacts(f, factsAdmission), handoff,
            f.Gate, new LongRunningOperationOwnership(), new LongRestSweepQueue(), f.Services.GetRequiredService<IServiceScopeFactory>(), f.Clock, work);

    private static async Task CleanReviewRegisteredPassAsync(MaintenanceFixture f, LongRestSweepCoordinator coordinator,
        LongRestWorkAdmission work, IGrimoireConnectionAdmissionGate gate, string owner)
    {
        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation;",
            ("$operation", f.Operation.ToString("N")));

        LongRunningOperationStartupHostedService recovery = new(new CleanReviewRecoveryScopes(f, coordinator, work), f.Clock,
            new LongRunningOperationReconciliationStatus(), gate, NullLogger<LongRunningOperationStartupHostedService>.Instance);

        try
        {
            Exception? stopped = await Record.ExceptionAsync(() => recovery.RunBackgroundPassAsync(
                f.Clock.GetUtcNow(), owner, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.True(stopped is null or OperationCanceledException, stopped?.ToString());
        }
        finally
        {
            await recovery.StopAsync(CancellationToken.None);
        }
    }

    private static async Task CleanReviewSaveCurrentCheckpointAsync(MaintenanceFixture f, string owner)
    {
        LongRestSweep sweep = LongRestSweepHarness.Require(await new LongRestSweepStore(f.H.Saga.Context, f.Clock)
            .BeginOrResumeAsync(f.Operation, f.Partition, f.Epoch.Capture().Stamp, CancellationToken.None));

        LongRunningOperation operation = (await f.Operations.GetAsync(f.Operation))!;

        LongRestSweepCheckpoint checkpoint = new(1, f.Operation, f.Partition.Kind, f.Partition.CampaignId,
            sweep.GenerationId, sweep.Phase, sweep.Revision, sweep.CommittedItemCursor, sweep.CommittedWorkDigest, null);

        Assert.True(await f.Operations.SaveCheckpointAsync(f.Operation, owner, operation.CheckpointVersion, 1,
            LongRestSweepHarness.Require(LongRestSweepCheckpointCodec.Encode(checkpoint)), null, "Long Rest exact review checkpoint.", f.Clock.GetUtcNow()));
    }

    private static async Task CleanReviewFinishClosingAsync(MaintenanceFixture f, CleanReviewMaintenance handoff)
    {
        if (handoff.Closing is not { } closing) return;

        Assert.True((await f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Assert.True((await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

        await closing.DisposeAsync();

        handoff.Closing = null;
    }

    private static async Task CleanReviewSafeForeignEdgeAsync(GrimoireConnectionAdmissionGate gate)
    {
        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? lease));

        IGrimoireWorkLease admitted = lease!;

        Assert.True(admitted.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group));

        await using IGrimoireClosingOwner closing = LongRestSweepHarness.Require(gate.BeginOrResumeExclusive(PaidOwnershipClosingOwner()));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        Assert.False(drain.IsCompleted);

        await group!.DisposeAsync();

        await admitted.DisposeAsync();

        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Assert.True((await gate.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);
    }

    private static async Task<string> CleanReviewImmutableAsync(MaintenanceFixture f)
    {
        string[] queries =
        [
            "SELECT * FROM long_rest_provider_effects ORDER BY WorkItemId;",
            "SELECT * FROM long_rest_accepted_summaries ORDER BY WorkItemId;",
            "SELECT * FROM BillableOperations ORDER BY Id;",
            "SELECT * FROM long_rest_sweeps ORDER BY OperationId;",
            "SELECT * FROM long_rest_work_inputs ORDER BY WorkItemId, Ordinal;",
            "SELECT * FROM long_rest_discovery_nodes ORDER BY NodeId;",
            "SELECT * FROM long_rest_discovery_embeddings ORDER BY NodeId;",
            "SELECT * FROM long_rest_adjudications ORDER BY WorkItemId;",
            "SELECT * FROM long_rest_receipts ORDER BY ReceiptId;",
        ];

        StringBuilder snapshot = new();

        foreach (string sql in queries)
        {
            snapshot.AppendLine(sql);

            await using var command = f.H.Connection.CreateCommand();

            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                for (int field = 0; field < reader.FieldCount; field++)
                {
                    object value = reader.GetValue(field);

                    snapshot.Append(value switch
                    {
                        DBNull => "N",
                        byte[] bytes => "B" + Convert.ToHexString(bytes),
                        _ => "T" + Convert.ToHexString(Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture)!)),
                    }).Append(';');
                }

                snapshot.AppendLine();
            }
        }

        return snapshot.ToString();
    }

    private sealed class CleanReviewMaintenance(MaintenanceFixture fixture, LongRestWorkAdmission work, bool closeAtHandoff) : ILongRestMaintenance
    {
        internal int Calls;

        internal IGrimoireClosingOwner? Closing;

        public Task<Result<LongRestMaintenanceCapability>> CreateCapabilityAsync(Guid operationId, ArcanumInvocationContext invocation,
            SessionTurnClaimLease claim, CanonicalCampaignContext scope, CovenantTurnLease lease, CancellationToken ct) =>
            fixture.Maintenance.CreateCapabilityAsync(operationId, invocation, claim, scope, lease, ct);

        public async Task<Result<LongRestSweepProgress>> ProcessAsync(Guid operationId, string workerOwner, LongRestMaintenanceCapability? capability, CancellationToken ct)
        {
            Calls++;

            LongRestDiscoveryGeneration generation = LongRestSweepHarness.Require(
                await fixture.H.Store.ReadGenerationMetadataAsync(operationId, ct))!;

            Assert.Equal(work.Lease.Generation, generation.Fence.AdmissionGeneration);

            Assert.True((await CleanReviewFacts(fixture).ReadWorkerAsync(generation, workerOwner, fixture.Clock.GetUtcNow(), ct)).IsSuccess);

            Assert.Equal(LongRunningOperationState.Running, (await fixture.Operations.GetAsync(operationId, ct))!.State);

            if (!closeAtHandoff)
            {
                // Contain a wrong authorization without inventing a paid response or authority.
                return new Error(ErrorCodes.LongRest.Unavailable, "Review stopped before physical work.");
            }

            Closing = LongRestSweepHarness.Require(fixture.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()));

            Assert.True(work.Lease.MaintenanceRevocationPending);

            throw new LongRestMaintenanceDeferredException(work.Lease.MaintenanceRevocation);
        }
    }

    private sealed class CleanReviewRecoveryScopes(MaintenanceFixture fixture, LongRestSweepCoordinator coordinator,
        LongRestWorkAdmission work) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new CleanReviewRecoveryScope(fixture, coordinator, work,
            fixture.Services.GetRequiredService<IServiceScopeFactory>().CreateScope());
    }

    private sealed class CleanReviewRecoveryScope(MaintenanceFixture fixture, LongRestSweepCoordinator coordinator,
        LongRestWorkAdmission work, IServiceScope inner) : IServiceScope, IServiceProvider, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type type)
        {
            if (type == typeof(ILongRunningOperationGenericRecoveryDiscovery)) return fixture.Operations;

            if (type == typeof(LongRestWorkAdmission)) return work;

            if (type == typeof(LongRunningOperationReconciler)) return new LongRunningOperationReconciler(fixture.Operations,
                [new LongRestSweepRecoveryHandler(coordinator)], fixture.Clock, NullLogger<LongRunningOperationReconciler>.Instance,
                new LongRunningOperationOwnership());

            return inner.ServiceProvider.GetService(type);
        }

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync()
        {
            if (inner is IAsyncDisposable asynchronous) return asynchronous.DisposeAsync();

            inner.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
