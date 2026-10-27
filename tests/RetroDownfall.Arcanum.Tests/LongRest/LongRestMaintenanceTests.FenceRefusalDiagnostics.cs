using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData("source", "LongRest.Unavailable", LongRestFaultClass.ResumeLater)]
    [InlineData("configuration", "LongRest.ConfigurationChanged", LongRestFaultClass.RestartGeneration)]
    [InlineData("worker", "LongRest.Unavailable", LongRestFaultClass.ResumeLater)]
    public async Task Actual_post_sent_fence_refusal_preserves_its_code_without_unknown_usage(string mutation, string code, LongRestFaultClass fault)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        int marked = 0;

        f.AfterMarkedSent = async (_, _) =>
        {
            marked++;

            EffectRow sent = Assert.Single(await EffectRowsAsync(f));

            Assert.Equal((long)LongRestDispatchAttemptState.Sent, sent.State);

            Assert.Equal(1, sent.Sent);

            if (mutation == "worker") await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseOwner = 'other-worker';");
            else await MutateAsync(f, mutation);
        };

        Result<LongRestSweepProgress> result = await f.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(code, result.Error.Code);

        Assert.Equal(fault, LongRestFaultPolicy.Classify(result.Error.Code));

        Assert.Equal(1, marked);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        // The real source-mutation triggers retire the generation and cascade its work/effects.
        // No accounting/cleanup path may resurrect that deleted attempt.
        if (mutation == "source")
        {
            Assert.Empty(await EffectRowsAsync(f));

            Assert.Equal(7L, await f.H.ScalarAsync("SELECT PhaseCode FROM long_rest_discovery_generations;"));

            return;
        }

        EffectRow row = Assert.Single(await EffectRowsAsync(f));

        Assert.Equal(6, row.State);

        Assert.Equal(0, row.Failures);

        Assert.Equal(1, row.Sent);

        Assert.Equal(0, row.Paid);

        Assert.True(row.CompletionNull);

        Assert.Equal(0, row.InputTokens);

        Assert.Equal(0, row.OutputTokens);

        if (mutation == "worker")
        {
            f.AfterMarkedSent = null;

            Assert.True((await f.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None)).IsFailure);

            Assert.Equal(row, Assert.Single(await EffectRowsAsync(f)));

            Assert.Equal(0, f.Handler.SendCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_code_owned_no_send_refusals_do_not_spend_unknown_cap_and_then_allow_paid_progress(bool embedding)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        f.AttemptValidationRefusal = attempt => attempt.State == LongRestDispatchAttemptState.Sent
            && (!embedding || attempt.PayloadKind == LongRestProviderPayloadKind.Embedding)
            ? Result.Failure(new Error(ErrorCodes.LongRest.StaleInput, "Private store reason must not be logged.")) : null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            Result<LongRestSweepProgress> refused = await f.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None);

            Assert.True(refused.IsFailure);

            Assert.Equal(ErrorCodes.LongRest.StaleInput, refused.Error.Code);

            Assert.Equal(LongRestFaultClass.RestartGeneration, LongRestFaultPolicy.Classify(refused.Error.Code));

            EffectRow row = Assert.Single(await EffectRowsAsync(f));

            Assert.Equal(6, row.State);

            Assert.Equal(0, row.Failures);

            Assert.Equal(attempt + (embedding ? 1 : 0), row.Sent);

            Assert.Equal(embedding ? 1 : 0, row.Paid);

            Assert.True(row.CompletionNull);

            Assert.Equal(embedding ? 1 : 0, f.Handler.SendCount);
        }

        Assert.Equal(embedding ? 1L : 0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        f.AttemptValidationRefusal = null;

        Assert.True((await f.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None)).IsSuccess);

        Assert.Equal(2, f.Handler.SendCount);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT sum(FailureCount) FROM long_rest_provider_effects;"));
    }

    [Theory]
    [InlineData(false, "LongRest.StaleInput")]
    [InlineData(true, "LongRest.ConfigurationChanged")]
    public async Task Actual_clean_fence_refusal_abandons_instead_of_parking_needs_operator(bool configuration, string code)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        List<LongRestUnresolvedSend> settlements = [];

        f.SettleUnresolved = (_, outcome, _) => { settlements.Add(outcome); return null; };

        if (configuration) f.AfterMarkedSent = (_, _) => MutateAsync(f, "configuration");
        else f.AttemptValidationRefusal = attempt => attempt.State == LongRestDispatchAttemptState.Sent
            ? Result.Failure(new Error(ErrorCodes.LongRest.StaleInput, "private-store-reason")) : null;

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        LongRunningOperation retired = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, retired.State);

        Assert.Equal(code, retired.TerminalErrorCode);

        Assert.Equal(code, progress.DeferredReason);

        Assert.Null(retired.LeaseOwner);

        Assert.NotNull(retired.CompletedAt);

        Assert.Equal(LongRestUnresolvedSend.NotSent, Assert.Single(settlements));

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT COALESCE(sum(FailureCount), 0) FROM long_rest_provider_effects;"));

        f.AfterMarkedSent = null;

        f.AttemptValidationRefusal = null;

        var fresh = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(fresh.IsSuccess, fresh.Error.Code);

        Assert.NotEqual(f.Operation, fresh.Value.Id);

        Assert.Equal(LongRunningOperationState.Pending, fresh.Value.State);

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task Same_type_from_run_creation_is_not_a_send_fence_observation()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        f.Starting = () => throw new LongRestBeforeSendRefusal(new(ErrorCodes.LongRest.StaleInput, "private-forged-start-body"), "private-forged-type");

        Result<LongRestSweepProgress> result = await f.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Hub.Error, result.Error.Code);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Empty(await EffectRowsAsync(f));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM InferenceRuns;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SDK_same_refusal_type_and_code_remain_a_real_unknown_send(bool embedding, bool observedType)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        Error forged = new(ErrorCodes.LongRest.StaleInput, "private-forged-provider-body");

        Exception failure = observedType ? new LongRestBeforeSendRefusal(forged, "private-forged-type") : new LongRestFenceRefusal(forged);

        if (embedding) f.PaidRecorded = () => f.Handler.TransportFailure = failure;
        else f.Handler.TransportFailure = failure;

        Result<LongRestSweepProgress> result = await f.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.ProviderUnavailable, result.Error.Code);

        Assert.Equal(embedding ? 2 : 1, f.Handler.SendCount);

        EffectRow row = Assert.Single(await EffectRowsAsync(f));

        Assert.Equal(6, row.State);

        Assert.Equal(1, row.Failures);

        Assert.Equal(embedding ? 2 : 1, row.Sent);

        Assert.Equal(embedding ? 1 : 0, row.Paid);

        Assert.True(row.CompletionNull);

        Assert.Equal(embedding ? 1L : 0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_same_type_refusal_preserves_actual_paid_usage_instead_of_claiming_not_sent(bool finalAccounting)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        LongRestBeforeSendRefusal failure = new(new(ErrorCodes.LongRest.StaleInput, "private-forged-accounting-body"), "private-forged-type");

        if (finalAccounting) f.Completing = () => throw failure;
        else f.PaidRecorded = () => throw failure;

        Result<LongRestSweepProgress> result = await f.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(finalAccounting ? ErrorCodes.LongRest.AccountingCompletionRefused : ErrorCodes.LongRest.AccountingRefused, result.Error.Code);

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        EffectRow row = Assert.Single(await EffectRowsAsync(f));

        Assert.Equal((long)LongRestDispatchAttemptState.Invalid, row.State);

        Assert.Equal(0, row.Failures);

        Assert.Equal(1, row.Paid);

        Assert.Equal(17, row.InputTokens);

        Assert.Equal(3, row.OutputTokens);

        Assert.False(row.CompletionNull);
    }

    [Fact]
    public async Task Cancellation_after_sent_keeps_ambiguous_sent_for_recovery_without_provider_dispatch()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = await CaptureAsync(f);

        using CancellationTokenSource caller = new();

        f.AfterMarkedSent = (_, _) => { caller.Cancel(); return Task.CompletedTask; };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.ProcessAsync(f.Operation, "fixture-worker", capability, caller.Token));

        EffectRow row = Assert.Single(await EffectRowsAsync(f));

        Assert.Equal((long)LongRestDispatchAttemptState.Sent, row.State);

        Assert.Equal(0, row.Failures);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }
}
