using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Fact]
    public Task Paid_unit_maintenance_after_group_win_finishes_summary_before_drain() =>
        ObservePaidUnitMaintenanceAsync(afterProviderReturn: false);

    [Fact]
    public Task Paid_unit_maintenance_after_provider_return_retains_effect_through_summary_persistence() =>
        ObservePaidUnitMaintenanceAsync(afterProviderReturn: true);

    private static async Task ObservePaidUnitMaintenanceAsync(bool afterProviderReturn)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        using CancellationTokenSource stopping = new();

        using LongRestSweepHostedService host = PaidOwnershipHost(f);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ConcurrentQueue<long> acceptedSummariesAtEffectRelease = new();

        CancellationToken transport = default;

        f.Handler.InsideSendWithCancellation = async token =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            if (f.Handler.SendCount != 1) return;

            transport = token;

            if (!afterProviderReturn)
            {
                entered.TrySetResult();

                await release.Task.WaitAsync(token);
            }
        };

        if (afterProviderReturn)
        {
            f.ProviderReturned = async () =>
            {
                if (f.Handler.SendCount != 1) return;

                entered.TrySetResult();

                // This is an observation barrier after the REAL provider's accounting finally.
                // It does not replace the result or perform any durable transition.
                await release.Task;
            };
        }

        f.RecordedAdmission.BeforeEffectGroupDisposalAsync = async () =>
        {
            acceptedSummariesAtEffectRelease.Enqueue((long)(await f.H.ScalarAsync(
                "SELECT count(*) FROM long_rest_accepted_summaries;"))!);
        };

        Task<Exception?> run = Record.ExceptionAsync(() => host.RunOneAsync(f.Operation, stopping.Token));

        IGrimoireClosingOwner? closing = null;

        bool transportCancelledByMaintenance = false;

        bool drainedWhileHeld = false;

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, f.Handler.SendCount);

            Assert.Equal(1L, await f.H.ScalarAsync(
                "SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 3 AND SentCount = 1 AND CompletionJson IS NULL;"));

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

            if (afterProviderReturn)
            {
                Assert.NotNull(f.ProviderResult);

                Assert.True(f.ProviderResult.IsSuccess);

                Assert.Equal(LongRestProviderResultDisposition.Valid, f.ProviderResult.Value.Disposition);

                Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
            }
            else
            {
                Assert.Null(f.ProviderResult);

                Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
            }

            closing = f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()).Value;

            Task<RetroDownfall.Arcanum.Core.Primitives.Result> drain =
                f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            // Read while the handler barrier is still held. Real host cancellation is a separate test.
            transportCancelledByMaintenance = !afterProviderReturn && transport.IsCancellationRequested;

            drainedWhileHeld = drain.IsCompleted;

            release.TrySetResult();

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        }
        finally
        {
            release.TrySetResult();

            await stopping.CancelAsync();

            await run.WaitAsync(TimeSpan.FromSeconds(10));

            if (closing is not null) await closing.DisposeAsync();
        }

        Assert.True((await run) is null or OperationCanceledException);

        Assert.False(transportCancelledByMaintenance);

        Assert.False(drainedWhileHeld);

        Assert.Equal(1L, Assert.Single(acceptedSummariesAtEffectRelease));

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(LongRestProviderPayloadKind.Summary, Assert.Single(f.Payloads).Kind);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(1L, await f.H.ScalarAsync("""
            SELECT count(*) FROM long_rest_provider_effects
            WHERE StateCode = 4 AND SentCount = 1 AND PaidCount = 1 AND InvalidCount = 0
              AND InputTokens = 17 AND OutputTokens = 3 AND TotalTokens = 20 AND CompletionJson IS NOT NULL;
            """));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));

        // One retained summary is the paid disposition; embedding requires a new admitted send.
        Assert.Equal(0L, await f.H.ScalarAsync("SELECT FoldGroup FROM long_rest_discovery_generations;"));
    }

    [Fact]
    public async Task Paid_unit_admission_loss_before_effect_CAS_never_dispatches()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        using CancellationTokenSource stopping = new();

        using LongRestSweepHostedService host = PaidOwnershipHost(f);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IGrimoireClosingOwner? closing = null;

        f.RecordedAdmission.BeforeEffectGroupAdmission = () =>
        {
            closing = f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()).Value;

            entered.TrySetResult();
        };

        Task<Exception?> run = Record.ExceptionAsync(() => host.RunOneAsync(f.Operation, stopping.Token));

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(closing);

            Assert.True((await f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        }
        finally
        {
            await stopping.CancelAsync();

            await run.WaitAsync(TimeSpan.FromSeconds(10));

            if (closing is not null) await closing.DisposeAsync();
        }

        Assert.True((await run) is null or OperationCanceledException);

        Assert.Equal(1, f.RecordedAdmission.EffectGroupAttempts);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Empty(f.Payloads);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT COALESCE(sum(SentCount), 0) FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));
    }

    [Fact]
    public async Task Paid_unit_host_cancellation_still_cancels_winning_HTTP_and_preserves_unknown_Sent()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        using CancellationTokenSource stopping = new();

        using LongRestSweepHostedService host = PaidOwnershipHost(f);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        f.Handler.InsideSendWithCancellation = async token =>
        {
            using CancellationTokenRegistration observed = token.Register(() => cancelled.TrySetResult());

            entered.TrySetResult();

            await release.Task.WaitAsync(token);
        };

        Task<Exception?> run = Record.ExceptionAsync(() => host.RunOneAsync(f.Operation, stopping.Token));

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1L, await f.H.ScalarAsync(
                "SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 3 AND SentCount = 1 AND CompletionJson IS NULL;"));

            await stopping.CancelAsync();

            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.IsAssignableFrom<OperationCanceledException>(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.TrySetResult();

            await stopping.CancelAsync();

            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(1L, await f.H.ScalarAsync("""
            SELECT count(*) FROM long_rest_provider_effects
            WHERE StateCode = 3 AND SentCount = 1 AND PaidCount = 0 AND InvalidCount = 0
              AND CompletionJson IS NULL AND CostUsd IS NULL;
            """));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));
    }

    private static LongRestSweepHostedService PaidOwnershipHost(MaintenanceFixture fixture) =>
        new(fixture.Services.GetRequiredService<IServiceScopeFactory>(), new LongRestSweepQueue(),
            fixture.Epoch, fixture.RecordedAdmission, NullLoggerFactory.Instance);

    private static CovenantExclusiveRecoveryOwner PaidOwnershipClosingOwner() =>
        new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(new byte[32]));
}
