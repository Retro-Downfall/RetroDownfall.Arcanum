using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData(LongRestDispatchAttemptState.Invalid)]
    [InlineData(LongRestDispatchAttemptState.Failed)]
    public async Task Original_terminal_work_survives_reboot_and_two_safe_adoptions_before_actual_retry(LongRestDispatchAttemptState state)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        await LongRestProvenanceTestEvidence.RequireSchemaAsync(f.H);

        LongRestDispatchAttempt? originalSent = null;

        f.AfterAttemptValidated = (attempt, _) =>
        {
            if (attempt.State == LongRestDispatchAttemptState.Sent) originalSent = attempt;

            return Task.CompletedTask;
        };

        if (state == LongRestDispatchAttemptState.Invalid) f.Handler.ChatText = "{}";
        else f.Handler.Status = HttpStatusCode.BadRequest;

        LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        Assert.True((await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None)).IsFailure);

        Assert.NotNull(originalSent);

        Assert.Equal(1, f.Handler.SendCount);

        if (state == LongRestDispatchAttemptState.Failed)
        {
            // The actual SDK returned HTTP 400, so this is a known failed response rather
            // than unknown transport. Settle that exact Sent attempt through the real store.
            Assert.NotNull(f.ProviderResult);

            Assert.True(f.ProviderResult!.IsFailure);

            Assert.Equal(3L, await f.H.ScalarAsync("SELECT StateCode FROM long_rest_provider_effects;"));

            LongRestSweepHarness.Require(await new LongRestAttemptStore(f.H.Saga.Context, f.Clock).CompleteAttemptAsync(originalSent!,
                new(state, new(0, 0, 0, 0, 0), null, "LongRest.TransportFailure"), CancellationToken.None));
        }

        f.AfterAttemptValidated = null;

        LongRestWorkIdentity original = originalSent!.Work;

        string witness = await LongRestProvenanceTestEvidence.RequireOriginalAsync(f.H, original);

        object? physical = await f.H.ScalarAsync(LongRestProvenanceTestEvidence.PhysicalSnapshotSql);

        object? billing = await f.H.ScalarAsync(CleanBillingSnapshotSql);

        Assert.Equal((long)state, await f.H.ScalarAsync("SELECT StateCode FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT PhysicalOrdinal FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT SentCount FROM long_rest_provider_effects;"));

        Assert.Equal(state == LongRestDispatchAttemptState.Invalid ? 1L : 0L,
            await f.H.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

        await f.RebootAsync();

        Assert.NotEqual(original.Fence.Configuration.BootId, f.Epoch.Capture().Stamp.BootId);

        Assert.Equal(original.Fence.Configuration.ValueDigest, f.Epoch.Capture().Stamp.ValueDigest);

        f.Handler.Status = HttpStatusCode.OK;

        f.Handler.InsideSend = () => { f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!); return Task.CompletedTask; };

        // Pass 0 performs genuine reboot association. Its handoff creates the first actual
        // drained abort. Pass 1 adopts that edge and creates the second; pass 2 adopts the
        // second edge and cancels before Prepare without creating another admission edge.
        for (int pass = 0; pass < 3; pass++)
        {
            long admitted = f.Admission.CurrentGeneration;

            LongRestDiscoveryGeneration current = await ProvenancePassBeforePrepareAsync(f, pass < 2, $"provenance-pass-{pass}");

            Assert.Equal(admitted, current.Fence.AdmissionGeneration);

            Assert.Equal(f.Epoch.Capture().Stamp, current.Fence.Configuration);

            Assert.NotEqual(original.Fence.Configuration.BootId, current.Fence.Configuration.BootId);

            Assert.Equal(witness, await LongRestProvenanceTestEvidence.RequireOriginalAsync(f.H, original));

            Assert.Equal(physical, await f.H.ScalarAsync(LongRestProvenanceTestEvidence.PhysicalSnapshotSql));

            Assert.Equal(billing, await f.H.ScalarAsync(CleanBillingSnapshotSql));

            Assert.Equal(1, f.Handler.SendCount);

            Assert.Single(f.Payloads);

            Assert.Equal(LongRunningOperationState.Running, (await f.Operations.GetAsync(f.Operation))!.State);

            Assert.Equal(Convert.ToHexString(LongRestSweepHarness.Require(LongRestDiscoveryHash.Work(original with { Fence = current.Fence })).Bytes),
                await f.H.ScalarAsync("SELECT hex(WorkDigest) FROM long_rest_work_items WHERE KindCode = 2;"));

            Assert.Equal(pass < 2 ? admitted + 1 : admitted, f.Admission.CurrentGeneration);
        }

        f.Payloads.Clear();

        LongRunningOperationReconciliationSummary recovered = await RecoverCleanAssociationAsync(f);

        Assert.Equal(1, recovered.Completed);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        // NoChange leaves both different-content sources eligible, so each anchor requires
        // its own adjudication after the summary retry and embedding.
        Assert.Equal(5, f.Handler.SendCount);

        Assert.Equal([LongRestProviderPayloadKind.Summary, LongRestProviderPayloadKind.Embedding, LongRestProviderPayloadKind.Adjudication, LongRestProviderPayloadKind.Adjudication],
            f.Payloads.Select(payload => payload.Kind).ToArray());

        Assert.Equal(f.Payloads[0].Work, f.Payloads[1].Work);

        LongRestProviderPayload[] adjudications = [.. f.Payloads.Skip(2)];

        string[] sources = adjudications[0].Inputs.Select(input => input.Snapshot.MemoryId).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(2, sources.Length);

        Assert.Equal(2, sources.Distinct(StringComparer.Ordinal).Count());

        Assert.Contains(f.Source, sources);

        Assert.Equal(sources, adjudications.Select(payload => payload.Inputs[0].Snapshot.MemoryId).ToArray());

        Assert.Equal(2, adjudications.Select(payload => payload.Work.ItemId).Distinct(StringComparer.Ordinal).Count());

        Assert.All(adjudications, payload => Assert.Equal(sources,
            payload.Inputs.Select(input => input.Snapshot.MemoryId).Order(StringComparer.Ordinal).ToArray()));

        LongRestWorkIdentity retry = f.Payloads[0].Work;

        Assert.NotEqual(original.Fence, retry.Fence);

        Assert.Equal(f.Epoch.Capture().Stamp, retry.Fence.Configuration);

        Assert.Equal(f.Admission.CurrentGeneration, retry.Fence.AdmissionGeneration);

        Assert.NotEqual(witness, await LongRestProvenanceTestEvidence.RequireOriginalAsync(f.H, retry));

        Assert.Equal(3L, await f.H.ScalarAsync("SELECT PhysicalOrdinal FROM long_rest_provider_effects effect JOIN long_rest_work_items unit ON unit.WorkItemId = effect.WorkItemId WHERE unit.KindCode = 2;"));

        Assert.Equal(state == LongRestDispatchAttemptState.Invalid ? 5L : 4L,
            await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(state == LongRestDispatchAttemptState.Invalid ? 5L : 4L,
            await f.H.ScalarAsync("SELECT sum(PaidCount) FROM long_rest_provider_effects;"));

        Assert.Equal(state == LongRestDispatchAttemptState.Invalid ? 1L : 0L,
            await f.H.ScalarAsync("SELECT sum(InvalidCount) FROM long_rest_provider_effects;"));

        Assert.Equal(state == LongRestDispatchAttemptState.Failed ? 1L : 0L,
            await f.H.ScalarAsync("SELECT sum(FailureCount) FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

        Assert.Equal(5L, await f.H.ScalarAsync("SELECT sum(SentCount) FROM long_rest_provider_effects;"));

        Assert.Equal(2L, await f.H.ScalarAsync("""
            SELECT count(*) FROM long_rest_provider_effects effect JOIN long_rest_work_items unit ON unit.WorkItemId = effect.WorkItemId
            WHERE unit.KindCode = 3 AND unit.DispositionCode = 2 AND effect.StateCode = 4
              AND effect.PhysicalOrdinal = 1 AND effect.SentCount = 1 AND effect.PaidCount = 1;
            """));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications WHERE DecisionKindCode = 3;"));

        // Provider NoChange completes the unit without applying a canonical transformation.
        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));
    }

    private static async Task<LongRestDiscoveryGeneration> ProvenancePassBeforePrepareAsync(MaintenanceFixture f, bool close, string owner)
    {
        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation;",
            ("$operation", f.Operation.ToString("N")));

        using CancellationTokenSource stopping = new();

        IGrimoireClosingOwner? closing = null;

        int handoffs = 0;

        f.RecordedAdmission.BeforeEffectGroupAdmission = () =>
        {
            handoffs++;

            if (close) closing = LongRestSweepHarness.Require(f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()));
            else stopping.Cancel();
        };

        LongRunningOperationStartupHostedService recovery = new(new PaidRecoveryScopes(f), f.Clock,
            new LongRunningOperationReconciliationStatus(), f.RecordedAdmission, NullLogger<LongRunningOperationStartupHostedService>.Instance);

        try
        {
            Exception? stopped = await Record.ExceptionAsync(() => recovery.RunBackgroundPassAsync(
                f.Clock.GetUtcNow(), owner, stopping.Token).WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.True(stopped is null or OperationCanceledException, stopped?.ToString());

            // Proves each genuine/reopened association reached the real maintenance
            // effect-group boundary. A safe refusal before this point remains a RED.
            Assert.Equal(1, handoffs);

            return LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;
        }
        finally
        {
            f.RecordedAdmission.BeforeEffectGroupAdmission = static () => { };

            await stopping.CancelAsync();

            await recovery.StopAsync(CancellationToken.None);

            if (closing is not null)
            {
                Assert.True((await f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

                Assert.True((await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

                await closing.DisposeAsync();
            }
        }
    }
}
