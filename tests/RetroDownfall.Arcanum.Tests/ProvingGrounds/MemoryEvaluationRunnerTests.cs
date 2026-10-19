using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

[Collection("Grimoire")]
public sealed class MemoryEvaluationRunnerTests
{
    [Fact]
    public async Task Three_pairs_commit_before_dispatch_and_replay_without_hydration_or_billing()
    {
        await using var f = await Fixture.Create();

        var response = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.True(response.IsSuccess, response.Error.ToString());

        Assert.Equal(MemoryEvaluationState.Completed, response.Value.State);

        Assert.Equal(MemoryEvaluationOutcome.NoMeasurableEffect, response.Value.Report!.Decision.Outcome);

        Assert.Equal(3, response.Value.Report.Aggregate.ValidPairs);

        Assert.Equal(6, f.Physical.Server.Bodies.Count);

        Assert.Equal(new[] { false, true, true, false, false, true }, f.Physical.Server.Bodies.Select(body => body.Contains("memory sentinel", StringComparison.Ordinal)).ToArray());

        Assert.Equal(6, f.Journal.Acknowledgements);

        Assert.Equal(6L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));

        var stored = (await f.Evidence.GetAsync(f.Request.EvaluationId, default)).Value;

        Assert.Equal(stored.ReportDigest, response.Value.ReportDigest);

        Assert.True((await f.Scope.RevalidateAsync(default)).IsSuccess);

        Assert.False(f.Scope.Invocation.CanStageCovenantMutation);

        var replay = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.True(replay.IsSuccess);

        Assert.Equal(response.Value.ReportDigest, replay.Value.ReportDigest);

        Assert.Equal(1, f.Reader.Reads);

        Assert.Equal(6, f.Physical.Server.Bodies.Count);

