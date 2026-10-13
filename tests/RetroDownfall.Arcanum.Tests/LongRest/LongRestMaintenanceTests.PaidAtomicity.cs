using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData("long_rest_accepted_summaries", 1, 1)]
    [InlineData("long_rest_discovery_embeddings", 1, 2)]
    [InlineData("long_rest_adjudications", 3, 3)]
    public async Task Paid_atomicity_artifact_rollback_leaves_one_truthful_Invalid_without_orphan_Accepted(
        string table, int precedingUnits, int expectedSends)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        for (int unit = 0; unit < precedingUnits; unit++)
        {
            LongRestSweepHarness.Require(await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));
        }

        // A real SQLite rollback at the retained-output write, after the real SDK has billed.
        // The table is one of the three fixed theory literals above, never external input.
        await f.H.ExecuteAsync($"CREATE TEMP TRIGGER fixture_abort_paid_artifact BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT, 'fixture retained artifact refused'); END;");

        var result = await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(expectedSends, f.Handler.SendCount);

        Assert.Equal((long)expectedSends, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal((long)expectedSends, await f.H.ScalarAsync("SELECT sum(PaidCount) FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT sum(InvalidCount) FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 5 AND CompletionJson IS NOT NULL;"));

        Assert.Equal(0L, await f.H.ScalarAsync($"SELECT count(*) FROM {table};"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 3;"));

        if (table == "long_rest_discovery_embeddings")
        {
            Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer > 0;"));

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT FoldGroup FROM long_rest_discovery_generations;"));
        }

        if (table == "long_rest_adjudications")
        {
            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2;"));
        }
    }

    [Fact]
    public async Task Paid_atomicity_committed_summary_before_effect_release_survives_genuine_boot_without_second_summary()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        using CancellationTokenSource stopping = new();

        using LongRestSweepHostedService host = PaidOwnershipHost(f);

        TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IGrimoireClosingOwner? closing = null;

        string? original = null;

        string? originalWork = null;

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        f.RecordedAdmission.BeforeEffectGroupDisposalAsync = async () =>
        {
            if (f.Handler.SendCount != 1) return;

            original = await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;") as string;

            originalWork = await f.H.ScalarAsync("SELECT hex(WorkDigest) FROM long_rest_accepted_summaries;") as string;

            closing = f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()).Value;

            committed.TrySetResult();
        };

        Task<Exception?> run = Record.ExceptionAsync(() => host.RunOneAsync(f.Operation, stopping.Token));

        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(closing);

            Assert.True((await f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        }
        finally
        {
            await stopping.CancelAsync();

            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.NotNull(original);

        Assert.NotNull(originalWork);

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(LongRunningOperationState.Running, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));

        Assert.NotNull(closing);

        try
        {
            Assert.True((await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);
        }
        finally
        {
            await closing.DisposeAsync();
        }

        f.RecordedAdmission.BeforeEffectGroupDisposalAsync = () => ValueTask.CompletedTask;

        f.Payloads.Clear();

        await f.RebootAsync();

        var result = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, result.Phase);

        Assert.Equal(LongRestProviderPayloadKind.Embedding, f.Payloads[0].Kind);

        Assert.DoesNotContain(f.Payloads, payload => payload.Kind == LongRestProviderPayloadKind.Summary);

        Assert.Equal(original, await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"));

        Assert.Equal(originalWork, await f.H.ScalarAsync("SELECT hex(WorkDigest) FROM long_rest_accepted_summaries;"));

        Assert.Equal(f.Epoch.Capture().Stamp, LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!.Fence.Configuration);
    }
}
