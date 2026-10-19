using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

[Collection("Grimoire")]
public sealed class MemoryEvaluationProviderTests
{
    [Fact]
    public async Task ExactFrozenArmsReachWireOnceWithEqualOutputLimitsAndRealBills()
    {
        await using var f = await Fixture.CreateAsync();

        var plan = (await f.Prepare()).Value;

        List<MemoryEvaluationSendEvidence> acknowledged = [];

        CovenantDisclosureTransactionWriter journal = new(Guid.NewGuid());

        Guid subject = Guid.NewGuid();

        Guid installation = Guid.NewGuid();

        Exception? callbackFailure = null;

        foreach (MemoryEvaluationArm arm in new[] { MemoryEvaluationArm.Control, MemoryEvaluationArm.Treatment })
        {
            var sent = await f.Provider.SendAsync(plan, 0, arm, UnrestrictedTurnBudget.Instance, async (e, ct) =>
            {
                Assert.Equal(acknowledged.Count, f.Server.Bodies.Count);

                var effect = CovenantDigests.ProviderDispatchEffect(new(subject, (ulong)e.SendIndex + 1,
                    MemoryEvaluationDigests.ExecutionPlan(plan), e.Envelope.Digest, e.DestinationDigest));

                var draft = new CovenantDisclosureDraft(installation, CovenantDisclosureSubjectKind.Operation, subject, effect,
                    CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable,
                    e.DestinationDigest, e.SensitivityDigest, null, MemoryEvaluationDigests.ExecutionPlan(plan), null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                RetroDownfall.Arcanum.Core.Primitives.Result<CovenantDisclosureReceipt> receipt;

                try { receipt = await journal.AcknowledgeAsync(f.H.Connection, draft, CovenantDisclosureEffectCategory.ProviderDispatch, e.Envelope.Sensitivity, ct); }
                catch (Exception exception) { callbackFailure = exception; throw; }

                Assert.True(receipt.IsSuccess, receipt.Error.Message);

                acknowledged.Add(e);

                Assert.Equal((long)acknowledged.Count, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));
            }, CancellationToken.None);

            Assert.Null(callbackFailure);

            Assert.True(sent.IsSuccess, sent.Error.Message);

            Assert.True(sent.Value.IsValid);

            Assert.True(sent.Value.UsageKnown);

            Assert.Equal("yes", sent.Value.Answer);

            Assert.Equal(10, sent.Value.Usage.InputTokens);

            Assert.Equal(2, sent.Value.Usage.OutputTokens);

            Assert.Equal(0.000026m, sent.Value.Usage.Cost);
        }

        Assert.Equal(2, f.Server.Bodies.Count);

        string[] bodies = f.Server.Bodies.ToArray();

        for (int i = 0; i < 2; i++)
        {
            using var wire = JsonDocument.Parse(bodies[i]);

            var root = wire.RootElement;

            Assert.Equal("model", root.GetProperty("model").GetString());

            Assert.False(root.TryGetProperty("tools", out _));

            Assert.False(root.TryGetProperty("prompt_cache_key", out _));

            Assert.Equal(512, root.GetProperty("max_completion_tokens").GetInt32());

            Assert.Equal(2, root.GetProperty("messages").GetArrayLength());

            Assert.Equal(i == 0 ? "system é" : "system é\n\nselected λ", root.GetProperty("messages")[0].GetProperty("content").GetString());

            Assert.Equal("user 🧙", root.GetProperty("messages")[1].GetProperty("content").GetString());

            Assert.Empty(acknowledged[i].Envelope.ToolDefinitions);
        }

        Assert.Empty(acknowledged[0].Envelope.PromptSpans);

        var span = Assert.Single(acknowledged[1].Envelope.PromptSpans);

        Assert.Equal(8u, span.Utf16Start);

        Assert.Equal(12u, span.Utf16Length);

        Assert.Equal(CovenantPromptAttribution.CovenantConfirmed, span.Attribution);

        Assert.Equal(ContentSensitivity.None, acknowledged[0].Envelope.Sensitivity.Level);

        Assert.Equal(ContentSensitivity.CovenantDerived, acknowledged[1].Envelope.Sensitivity.Level);

        Assert.Equal(f.Generation, Assert.Single(acknowledged[1].Envelope.Sensitivity.Provenance.ExactGenerationIds));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations WHERE Status = 0;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BudgetReservations WHERE Status IN (0, 4);"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(DISTINCT ReservedUsd) FROM BudgetReservations;"));
    }

    [Theory]
    [InlineData("clone")]
    [InlineData("aba")]
    [InlineData("callback")]
    [InlineData("callback-aba")]
    public async Task RefusedPlanOrFenceNeverSendsOrBills(string mode)
    {
        await using var f = await Fixture.CreateAsync();

        var plan = (await f.Prepare()).Value;

        if (mode == "clone") plan = plan with { };

        if (mode == "aba") f.Aba();

        try
        {
            var sent = await f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance, (_, _) =>
            {
                if (mode == "callback") throw new InvalidOperationException("PRIVATE_CALLBACK_SENTINEL");

                if (mode == "callback-aba") f.Aba();

                return Task.CompletedTask;
            }, CancellationToken.None);

            Assert.True(sent.IsFailure || !sent.Value.IsValid);
        }
        catch (InvalidOperationException) when (mode == "callback") { }

        Assert.Empty(f.Server.Bodies);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }

    [Theory]
    [InlineData("missing-usage", MemoryEvaluationReason.UsageFailure)]
    [InlineData("wrong-model", MemoryEvaluationReason.ModelMismatch)]
    [InlineData("missing-model", MemoryEvaluationReason.ModelMismatch)]
    [InlineData("audio", MemoryEvaluationReason.InvalidOutput)]
    [InlineData("truncated", MemoryEvaluationReason.InvalidOutput)]
    [InlineData("refusal", MemoryEvaluationReason.InvalidOutput)]
    [InlineData("tool", MemoryEvaluationReason.InvalidOutput)]
    [InlineData("overflow", MemoryEvaluationReason.InvalidOutput)]
    public async Task InvalidPaidResponsesRetainKnownUsageAndUnknownUsageNeverCreatesZeroBill(string mode, MemoryEvaluationReason reason)
    {
        await using var f = await Fixture.CreateAsync(mode);

        var plan = (await f.Prepare()).Value;

        var sent = await f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance, (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.True(sent.IsSuccess, sent.Error.Message);

        Assert.False(sent.Value.IsValid);

        Assert.Equal(reason, sent.Value.Reason);

        Assert.Single(f.Server.Bodies);

        if (mode == "missing-usage")
        {
            Assert.False(sent.Value.UsageKnown);

            Assert.Null(sent.Value.Usage.InputTokens);

            Assert.Null(sent.Value.Usage.OutputTokens);

            Assert.Null(sent.Value.Usage.Cost);

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

            Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BudgetReservations WHERE Status = 4;"));

            await f.Reservations.SweepExpiredAsync(DateTimeOffset.UtcNow.AddDays(2));

            Assert.True(await f.Reservations.GetTodayOutstandingReservationsAsync() > 0m);
        }
        else
        {
            Assert.True(sent.Value.UsageKnown);

            Assert.Equal(0.000026m, sent.Value.Usage.Cost);

            Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
        }

        var next = await f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Treatment, UnrestrictedTurnBudget.Instance, (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.True(next.IsFailure);

        Assert.Single(f.Server.Bodies);
    }

    [Fact]
    public async Task ContextOverflowIsRejectedBeforeAccounting()
    {
        await using var f = await Fixture.CreateAsync();

        f.Options.CurrentValue.Providers[0].ContextWindowLimit = 1;

        f.Options.Set(f.Options.CurrentValue);

        Assert.True((await f.Prepare()).IsFailure);

        Assert.Empty(f.Server.Bodies);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM InferenceRuns;"));
    }

    [Fact]
    public async Task ChangedControlPromptFailsPreparationAndDailyAdmissionPrecedesAcknowledgement()
    {
        await using var f = await Fixture.CreateAsync();

        var plan = (await f.Prepare()).Value;

        var changed = plan.Prompts with { ControlSystemPrompt = plan.Prompts.ControlSystemPrompt + "changed" };

        Assert.True((await f.Provider.PrepareAsync(plan.Request, plan.Sources, changed, CancellationToken.None)).IsFailure);

        f.Options.CurrentValue.Cost.Budget.DailyLimitUsd = 0.000001m;

        f.Options.Set(f.Options.CurrentValue);

        plan = (await f.Prepare()).Value;

        int acknowledged = 0;

        var sent = await f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance,
            (_, _) => { acknowledged++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(sent.IsFailure);

        Assert.Equal(0, acknowledged);

        Assert.Empty(f.Server.Bodies);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }

    [Theory]
    [InlineData(ReasoningEffortLevel.Minimal, "minimal")]
    [InlineData(ReasoningEffortLevel.High, "high")]
    public async Task FinalMappedOptionsMatchTheAcknowledgedEnvelope(ReasoningEffortLevel effort, string wireEffort)
    {
        await using var f = await Fixture.CreateAsync();

        f.Options.CurrentValue.Features.Reasoning = true;

        f.Options.CurrentValue.Providers[0].Models = [new ModelEntry("model", Reasoning: new ModelReasoningSettings { WireDialect = ReasoningWireDialect.Standard })];

        f.Options.Set(f.Options.CurrentValue);

        var plan = (await f.Prepare(new(Temperature: 0.75, TopP: 0.5, Seed: 42, ReasoningEffort: effort))).Value;

        MemoryEvaluationSendEvidence? acknowledged = null;

        var result = await f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance,
            (e, _) => { acknowledged = e; return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        using var wire = JsonDocument.Parse(Assert.Single(f.Server.Bodies));

        Assert.Equal(wireEffort, wire.RootElement.GetProperty("reasoning_effort").GetString());

        Assert.Equal(0.75, wire.RootElement.GetProperty("temperature").GetDouble());

        Assert.Equal(0.5, wire.RootElement.GetProperty("top_p").GetDouble());

        Assert.Equal(42, wire.RootElement.GetProperty("seed").GetInt64());

        Assert.Equal(wire.RootElement.GetProperty("temperature").GetDouble(), acknowledged!.Envelope.Options.Temperature);

        Assert.Equal(wire.RootElement.GetProperty("top_p").GetDouble(), acknowledged.Envelope.Options.TopP);

        Assert.Equal(wire.RootElement.GetProperty("seed").GetInt64(), acknowledged.Envelope.Options.Seed);

        Assert.Equal(wireEffort, acknowledged.Envelope.Options.ReasoningEffort!.Value.ToString().ToLowerInvariant());

        Assert.Equal("system é", Encoding.UTF8.GetString(acknowledged.Envelope.SystemPromptBytes.AsSpan()));
    }

    [Theory]
    [InlineData("server-error")]
    [InlineData("redirect")]
    [InlineData("malformed")]
    public async Task FailedPhysicalRequestIsNeverRetriedOrReleasedAsUnsent(string mode)
    {
        await using var f = await Fixture.CreateAsync(mode);

        var plan = (await f.Prepare()).Value;

        var result = await f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance,
            (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.IsFailure || !result.Value.IsValid);

        Assert.Single(f.Server.Bodies);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BudgetReservations WHERE Status = 4;"));
    }

    [Fact]
    public async Task ActiveCancellationKeepsUnknownHoldAndStopsFurtherSends()
    {
        await using var f = await Fixture.CreateAsync("wait");

        var plan = (await f.Prepare()).Value;

        using CancellationTokenSource cancellation = new();

        var sending = f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance,
            (_, _) => Task.CompletedTask, cancellation.Token);

        await f.Server.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BudgetReservations WHERE Status = 4;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.True((await f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Treatment, UnrestrictedTurnBudget.Instance,
            (_, _) => Task.CompletedTask, CancellationToken.None)).IsFailure);

        Assert.Single(f.Server.Bodies);
    }

    [Fact]
    public async Task FailedSettlementCannotLeavePlanUsableAfterKnownPaidAnswer()
    {
        await using var f = await Fixture.CreateAsync();

        await f.H.ExecuteAsync("CREATE TRIGGER fail_evaluation_settlement BEFORE UPDATE OF Status ON BudgetReservations WHEN NEW.Status = 1 BEGIN SELECT RAISE(ABORT, 'settlement blocked'); END;");

        var plan = (await f.Prepare()).Value;

        await Assert.ThrowsAnyAsync<Exception>(() => f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Control,
            UnrestrictedTurnBudget.Instance, (_, _) => Task.CompletedTask, CancellationToken.None));

        Assert.True((await f.Provider.ValidateAsync(plan, CancellationToken.None)).IsFailure);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BudgetReservations WHERE Status = 4;"));
    }

    [Fact]
    public async Task FixedAlternatingOrderAndRepeatedArmNeverAddAnotherCharge()
    {
        await using var f = await Fixture.CreateAsync();

        var plan = (await f.Prepare()).Value;

        Assert.Equal(new[] { MemoryEvaluationArm.Control, MemoryEvaluationArm.Treatment, MemoryEvaluationArm.Treatment, MemoryEvaluationArm.Control }, plan.SendOrder.Take(4));

        for (int index = 0; index < 4; index++)
        {
            var result = await f.Provider.SendAsync(plan, index / 2, plan.SendOrder[index], UnrestrictedTurnBudget.Instance,
                (_, _) => Task.CompletedTask, CancellationToken.None);

            Assert.True(result.IsSuccess && result.Value.IsValid);
        }

        Assert.True((await f.Provider.SendAsync(plan, 1, MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance,
            (_, _) => Task.CompletedTask, CancellationToken.None)).IsFailure);

        Assert.Equal(4, f.Server.Bodies.Count);

        Assert.Equal(4L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }

    [Fact]
    public async Task ReservationCoversOutputWhenReasoningHasHigherPriceWithoutEnlargingAnswerBudget()
    {
        await using var f = await Fixture.CreateAsync();

        f.Options.CurrentValue.Cost.Pricing.DefaultPricing.ReasoningPer1M = 1000m;

        f.Options.Set(f.Options.CurrentValue);

        var plan = (await f.Prepare()).Value;

        var result = await f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance,
            async (_, _) =>
            {
                decimal held = decimal.Parse((string)(await f.H.ScalarAsync("SELECT ReservedUsd FROM BudgetReservations;"))!, System.Globalization.CultureInfo.InvariantCulture);

                Assert.True(held >= 0.512m);

                await Task.CompletedTask;
            }, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        using var wire = JsonDocument.Parse(Assert.Single(f.Server.Bodies));

        Assert.Equal(512, wire.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public async Task DisabledFinancialLimitStillRequiresAccountingAndDoesNotInventPaidZeroUsage()
    {
        await using var f = await Fixture.CreateAsync("missing-usage");

        f.Options.CurrentValue.Cost.Budget.Enabled = false;

        f.Options.Set(f.Options.CurrentValue);

        var plan = (await f.Prepare()).Value;

        var result = await f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance,
            (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(MemoryEvaluationReason.UsageFailure, result.Value.Reason);

        Assert.Null(result.Value.Usage.Cost);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BudgetReservations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM InferenceRuns;"));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("fingerprint")]
    [InlineData("reserve")]
    public async Task ExecutorRejectsChangedAdmittedIdentityBeforeCallbackAndWire(string mutation)
    {
        await using var f = await Fixture.CreateAsync();

        var plan = (await f.Prepare()).Value;

        var provider = f.Epoch.Capture().Read().Providers[0];

        ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        string model = mutation == "model" ? "other" : "model";

        var breakdown = estimator.EstimateMemoryEvaluation(provider, model, plan.Prompts.ControlSystemPrompt, plan.Prompts.ControlUserPrompt, 512);

        if (mutation == "fingerprint") breakdown = breakdown with { PayloadFingerprint = "changed" };

        using var lease = (await f.Clients.ResolveChatAsync(provider, "model", CancellationToken.None)).Value;

        var context = new ModelCallContext(provider, model, mutation == "reserve" ? 100 : 512, 0, breakdown);

        int acknowledged = 0;

        var result = await new ModelCallExecutor(estimator).ExecuteMemoryEvaluationAsync(lease.ChatClient, plan, 0,
            MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance,
            (_, _) => { acknowledged++; return Task.CompletedTask; }, _ => Task.CompletedTask, context, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, acknowledged);

        Assert.Empty(f.Server.Bodies);
    }

    [Theory]
    [InlineData("empty-usage", null, null, false)]
    [InlineData("input-only-usage", 10L, null, false)]
    [InlineData("output-only-usage", null, 2L, false)]
    [InlineData("zero-usage", 0L, 0L, true)]
    public async Task RawPrimaryUsagePresenceSurvivesSdkDefaults(string mode, long? input, long? output, bool known)
    {
        await using var f = await Fixture.CreateAsync(mode);

        var plan = (await f.Prepare()).Value;

        var sent = await f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance,
            (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.True(sent.IsSuccess, sent.Error.Message);

        Assert.Equal(known, sent.Value.IsValid);

        Assert.Equal(known, sent.Value.UsageKnown);

        Assert.Equal(input, sent.Value.Usage.InputTokens);

        Assert.Equal(output, sent.Value.Usage.OutputTokens);

        Assert.Single(f.Server.Bodies);

        Assert.Equal(known ? 1L : 0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations WHERE Status = 0;"));

        Assert.Equal(known ? 0L : 1L, await f.H.ScalarAsync("SELECT count(*) FROM BudgetReservations WHERE Status = 4;"));

        if (known) Assert.Equal(0m, sent.Value.Usage.Cost);
        else
        {
            Assert.Null(sent.Value.Usage.Cost);

            Assert.Equal(MemoryEvaluationReason.UsageFailure, sent.Value.Reason);

            Assert.True((await f.Provider.ValidateAsync(plan, CancellationToken.None)).IsFailure);
        }
    }

    [Theory]
    [InlineData("extra-choice")]
    [InlineData("blank-tool")]
    public async Task RawUnsupportedShapeCannotBeHiddenBySdkButStillSettlesMeasuredUsage(string mode)
    {
        await using var f = await Fixture.CreateAsync(mode);

        var plan = (await f.Prepare()).Value;

        var sent = await f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance,
            (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.True(sent.IsSuccess, sent.Error.Message);

        Assert.False(sent.Value.IsValid);

        Assert.Equal(MemoryEvaluationReason.InvalidOutput, sent.Value.Reason);

        Assert.True(sent.Value.UsageKnown);

        Assert.Equal(10L, sent.Value.Usage.InputTokens);

        Assert.Equal(2L, sent.Value.Usage.OutputTokens);

        Assert.Equal(0.000026m, sent.Value.Usage.Cost);

        Assert.Single(f.Server.Bodies);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations WHERE Status = 0;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BudgetReservations WHERE Status = 1;"));

        Assert.True((await f.Provider.ValidateAsync(plan, CancellationToken.None)).IsFailure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatchMarkerFinishesBeforeFinalFenceAndObservedAbaCannotSend(bool changeEpoch)
    {
        await using var f = await Fixture.CreateAsync();

        MarkerBarrier reservations = new(f.Reservations);

        ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        f.Provider = new(f.Clients, f.Epoch, new ModelCallExecutor(estimator), estimator, new TurnRunWriter(f.H.Saga.Context), reservations);

        var plan = (await f.Prepare()).Value;

        int acknowledged = 0;

        bool markerFinishedAtCallback = false;

        var sending = f.Provider.SendAsync(plan, 0, MemoryEvaluationArm.Control, UnrestrictedTurnBudget.Instance,
            (_, _) => { acknowledged++; markerFinishedAtCallback = reservations.Finished; return Task.CompletedTask; }, CancellationToken.None);

        await reservations.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(f.Server.Bodies);

        if (changeEpoch) f.Aba();

        reservations.Continue.TrySetResult();

        var sent = await sending;

        if (changeEpoch)
        {
            Assert.True(sent.IsFailure);

            Assert.Equal(0, acknowledged);

            Assert.Empty(f.Server.Bodies);

            Assert.True((await f.Provider.ValidateAsync(plan, CancellationToken.None)).IsFailure);

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

            // Deliberately conservative: even this proven-unsent marker stays held, never billed.
            Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BudgetReservations WHERE Status = 4;"));
        }
        else
        {
            Assert.True(sent.IsSuccess && sent.Value.IsValid);

            Assert.True(markerFinishedAtCallback);

            Assert.Equal(1, acknowledged);

            Assert.Single(f.Server.Bodies);
        }
    }

    private sealed class MarkerBarrier(IBudgetReservationService inner) : IBudgetReservationService
    {
        internal TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Finished;

        public async Task MarkPossiblyDispatchedAsync(Guid id, Guid run, CancellationToken ct = default)
        {
            await inner.MarkPossiblyDispatchedAsync(id, run, ct);

            Arrived.TrySetResult();

            await Continue.Task.WaitAsync(ct);

            Finished = true;
        }

        public Task<Result<BudgetReservation>> ReserveAsync(BudgetReservationRequest request, CancellationToken ct = default) => inner.ReserveAsync(request, ct);

        public Task<Result> AdjustAsync(Guid id, decimal amount, CancellationToken ct = default) => inner.AdjustAsync(id, amount, ct);

        public Task<Result> RecheckDailyLimitAsync(Guid id, decimal amount, CancellationToken ct = default) => inner.RecheckDailyLimitAsync(id, amount, ct);

        public Task ExtendExpiryAsync(Guid id, DateTimeOffset expiry, CancellationToken ct = default) => inner.ExtendExpiryAsync(id, expiry, ct);

        public Task ReconcileAsync(Guid id, decimal amount, CancellationToken ct = default) => inner.ReconcileAsync(id, amount, ct);

        public Task ReleaseAsync(Guid id, CancellationToken ct = default) => inner.ReleaseAsync(id, ct);

        public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken ct = default) => inner.GetTodayCommittedSpendAsync(ct);

        public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken ct = default) => inner.GetTodayOutstandingReservationsAsync(ct);

        public Task<int> SweepExpiredAsync(DateTimeOffset now, CancellationToken ct = default) => inner.SweepExpiredAsync(now, ct);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal LongRestSweepHarness H = null!;

        internal LoopbackProvider Server = null!;

        internal LongRestConfigurationEpochTests.NotifyingOptions Options = null!;

        internal MemoryEvaluationProviderEpoch Epoch = null!;

        internal MemoryEvaluationProviderClientFactory Clients = null!;

        internal MemoryEvaluationProvider Provider = null!;

        internal BudgetReservationService Reservations = null!;

        internal Guid Generation = Guid.NewGuid();

        internal static async Task<Fixture> CreateAsync(string mode = "valid")
        {
            Fixture f = new() { H = await LongRestSweepHarness.CreateAsync(), Server = new(mode) };

            ArcanumSettings settings = LongRestConfigurationEpochTests.Settings();

            settings.Providers[0].Endpoint = f.Server.Endpoint;

            settings.Cost.Budget = new() { Enabled = true, DailyLimitUsd = 100m };

            f.Options = new(settings);

            f.Epoch = new(f.Options);

            f.Clients = new(new NoKeys());

            ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

            f.Reservations = new(f.H.Saga.Context, f.Options);

            f.Provider = new(f.Clients, f.Epoch, new ModelCallExecutor(estimator), estimator, new TurnRunWriter(f.H.Saga.Context), f.Reservations);

            return f;
        }

        internal Task<RetroDownfall.Arcanum.Core.Primitives.Result<MemoryEvaluationExecutionPlan>> Prepare(MemoryEvaluationOptions? options = null)
        {
            var request = MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Workload = new("system é", "user 🧙"), Options = options ?? new() }).Value;

            var target = request.Request.Memory.Covenant[0];

            MemoryEvaluationSourceCoordinate source = new(MemoryEvaluationCoordinateRole.Intervention, MemoryEvaluationSource.Covenant,
                target.EntryId.ToString("D"), null, target.VersionId.ToString("D"), 1, target.FragmentDigest, null, null,
                MemoryEvaluationScopeKind.Global, null, null, null, null, null, MemoryEvaluationSourceLifecycle.Active,
                ContentSensitivity.CovenantDerived, null, null, Lane: CovenantLane.Confirmed, Placement: CovenantPlacement.GlobalConfirmed);

            MemoryEvaluationSourceSnapshot snapshot = new([source], "selected λ", MemoryEvaluationDigests.SourceManifest([source], []),
                Provenance: GenerationProvenance.CreateExact([Generation]), Fragments: [new(0, "selected λ", MemoryEvaluationPromptPart.System)]);

            var layout = MemoryEvaluationPromptComposer.Compose(request, snapshot).Value;

            return Provider.PrepareAsync(request, snapshot, MemoryEvaluationPromptPolicy.Derive(request, snapshot, layout).Value, CancellationToken.None);
        }

        internal void Aba()
        {
            var original = Epoch.Capture().Read();

            var other = Epoch.Capture().Read();

            other.Providers[0].Endpoint = "https://changed.test/v1";

            Options.Set(other);

            Options.Set(original);
        }

        public async ValueTask DisposeAsync()
        {
            Clients.Dispose();

            Epoch.Dispose();

            await Server.DisposeAsync();

            await H.DisposeAsync();
        }
    }

    private sealed class NoKeys : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    internal sealed class LoopbackProvider : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();

        private readonly Task _worker;

        private readonly CancellationTokenSource _stop = new();

        internal TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ConcurrentQueue<string> Bodies { get; } = new();

        internal string Endpoint { get; }

        internal LoopbackProvider(string mode)
        {
            using TcpListener port = new(IPAddress.Loopback, 0);

            port.Start();

            int number = ((IPEndPoint)port.LocalEndpoint).Port;

            port.Stop();

            Endpoint = $"http://127.0.0.1:{number}/v1";

            _listener.Prefixes.Add($"http://127.0.0.1:{number}/");

            _listener.Start();

            _worker = Run(mode);
        }

        private async Task Run(string mode)
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();

                    using StreamReader reader = new(context.Request.InputStream);

                    Bodies.Enqueue(await reader.ReadToEndAsync());

                    Arrived.TrySetResult();

                    if (mode == "wait") await Task.Delay(Timeout.Infinite, _stop.Token);

                    if (mode == "server-error") context.Response.StatusCode = 500;

                    if (mode == "redirect") { context.Response.StatusCode = 302; context.Response.RedirectLocation = Endpoint + "/chat/completions"; }

                    string model = mode == "missing-model" ? "" : mode == "wrong-model" ? "unexpected" : "model";

                    string finish = mode == "truncated" ? "length" : "stop";

                    string answer = mode == "overflow" ? new string('x', 65537) : "yes";

                    string extra = mode == "refusal" ? ",\"refusal\":\"private refusal\"" : mode == "tool" ? ",\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"bad\",\"arguments\":\"{}\"}}]" : "";

                    if (mode == "audio") extra = ",\"audio\":{\"id\":\"audio1\",\"expires_at\":2000000000,\"data\":\"YQ==\",\"transcript\":\"private audio\"}";

                    string usage = mode == "missing-usage" ? "" : ",\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}";

                    usage = mode switch
                    {
                        "empty-usage" => ",\"usage\":{}",
                        "input-only-usage" => ",\"usage\":{\"prompt_tokens\":10}",
                        "output-only-usage" => ",\"usage\":{\"completion_tokens\":2}",
                        "zero-usage" => ",\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}",
                        _ => usage,
                    };

                    if (mode == "blank-tool") extra = ",\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"\",\"arguments\":\"{}\"}}]";

                    string response = $$"""{"id":"chat1","object":"chat.completion","created":1,"model":"{{model}}","choices":[{"index":0,"message":{"role":"assistant","content":"{{answer}}"{{extra}}},"finish_reason":"{{finish}}"}]{{usage}}} """;

                    if (mode == "extra-choice") response = response.Replace("}]", "},{\"index\":1,\"message\":{\"role\":\"assistant\",\"content\":\"other\"},\"finish_reason\":\"stop\"}]");

                    byte[] bytes = Encoding.UTF8.GetBytes(mode == "malformed" ? "invalid json" : response);

                    context.Response.ContentType = "application/json";

                    context.Response.ContentLength64 = bytes.Length;

                    await context.Response.OutputStream.WriteAsync(bytes);

                    context.Response.Close();
                }
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();

            _listener.Close();

            await _worker;

            _stop.Dispose();
        }
    }
}