        Assert.Equal(6L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));

        var changed = await f.Runner.RunAsync(f.Request with { Model = "changed" }, f.Scope, default);

        Assert.True(changed.IsFailure);

        Assert.Equal("Security.IdempotencyConflict", changed.Error.Code);

        var get = await f.Runner.GetAsync(new(f.Request.EvaluationId), f.Scope, default);

        Assert.Equal(response.Value.ReportDigest, get.Value.ReportDigest);

        Assert.True((await f.Runner.DeleteAsync(new(f.Request.EvaluationId, get.Value.ReportDigest!.Value, true), f.Scope, default)).IsSuccess);

        var erased = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.True(erased.IsFailure);

        Assert.Equal("MemoryEvaluation.Gone", erased.Error.Code);

        Assert.Equal(1, f.Reader.Reads);

        Assert.Equal(6, f.Physical.Server.Bodies.Count);
    }

    [Fact]
    public async Task Missing_get_distinguishes_unknown_and_other_kind_identity_from_erased_evaluation()
    {
        await using var f = await Fixture.Create();

        var unknown = await f.Runner.GetAsync(new(Guid.NewGuid()), f.Scope, default);

        Assert.Equal("MemoryEvaluation.NotFound", unknown.Error.Code);

        Guid otherKindId = Guid.NewGuid();

        var digest = MemoryEvaluationEvidenceStoreTests.Registration([MemoryEvaluationEvidenceStoreTests.Source()]).RequestDigest;

        await f.Operations.ResolveOrCreateAsync(new(LongRunningOperationKinds.Subagent, LongRunningOperationRecoveryPolicy.AbandonSafely, "Other operation", DateTimeOffset.UtcNow), new(otherKindId, digest, digest));

        var otherKind = await f.Runner.GetAsync(new(otherKindId), f.Scope, default);

        Assert.Equal("MemoryEvaluation.NotFound", otherKind.Error.Code);

        var result = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.True(result.IsSuccess, result.Error.ToString());

        Assert.True((await f.Runner.DeleteAsync(new(f.Request.EvaluationId, result.Value.ReportDigest!.Value, true), f.Scope, default)).IsSuccess);

        var erased = await f.Runner.GetAsync(new(f.Request.EvaluationId), f.Scope, default);

        Assert.Equal("MemoryEvaluation.Gone", erased.Error.Code);

        Assert.Equal(1, f.Reader.Reads);

        Assert.Equal(6, f.Physical.Server.Bodies.Count);

        Assert.Equal(6L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Absent_delete_requires_an_exact_retained_memory_evaluation_identity(bool otherKind)
    {
        await using var f = await Fixture.Create();

        Guid id = Guid.NewGuid();

        var digest = MemoryEvaluationEvidenceStoreTests.Registration([MemoryEvaluationEvidenceStoreTests.Source()]).RequestDigest;

        if (otherKind)
            await f.Operations.ResolveOrCreateAsync(new(LongRunningOperationKinds.Subagent, LongRunningOperationRecoveryPolicy.AbandonSafely, "Other operation", DateTimeOffset.UtcNow), new(id, digest, digest));

        var result = await f.Runner.DeleteAsync(new(id, digest, true), f.Scope, default);

        Assert.True(result.IsFailure);

        Assert.Equal("MemoryEvaluation.NotFound", result.Error.Code);

        Assert.Equal(0, f.Reader.Reads);

        Assert.Empty(f.Physical.Server.Bodies);

        Assert.Equal(0L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));
    }

    [Fact]
    public async Task Repeated_confirmed_delete_is_metadata_only_and_preserves_exact_request_validation()
    {
        await using var f = await Fixture.Create();

        var response = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.True(response.IsSuccess, response.Error.ToString());

        var request = new MemoryEvaluationDeleteRequest(f.Request.EvaluationId, response.Value.ReportDigest!.Value, true);

        var deleted = await f.Runner.DeleteAsync(request, f.Scope, default);

        Assert.True(deleted.IsSuccess);

        Assert.True(deleted.Value.Deleted);

        var repeated = await f.Runner.DeleteAsync(request, f.Scope, default);

        Assert.True(repeated.IsSuccess, repeated.Error.ToString());

        Assert.False(repeated.Value.Deleted);

        Assert.Equal(f.Request.EvaluationId, repeated.Value.EvaluationId);

        Assert.True((await f.Runner.DeleteAsync(request with { Confirm = false }, f.Scope, default)).IsFailure);

        Assert.True((await f.Runner.DeleteAsync(request with { ExpectedReportDigest = default }, f.Scope, default)).IsFailure);

        Assert.Equal(1, f.Reader.Reads);

        Assert.Equal(6, f.Physical.Server.Bodies.Count);

        Assert.Equal(6L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));

        f.Authority.Advance();

        Assert.True((await f.Runner.DeleteAsync(request, f.Scope, default)).IsFailure);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("erasure")]
    [InlineData("cancellation")]
    public async Task Final_acknowledgement_fence_stops_the_send_and_never_resurrects_erased_evidence(string change)
    {
        await using var f = await Fixture.Create();

        f.Journal.After = async () =>
        {
            if (change == "configuration") f.Physical.Aba();
            else if (change == "erasure") await f.Physical.H.ExecuteAsync("DELETE FROM memory_evaluation_sources; DELETE FROM memory_evaluations;");
            else
            {
                var current = (await f.Operations.FindByRequestedOperationIdAsync(f.Request.EvaluationId))!.Operation;

                Assert.True(await f.Operations.RequestCancellationAsync(current.Id, current.Revision, DateTimeOffset.UtcNow));
            }
        };

        var result = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.Empty(f.Physical.Server.Bodies);

        Assert.Equal(0L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));

        Assert.True(result.IsFailure || result.Value.Report!.Decision.Outcome == MemoryEvaluationOutcome.InsufficientEvidence);

        var stored = await f.Evidence.GetAsync(f.Request.EvaluationId, default);

        if (change == "erasure") Assert.True(stored.IsFailure);
        else
        {
            Assert.NotNull(stored.Value.Report); Assert.Null(stored.Value.Report.Cost);

            var reason = change == "configuration" ? MemoryEvaluationReason.StaleConfiguration : MemoryEvaluationReason.Cancelled;

            Assert.Equal(reason, stored.Value.Report.Decision.Reason);

            Assert.Equal(change == "configuration" ? MemoryEvaluationState.Completed : MemoryEvaluationState.Cancelled, stored.Value.Report.State);

            var get = await f.Runner.GetAsync(new(f.Request.EvaluationId), f.Scope, default);

            var replay = await f.Runner.RunAsync(f.Request, f.Scope, default);

            Assert.Equal(reason, get.Value.Report!.Decision.Reason);

            Assert.Equal(reason, replay.Value.Report!.Decision.Reason);
        }

        Assert.Equal(1, f.Reader.Reads);
    }

    [Fact]
    public async Task Concurrent_replay_and_new_identity_cannot_take_a_second_execution_owner()
    {
        await using var f = await Fixture.Create();

        TaskCompletionSource acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        f.Journal.After = async () => { if (f.Journal.Acknowledgements == 1) { acknowledged.SetResult(); await release.Task; } };

        var first = f.Runner.RunAsync(f.Request, f.Scope, default);

        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var replay = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.True(replay.IsSuccess);

        Assert.Equal(MemoryEvaluationState.Running, replay.Value.State);

        Assert.Null(replay.Value.ReportDigest);

        Assert.Equal(1, f.Reader.Reads);

        var refused = await f.Runner.RunAsync(f.Request with { EvaluationId = Guid.NewGuid() }, f.Scope, default);

        Assert.Equal("MemoryEvaluation.Busy", refused.Error.Code);

        Assert.Empty(f.Physical.Server.Bodies);

        release.SetResult();

        Assert.True((await first).IsSuccess);

        Assert.Equal(6, f.Physical.Server.Bodies.Count);

        Assert.Equal(1L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM LongRunningOperations"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_publication_waits_for_registration_and_checkpoint_without_resurrecting_erasure(bool eraseRegistered)
    {
        await using var f = await Fixture.Create();
        await using var replayServices = f.OpenMetadataScope();
        await using var getServices = f.OpenMetadataScope();
        var replayRunner = f.MetadataRunner(replayServices.ServiceProvider);
        var getRunner = f.MetadataRunner(getServices.ServiceProvider);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Hold() { entered.SetResult(); await release.Task; }
        if (eraseRegistered) f.Publications.AfterRegister = Hold; else f.Coordinator.AfterStart = Hold;
        f.Journal.After = async () => { if (f.Journal.Acknowledgements == 1) { acknowledged.SetResult(); await finish.Task; } };
        var first = f.Runner.RunAsync(f.Request, f.Scope, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var identity = (await f.Operations.FindByRequestedOperationIdAsync(f.Request.EvaluationId))!;
            Assert.Equal(0, identity.Operation.CheckpointVersion);
            if (eraseRegistered) await f.Physical.H.ExecuteAsync("DELETE FROM memory_evaluations");
            var replay = replayRunner.RunAsync(f.Request, f.Scope, default);
            var get = getRunner.GetAsync(new(f.Request.EvaluationId), f.Scope, default);
            Assert.False(replay.IsCompleted, "Exact replay must wait for initial publication instead of returning premature Gone.");
            Assert.False(get.IsCompleted, "Status must wait for initial publication.");
            Assert.Equal("Security.IdempotencyConflict", (await f.Runner.RunAsync(f.Request with { Model = "changed" }, f.Scope, default)).Error.Code);
            Assert.Equal("MemoryEvaluation.NotFound", (await f.Runner.GetAsync(new(Guid.NewGuid()), f.Scope, default)).Error.Code);
            Assert.Equal("MemoryEvaluation.Busy", (await f.Runner.RunAsync(f.Request with { EvaluationId = Guid.NewGuid() }, f.Scope, default)).Error.Code);
            Assert.Equal(1, f.Reader.Reads);
            Assert.Empty(f.Physical.Server.Bodies);
            Assert.Equal(0L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));
            release.SetResult();
            if (!eraseRegistered) await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var result in new[] { await replay, await get })
            {
                if (eraseRegistered) Assert.Equal("MemoryEvaluation.Gone", result.Error.Code);
                else
                {
                    Assert.True(result.IsSuccess, result.Error.ToString());
                    Assert.Equal(MemoryEvaluationState.Running, result.Value.State);
                    Assert.Equal(identity.Operation.Id, result.Value.OperationId);
                    Assert.Null(result.Value.Report);
                }
            }
        }
        finally { release.TrySetResult(); finish.TrySetResult(); await first; }
        Assert.Equal(1, f.Reader.Reads);
        Assert.Equal(eraseRegistered ? 0 : 6, f.Physical.Server.Bodies.Count);
        Assert.Equal(eraseRegistered ? 0L : 6L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));
        if (eraseRegistered) Assert.Equal("MemoryEvaluation.NotFound", (await f.Evidence.GetAsync(f.Request.EvaluationId, default)).Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_publication_waiter_cancellation_and_failed_initialization_settle_without_dispatch(bool cancelInitialization)
    {
        await using var f = await Fixture.Create();
        await using var replayServices = f.OpenMetadataScope();
        await using var getServices = f.OpenMetadataScope();
        var replayRunner = f.MetadataRunner(replayServices.ServiceProvider);
        var getRunner = f.MetadataRunner(getServices.ServiceProvider);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Coordinator.AfterStart = async () =>
        {
            entered.SetResult(); await release.Task;
            if (cancelInitialization) throw new OperationCanceledException();
            throw new InvalidOperationException("Initialization failed after durable identity.");
        };
        var first = f.Runner.RunAsync(f.Request, f.Scope, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var cancelledWaiter = getRunner.GetAsync(new(f.Request.EvaluationId), f.Scope, cancellation.Token);
            var replay = replayRunner.RunAsync(f.Request, f.Scope, default);
            Assert.False(cancelledWaiter.IsCompleted);
            Assert.False(replay.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledWaiter);
            Assert.False(first.IsCompleted);
            release.SetResult();
            Assert.Equal(cancelInitialization ? "MemoryEvaluation.Cancelled" : "MemoryEvaluation.Interrupted", (await first).Error.Code);
            Assert.Equal("MemoryEvaluation.Gone", (await replay.WaitAsync(TimeSpan.FromSeconds(10))).Error.Code);
            Assert.Equal("MemoryEvaluation.Gone", (await f.Runner.GetAsync(new(f.Request.EvaluationId), f.Scope, default)).Error.Code);
            Assert.Equal(1, f.Reader.Reads);
            Assert.Empty(f.Physical.Server.Bodies);
            Assert.Equal(0L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));
        }
        finally { release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task Missing_ownership_read_crossing_identity_creation_rechecks_publication_before_Gone()
    {
        await using var f = await Fixture.Create();
        await using var metadata = f.OpenMetadataScope();
        TaskCompletionSource missing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeMissing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource publish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = f.MetadataRunner(metadata.ServiceProvider, async () => { missing.TrySetResult(); await resumeMissing.Task; });
        f.Coordinator.AfterStart = async () => { created.SetResult(); await publish.Task; };
        f.Journal.After = async () => { if (f.Journal.Acknowledgements == 1) { acknowledged.SetResult(); await finish.Task; } };
        var get = reader.GetAsync(new(f.Request.EvaluationId), f.Scope, default);
        await missing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, f.Reader.Reads);
        var run = f.Runner.RunAsync(f.Request, f.Scope, default);
        await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var operation = (await f.Operations.FindByRequestedOperationIdAsync(f.Request.EvaluationId))!.Operation;
            Assert.Equal(0, operation.CheckpointVersion);
            resumeMissing.SetResult();
            publish.SetResult();
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await get.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.IsSuccess, result.Error.ToString());
            Assert.Equal(MemoryEvaluationState.Running, result.Value.State);
            Assert.Equal(operation.Id, result.Value.OperationId);
            Assert.Equal(1, f.Reader.Reads);
            Assert.Empty(f.Physical.Server.Bodies);
            Assert.Equal(0L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));
        }
        finally { resumeMissing.TrySetResult(); publish.TrySetResult(); finish.TrySetResult(); await run; }
        Assert.Equal(6, f.Physical.Server.Bodies.Count);
        Assert.Equal(6L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));
        Assert.Equal(1, f.Reader.Reads);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public async Task Cancellation_after_each_paid_prefix_is_insufficient_and_never_resumed(int stopAt)
    {
        await using var f = await Fixture.Create();

        f.Journal.After = async () =>
        {
            if (f.Journal.Acknowledgements == stopAt)
            {
                var current = (await f.Operations.FindByRequestedOperationIdAsync(f.Request.EvaluationId))!.Operation;

                Assert.True(await f.Operations.RequestCancellationAsync(current.Id, current.Revision, DateTimeOffset.UtcNow));
            }
        };

        var result = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.True(result.IsSuccess, result.Error.ToString());

        Assert.Equal(MemoryEvaluationOutcome.InsufficientEvidence, result.Value.Report!.Decision.Outcome);

        Assert.Equal(MemoryEvaluationReason.Cancelled, result.Value.Report.Decision.Reason);

        Assert.Equal(stopAt - 1, f.Physical.Server.Bodies.Count);

        Assert.Equal((long)stopAt - 1, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));

        Assert.Null(result.Value.Report.Cost);

        Assert.Null(result.Value.Report.InputTokens);

        var replay = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.Equal(result.Value.ReportDigest, replay.Value.ReportDigest);

        Assert.Equal(stopAt - 1, f.Physical.Server.Bodies.Count);
    }

    [Theory]
    [InlineData("missing-usage", MemoryEvaluationReason.UsageFailure)]
    [InlineData("wrong-model", MemoryEvaluationReason.ModelMismatch)]
    [InlineData("truncated", MemoryEvaluationReason.InvalidOutput)]
    public async Task Invalid_physical_result_retains_exact_reason_without_scoring_or_second_send(string mode, MemoryEvaluationReason reason)
    {
        await using var f = await Fixture.Create(mode);

        var result = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.True(result.IsSuccess, result.Error.ToString());

        Assert.Equal(MemoryEvaluationOutcome.InsufficientEvidence, result.Value.Report!.Decision.Outcome);

        Assert.Equal(reason, result.Value.Report.Decision.Reason);

        Assert.Equal(0, result.Value.Report.Aggregate.ValidPairs);

        Assert.Single(f.Physical.Server.Bodies);

        Assert.Null(result.Value.Report.Cost);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("source")]
    [InlineData("erasure")]
    [InlineData("authority")]
    public async Task Last_paid_response_cannot_publish_scores_after_final_fence_moves(string change)
    {
        await using var f = await Fixture.Create();

        f.AfterSend = async count =>
        {
            if (count != 6) return;

            if (change == "configuration") f.Physical.Aba();
            else if (change == "source") await f.Physical.H.Saga.Store.RetireAsync("selected", f.Request.Memory.Saga[0].ContentDigest.Bytes, DateTimeOffset.UtcNow, default);
            else if (change == "erasure") Assert.True(await f.Physical.H.Saga.Store.DeleteAsync("selected", default));
            else f.Authority.Advance();
        };

        var result = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.Equal(6, f.Physical.Server.Bodies.Count);

        var retained = await f.Evidence.GetAsync(f.Request.EvaluationId, default);

        if (change == "erasure") { Assert.True(retained.IsFailure); Assert.True(result.IsFailure); }
        else
        {
            Assert.True(retained.IsSuccess, retained.Error.ToString());

            Assert.Equal(MemoryEvaluationOutcome.InsufficientEvidence, retained.Value.Report!.Decision.Outcome);

            Assert.Equal(change switch { "configuration" => MemoryEvaluationReason.StaleConfiguration, "source" => MemoryEvaluationReason.StaleSource, _ => MemoryEvaluationReason.StaleAuthority }, retained.Value.Report.Decision.Reason);

            Assert.Equal(3, retained.Value.Report.Aggregate.ValidPairs);

            Assert.Null(retained.Value.Report.Cost);
        }

        Assert.Equal(6L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));
    }

    [Fact]
    public async Task Full_evidence_quota_refuses_without_dispatch_and_settles_its_operation()
    {
        await using var f = await Fixture.Create();

        var source = MemoryEvaluationEvidenceStoreTests.Source();

        for (int index = 0; index < MemoryEvaluationLimits.RetainedRegistrations; index++)
            Assert.True((await f.Evidence.RegisterAsync(MemoryEvaluationEvidenceStoreTests.Registration([source]), [source], default)).IsSuccess);

        var refused = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.True(refused.IsFailure);

        Assert.Equal("MemoryEvaluation.QuotaExceeded", refused.Error.Code);

        Assert.Empty(f.Physical.Server.Bodies);

        Assert.Equal(0, f.Journal.Acknowledgements);

        Assert.Equal(0L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));

        var operation = (await f.Operations.FindByRequestedOperationIdAsync(f.Request.EvaluationId))!.Operation;

        Assert.Equal(LongRunningOperationState.Failed, operation.State);

        var replay = await f.Runner.RunAsync(f.Request, f.Scope, default);

        Assert.Equal("MemoryEvaluation.Gone", replay.Error.Code);

        Assert.Equal(1, f.Reader.Reads);

        Assert.Equal(1024L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM memory_evaluations"));
    }

    [Theory]
    [InlineData("completed-observer")]
    [InlineData("ledger-before")]
    [InlineData("ledger-after")]
    public async Task Successful_report_commit_remains_authoritative_after_observer_or_ledger_settlement_failure(string boundary)
    {
        var clock = new ManualWallClockTimeProvider();

        await using var f = await Fixture.Create(clock: clock);

        clock.Advance(DateTimeOffset.UtcNow - clock.GetUtcNow());

        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (boundary == "ledger-before")
            f.Coordinator.BeforeComplete = async () =>
            {
                completed.SetResult();

                await release.Task;

                throw new InvalidOperationException("ledger fixture failure before transition");
            };
        else
            f.Coordinator.AfterComplete = async () =>
            {
                completed.SetResult();

                await release.Task;

                if (boundary == "ledger-after") throw new InvalidOperationException("ledger fixture failure after transition");
            };

        Task<Result<MemoryEvaluationResponse>> running = f.Runner.RunAsync(f.Request, f.Scope, default);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var committed = await f.Evidence.GetAsync(f.Request.EvaluationId, default);

        Assert.True(committed.IsSuccess, committed.Error.ToString());

        CovenantDigest? committedDigest = committed.Value.ReportDigest;

        var operation = (await f.Operations.FindByRequestedOperationIdAsync(f.Request.EvaluationId))!.Operation;

        Assert.Equal(boundary == "ledger-before" ? LongRunningOperationState.Running : LongRunningOperationState.Completed, operation.State);

        if (boundary == "completed-observer")
        {
            TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);

            using var cancellation = f.DispatchToken.Register(() => observed.TrySetResult());

            clock.Advance(TimeSpan.FromSeconds(1));

            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(f.DispatchToken.IsCancellationRequested);
        }

        release.SetResult();

        var result = await running;

        Assert.True(result.IsSuccess, result.Error.ToString());

        Assert.Equal(committedDigest, result.Value.ReportDigest);

        Assert.Equal(MemoryEvaluationOutcome.NoMeasurableEffect, result.Value.Report!.Decision.Outcome);

        Assert.Equal(1, f.Publications.CompleteAttempts);

        Assert.Equal(committedDigest, (await f.Runner.GetAsync(new(f.Request.EvaluationId), f.Scope, default)).Value.ReportDigest);

        Assert.Equal(committedDigest, (await f.Runner.RunAsync(f.Request, f.Scope, default)).Value.ReportDigest);

        Assert.Equal(1, f.Reader.Reads);

        Assert.Equal(6, f.Physical.Server.Bodies.Count);

        Assert.Equal(6L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("erasure")]
    [InlineData("request-abort")]
    [InlineData("caller-cancel")]
    public async Task Published_report_does_not_bypass_response_authority_erasure_or_request_cancellation(string boundary)
    {
        await using var f = await Fixture.Create();

        using var abort = new CancellationTokenSource();

        using var caller = new CancellationTokenSource();

        f.Http.RequestAborted = abort.Token;

        f.Coordinator.AfterComplete = async () =>
        {
            if (boundary == "authority") f.Authority.Advance();
            else if (boundary == "erasure") Assert.True(await f.Physical.H.Saga.Store.DeleteAsync("selected", default));
            else if (boundary == "request-abort") abort.Cancel();
            else caller.Cancel();
        };

        var result = await f.Runner.RunAsync(f.Request, f.Scope, caller.Token);

        Assert.True(result.IsFailure);

        Assert.NotEqual("Security.IdempotencyConflict", result.Error.Code);

        Assert.Equal(1, f.Publications.CompleteAttempts);

        if (boundary == "erasure") Assert.Equal("MemoryEvaluation.Gone", result.Error.Code);

        Assert.Equal(6, f.Physical.Server.Bodies.Count);

        Assert.Equal(6L, await f.Physical.H.ScalarAsync("SELECT count(*) FROM BillableOperations"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryEvaluationProviderTests.Fixture Physical = null!;

        internal MemoryEvaluationSourceHarness Source = null!;

        internal MemoryEvaluationRequest Request = null!;

        internal MemoryEvaluationRequestScope Scope = null!;

        internal MemoryEvaluationRunner Runner = null!;

        internal CountingReader Reader = null!;

        internal MemoryEvaluationEvidenceStore Evidence = null!;

        internal LongRunningOperationStore Operations = null!;

        internal MemoryEvaluationExecutionSlot Slot = new();

        internal Journal Journal = null!;

        internal CountingEvidence Publications = null!;

        internal ObservedCoordinator Coordinator = null!;

        internal TimeProvider Clock = TimeProvider.System;

        internal DefaultHttpContext Http = null!;

        internal CancellationToken DispatchToken;

        internal FakeCovenantAuthorityProvider Authority = new();

        internal Func<int, Task>? AfterSend;

        private ServiceProvider _services = null!;

        internal static async Task<Fixture> Create(string mode = "valid", TimeProvider? clock = null)
        {
            var f = new Fixture { Physical = await MemoryEvaluationProviderTests.Fixture.CreateAsync(mode), Clock = clock ?? TimeProvider.System };

            f.Source = new(f.Physical.H.Saga);

            await f.Physical.H.SeedAsync("selected", "memory sentinel");

            await f.Source.AdmitAsync();

            f.Request = (await f.Source.RequestAsync("selected")).Request with { PairCount = 3, Checks = [new MemoryEvaluationRegexCheck("yes")] };

            var issuer = new OperatorAuthorityContextIssuer(f.Authority);

            var services = new ServiceCollection().AddSingleton<IOperatorAuthorityContextIssuer>(issuer);

            // Every observer gets its own admitted fixture context, never the runner's connection.
            GrimoireFixture databaseFactory = new();

            services.AddScoped(_ => databaseFactory.CreateContext(f.Source.Connection.DataSource));

            services.AddScoped<ILongRunningOperationStore>(sp => new LongRunningOperationStore(sp.GetRequiredService<ArcanumDbContext>(), TestOrdinaryConnectionFactory.For(sp.GetRequiredService<ArcanumDbContext>())));

            services.AddScoped<ILongRunningOperationCoordinator>(sp => new LongRunningOperationCoordinator(sp.GetRequiredService<ILongRunningOperationStore>(), f.Clock));

            services.AddScoped<ICovenantConnectionSource>(sp => new CovenantConnectionSource(sp.GetRequiredService<ArcanumDbContext>(), TestOrdinaryConnectionFactory.For(sp.GetRequiredService<ArcanumDbContext>())));
            services.AddScoped<IMemoryEvaluationEvidenceStore, MemoryEvaluationEvidenceStore>();
            f._services = services.BuildServiceProvider();

            var http = f.Http = new DefaultHttpContext { RequestServices = f._services };

            var authority = issuer.Issue(CovenantAuthorityRequirement.ProtectedRead).Value;

            CovenantRequestFeatures.RecordAuthority(http, new(authority, authority.Requirement, authority.AuthorityEpoch));

            f.Scope = (await new MemoryEvaluationRequestScopeFactory(new GlobalResolver(), f.Source.Gate, issuer).OpenAsync(http, new(MemoryEvaluationScopeKind.Global), default)).Value;

            f.Reader = new(new MemoryEvaluationSourceReader(new Connections(f.Source.Connection), f.Source.Gate, f.Clock));

            f.Evidence = new(new Connections(f.Source.Connection));

            f.Operations = new(f.Physical.H.Saga.Context, TestOrdinaryConnectionFactory.For(f.Physical.H.Saga.Context));

            f.Journal = new(f);

            f.Publications = new(f.Evidence);

            f.Coordinator = new(new LongRunningOperationCoordinator(f.Operations, f.Clock));

            f.Runner = new(f.Reader, f.Publications, new ObservedProvider(f), f.Operations, f.Coordinator, f.Journal,
                f._services.GetRequiredService<IServiceScopeFactory>(), f.Slot, new(), f.Clock, new Host());

            return f;
        }

        internal AsyncServiceScope OpenMetadataScope() => _services.CreateAsyncScope();

        internal MemoryEvaluationRunner MetadataRunner(IServiceProvider services, Func<Task>? missingOwnership = null) => new(Reader,
            new CountingEvidence(services.GetRequiredService<IMemoryEvaluationEvidenceStore>()) { AfterMissingOwnership = missingOwnership }, new ObservedProvider(this),
            services.GetRequiredService<ILongRunningOperationStore>(), services.GetRequiredService<ILongRunningOperationCoordinator>(), Journal,
            _services.GetRequiredService<IServiceScopeFactory>(), Slot, new(), Clock, new Host());

        public async ValueTask DisposeAsync()
        {
            await Scope.DisposeAsync();

            await Source.Lease.DisposeAsync();

            await _services.DisposeAsync();

            await Physical.DisposeAsync();
        }
    }

    private sealed class CountingEvidence(IMemoryEvaluationEvidenceStore inner) : IMemoryEvaluationEvidenceStore
    {
        internal int CompleteAttempts;

        internal Func<Task>? AfterRegister;

        internal Func<Task>? AfterMissingOwnership;

        public async Task<Result<MemoryEvaluationStoredEvidence>> RegisterAsync(MemoryEvaluationRegistration registration, ImmutableArray<MemoryEvaluationSourceCoordinate> sources, CancellationToken ct)
        {
            var result = await inner.RegisterAsync(registration, sources, ct);
            if (result.IsSuccess && AfterRegister is not null) await AfterRegister();
            return result;
        }

        public async Task<Result<MemoryEvaluationOwnership>> ReadOwnershipAsync(Guid evaluationId, CancellationToken ct)
        {
            var result = await inner.ReadOwnershipAsync(evaluationId, ct);
            if (result.IsFailure && result.Error.Code == "MemoryEvaluation.NotFound" && AfterMissingOwnership is not null) await AfterMissingOwnership();
            return result;
        }

        public Task<Result<MemoryEvaluationStoredEvidence>> GetAsync(Guid evaluationId, CancellationToken ct) => inner.GetAsync(evaluationId, ct);

        public Task<Result<MemoryEvaluationStoredEvidence>> CompleteAsync(Guid evaluationId, MemoryEvaluationTerminalReport report, CancellationToken ct)
        {
            CompleteAttempts++;

            return inner.CompleteAsync(evaluationId, report, ct);
        }

        public Task<Result<MemoryEvaluationDeleteResult>> DeleteAsync(MemoryEvaluationDeleteRequest request, CancellationToken ct) => inner.DeleteAsync(request, ct);
    }

    private sealed class ObservedCoordinator(ILongRunningOperationCoordinator inner) : ILongRunningOperationCoordinator
    {
        internal Func<Task>? AfterStart;

        internal Func<Task>? BeforeComplete;

        internal Func<Task>? AfterComplete;

        public Task<LongRunningOperationLeaseResult> StartAsync(LongRunningOperationCreateRequest request, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => inner.StartAsync(request, ownerId, leaseDuration, cancellationToken);

        public async Task<Result<LongRunningOperationRequestIdentityResult>> StartWithRequestIdentityAsync(LongRunningOperationCreateRequest request, LongRunningOperationRequestIdentity identity, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            var result = await inner.StartWithRequestIdentityAsync(request, identity, ownerId, leaseDuration, cancellationToken);
            if (result.IsSuccess && AfterStart is not null) await AfterStart();
            return result;
        }

        public Task<bool> HeartbeatAsync(Guid operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => inner.HeartbeatAsync(operationId, ownerId, leaseDuration, cancellationToken);

        public Task<bool> CheckpointAsync(Guid operationId, string ownerId, int expectedCheckpointVersion, int checkpointVersion, byte[]? checkpointPayload, string? checkpointReference, string publicSummary, CancellationToken cancellationToken = default) => inner.CheckpointAsync(operationId, ownerId, expectedCheckpointVersion, checkpointVersion, checkpointPayload, checkpointReference, publicSummary, cancellationToken);

        public async Task<bool> CompleteAsync(Guid operationId, string ownerId, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (BeforeComplete is not null) await BeforeComplete();

            bool completed = await inner.CompleteAsync(operationId, ownerId, expectedRevision, cancellationToken);

            if (completed && AfterComplete is not null) await AfterComplete();

            return completed;
        }

        public Task<bool> FailAsync(Guid operationId, string ownerId, long expectedRevision, string errorCode, CancellationToken cancellationToken = default) => inner.FailAsync(operationId, ownerId, expectedRevision, errorCode, cancellationToken);
    }

    private sealed class ObservedProvider(Fixture fixture) : IMemoryEvaluationProvider
    {
        private int _sends;

        public Task<Result<MemoryEvaluationExecutionPlan>> PrepareAsync(MemoryEvaluationValidatedRequest request, MemoryEvaluationSourceSnapshot snapshot, MemoryEvaluationPromptPair prompts, CancellationToken ct) => fixture.Physical.Provider.PrepareAsync(request, snapshot, prompts, ct);

        public Task<Result> ValidateAsync(MemoryEvaluationExecutionPlan plan, CancellationToken ct) => fixture.Physical.Provider.ValidateAsync(plan, ct);

        public async Task<Result<MemoryEvaluationArmResult>> SendAsync(MemoryEvaluationExecutionPlan plan, int pair, MemoryEvaluationArm arm, ITurnBudget budget, Func<MemoryEvaluationSendEvidence, CancellationToken, Task> before, CancellationToken ct)
        {
            fixture.DispatchToken = ct;

            var result = await fixture.Physical.Provider.SendAsync(plan, pair, arm, budget, before, ct);

            _sends++;

            if (fixture.AfterSend is not null) await fixture.AfterSend(_sends);

            return result;
        }
    }

    private sealed class Journal(Fixture fixture) : ICovenantDisclosureJournal
    {
        private readonly CovenantDisclosureTransactionWriter _writer = new(Guid.NewGuid());

        internal int Acknowledgements;

        internal Func<Task>? After;

        public async ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(CovenantDisclosureDraft draft, CovenantDisclosureEffectCategory category, ProviderCallSensitivity sensitivity, CancellationToken ct)
        {
            var evidence = (await fixture.Evidence.GetAsync(fixture.Request.EvaluationId, ct)).Value;

            Assert.Null(evidence.Report);

            var operation = (await fixture.Operations.GetAsync(evidence.Registration.OperationId, ct))!;

            Assert.Equal(1, operation.CheckpointVersion);

            var checkpoint = MemoryEvaluationCheckpointCodec.Decode(operation.CheckpointPayload)!;

            Assert.Equal(3, checkpoint.PairCount);

            Assert.Equal(evidence.Registration.PlanDigest, checkpoint.PlanDigest);

            Assert.Equal(CovenantDisclosureSubjectKind.Operation, draft.SubjectKind);

            Assert.Equal(operation.Id, draft.SubjectId);

            Assert.Equal(Acknowledgements, fixture.Physical.Server.Bodies.Count);

            var receipt = await _writer.AcknowledgeAsync(fixture.Source.Connection, draft, category, sensitivity, ct);

            Assert.True(receipt.IsSuccess, receipt.Error.Message);

            Acknowledgements++;

            if (After is not null) await After();

            return receipt;
        }
    }

    private sealed class CountingReader(IMemoryEvaluationSourceReader inner) : IMemoryEvaluationSourceReader
    {
        internal int Reads;

        public Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryEvaluationValidatedRequest request, ArcanumInvocationContext invocation, CovenantTurnLease lease, CancellationToken ct)
        { Reads++; return inner.ReadAsync(request, invocation, lease, ct); }
        public Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct) => inner.ValidateAsync(snapshot, lease, ct);
    }

    private sealed class Connections(SqliteConnection connection) : ICovenantConnectionSource
    {
        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken ct) => ValueTask.FromResult(connection);

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken ct) => ValueTask.FromResult(connection);
    }

    private sealed class GlobalResolver : ICanonicalCampaignContextResolver
    {
        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken ct) => ValueTask.FromResult(Result<CanonicalCampaignContext>.Success(CanonicalCampaignContext.GlobalOnly));
    }

    private sealed class Host : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;

        public CancellationToken ApplicationStopping => default;

        public CancellationToken ApplicationStopped => default;

        public void StopApplication() { }
    }
}
