using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnAccountingHandleTests
{
    [Fact]
    public async Task AmbientPush_RestoresNestedHandleAndWriterAfterException()
    {
        TurnAccountingAmbient.Clear();
        RecordingTurnRunWriter parentWriter = new();
        RecordingTurnRunWriter nestedWriter = new();
        TurnAccountingHandle parent = (await TurnAccountingHandle.BeginAsync(
            turnRunWriter: null,
            budgetReservations: null,
            new PricingSettings(),
            model: null,
            sessionId: null,
            surface: "test",
            purpose: "ambient-parent",
            requestId: "ambient-parent",
            cancellationToken: CancellationToken.None)).Value;
        TurnAccountingHandle nested = parent.CreateNestedOperationHandle();

        try
        {
            using (TurnAccountingAmbient.Push(parent, parentWriter))
            {
                Assert.Same(parent, TurnAccountingAmbient.Current);
                Assert.Same(parentWriter, TurnAccountingAmbient.Writer);

                Action nestedFailure = () =>
                {
                    using (TurnAccountingAmbient.Push(nested, nestedWriter))
                    {
                        Assert.Same(nested, TurnAccountingAmbient.Current);
                        Assert.Same(nestedWriter, TurnAccountingAmbient.Writer);
                        throw new InvalidOperationException("expected");
                    }
                };
                _ = Assert.Throws<InvalidOperationException>(nestedFailure);

                Assert.Same(parent, TurnAccountingAmbient.Current);
                Assert.Same(parentWriter, TurnAccountingAmbient.Writer);
            }

            Assert.Null(TurnAccountingAmbient.Current);
            Assert.Null(TurnAccountingAmbient.Writer);
        }
        finally
        {
            TurnAccountingAmbient.Clear();
        }
    }

    /// <summary>
    /// A delegated child hides the parent turn's accounting while it runs and gets it back after.
    /// Observed in one synchronous flow, because an <c>AsyncLocal</c> written inside an awaited async
    /// method never flows back to its caller and so cannot prove a restoration from outside.
    /// </summary>
    [Fact]
    public async Task AmbientSuspend_HidesTheHandleAndWriterThenRestoresThem()
    {
        TurnAccountingAmbient.Clear();
        RecordingTurnRunWriter writer = new();
        TurnAccountingHandle parent = (await TurnAccountingHandle.BeginAsync(
            writer,
            budgetReservations: null,
            new PricingSettings(),
            model: null,
            sessionId: null,
            surface: "test",
            purpose: "suspend",
            requestId: "suspend",
            cancellationToken: CancellationToken.None)).Value;

        try
        {
            using (TurnAccountingAmbient.Push(parent, writer))
            {
                using (TurnAccountingAmbient.Suspend())
                {
                    Assert.Null(TurnAccountingAmbient.Current);
                    Assert.Null(TurnAccountingAmbient.Writer);
                }

                Assert.Same(parent, TurnAccountingAmbient.Current);
                Assert.Same(writer, TurnAccountingAmbient.Writer);
            }
        }
        finally
        {
            TurnAccountingAmbient.Clear();
        }
    }

    /// <summary>
    /// A tool task abandoned past the grace keeps running in a flow that still holds the turn's
    /// accounting. Once that run has settled (its status written and its reservation reconciled or
    /// released), nested work there must not ledger against it: it no longer sees the settled turn's
    /// handle or writer, and accounts for itself instead.
    /// </summary>
    [Fact]
    public async Task Ambient_HidesTheHandleAndWriterOfARunThatHasSettled()
    {
        TurnAccountingAmbient.Clear();
        RecordingTurnRunWriter writer = new();
        TurnAccountingHandle turn = (await TurnAccountingHandle.BeginAsync(
            writer,
            budgetReservations: null,
            new PricingSettings(),
            model: null,
            sessionId: null,
            surface: "test",
            purpose: "settled",
            requestId: "settled",
            cancellationToken: CancellationToken.None)).Value;
        TurnAccountingHandle nested = turn.CreateNestedOperationHandle();

        try
        {
            TurnAccountingAmbient.Publish(nested, writer);

            Assert.Same(nested, TurnAccountingAmbient.Current);

            await turn.CompleteAsync(
                writer,
                budgetReservations: null,
                InferenceRunStatus.Completed,
                CancellationToken.None);

            Assert.Null(TurnAccountingAmbient.Current);
            Assert.Null(TurnAccountingAmbient.Writer);
        }
        finally
        {
            TurnAccountingAmbient.Clear();
        }
    }

    [Fact]
    public async Task BeginAsync_ReservesTypedOutputAndReasoningHeadroom()
    {
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new();
        PricingSettings pricing = new()
        {
            DefaultPricing = new ModelPricingEntry
            {
                OutputPer1M = 20m,
                ReasoningPer1M = 80m,
            },
        };
        Result<TurnAccountingHandle> result = await TurnAccountingHandle.BeginAsync(
                writer,
                reservations,
                pricing,
                "reasoner",
                null,
                "test",
                "chat",
                "request-1",
                CancellationToken.None,
                maxOutputTokens: 1_000,
                reasoningBudgetTokens: 600);

        Assert.True(result.IsSuccess);
        BudgetReservationRequest request = Assert.IsType<BudgetReservationRequest>(reservations.LastRequest);
        Assert.Equal(
            BudgetReservationService.EstimateWorstCaseTurnUsd(
                pricing.DefaultPricing,
                maxOutputTokens: 1_000,
                reasoningBudgetTokens: 600),
            request.ReservedUsd);
    }

    /// <summary>
    /// The run row is opened before the reservation, and only the <c>IsFailure</c> branch closes it.
    /// An exception out of <c>ReserveAsync</c> — cancellation, or any SQLite error the reservation
    /// service does not convert to a <c>Result</c> — must close it too, otherwise the row stays
    /// <c>Running</c> forever with no expiry sweep to reclaim it.
    /// </summary>
    [Fact]
    public async Task BeginAsync_ReservationThrows_ClosesTheRunRowBeforeRethrowing()
    {
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new()
        {
            ReserveException = new InvalidOperationException("grimoire faulted"),
        };

        async Task Begin() => await TurnAccountingHandle.BeginAsync(
            writer,
            reservations,
            new PricingSettings(),
            "reasoner",
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "request-reserve-throws",
            cancellationToken: CancellationToken.None);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(Begin);

        Assert.Equal(InferenceRunStatus.Failed, writer.CompletedStatus);
    }

    [Fact]
    public async Task EnsureReservationForContextAsync_RaisesReservationFromMaterializedInput()
    {
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new();
        PricingSettings pricing = new()
        {
            DefaultPricing = new ModelPricingEntry
            {
                InputPer1M = 10m,
                OutputPer1M = 20m,
                ReasoningPer1M = 80m,
            },
        };
        TurnAccountingHandle handle = (await TurnAccountingHandle.BeginAsync(
            writer,
            reservations,
            pricing,
            "reasoner",
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "context-reservation",
            cancellationToken: CancellationToken.None,
            maxOutputTokens: 1_000,
            reasoningBudgetTokens: 600)).Value;
        ContextTokenBreakdown breakdown = new()
        {
            Provider = "provider",
            Model = "reasoner",
            Profile = new ResolvedModelTokenizationProfile
            {
                ProfileId = "test",
                Type = ModelTokenizationProfileType.UnknownFallback,
                TokenizerId = "o200k_base",
                SafetyMarginPercent = 15,
                PerMessageOverheadTokens = 4,
                PerToolOverheadTokens = 8,
                ProviderFramingTokens = 3,
                StopTokenOverheadTokens = 1,
                UnknownImageReserveTokens = 2048,
                Confidence = 0.5,
            },
            Components =
            [
                new ContextTokenComponent(
                    ContextTokenSource.ReservedAnswer,
                    new TokenEstimate(
                        1_000,
                        TokenEstimateClassification.Reserved,
                        "test")),
                new ContextTokenComponent(
                    ContextTokenSource.ReservedReasoning,
                    new TokenEstimate(
                        600,
                        TokenEstimateClassification.Reserved,
                        "test")),
            ],
            InputTokens = 5_000,
            ReservedTokens = 1_600,
            ReservedAnswerTokens = 1_000,
            ReservedReasoningTokens = 600,
            TotalTokens = 6_600,
            OverallClassification = TokenEstimateClassification.Estimated,
            SafetyMarginTokens = 500,
        };

        Result adjusted = await handle.EnsureReservationForContextAsync(
            reservations,
            pricing,
            budget: null,
            "reasoner",
            breakdown,
            CancellationToken.None);
        Result repeated = await handle.EnsureReservationForContextAsync(
            reservations,
            pricing,
            budget: null,
            "reasoner",
            breakdown,
            CancellationToken.None);

        Assert.True(adjusted.IsSuccess);
        Assert.True(repeated.IsSuccess);
        decimal expectedPerCall =
            (5_000m * 10m / 1_000_000m)
            + (400m * 20m / 1_000_000m)
            + (600m * 80m / 1_000_000m);
        Assert.Equal(
            expectedPerCall,
            reservations.AdjustedUsd);
        Assert.Equal(1, reservations.AdjustCount);
    }

    /// <summary>
    /// R-053: once the pre-call estimate stops growing the reservation is never raised again, and the
    /// raise was the only place the daily limit was rechecked. Spend the earlier rounds already
    /// committed must still be compared with the limit before the next provider call.
    /// </summary>
    [Fact]
    public async Task EnsureReservationForContextAsync_FailsWhenAccumulatedSpendExceedsDailyLimitEvenIfEstimateDidNotGrow()
    {
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new()
        {
            CommittedSpend = () => writer.RecordedCostUsd,
        };
        PricingSettings pricing = ReasonerPricing();
        BudgetSettings budget = new() { Enabled = true, DailyLimitUsd = 1.00m };
        TurnAccountingHandle handle = await BeginReasonerTurnAsync(writer, reservations, pricing);
        ContextTokenBreakdown breakdown = ReasonerContextBreakdown();

        Result first = await handle.EnsureReservationForContextAsync(
            reservations,
            pricing,
            budget,
            "reasoner",
            breakdown,
            CancellationToken.None);

        // 95,000 input tokens at 10 USD per million: 0.95 USD of actual spend for the first round.
        await handle.RecordChatUsageAsync(
            writer,
            "provider",
            "reasoner",
            promptTokens: 95_000,
            completionTokens: 0,
            cachedTokens: 0,
            reasoningTokens: 0,
            pricing.DefaultPricing,
            CancellationToken.None);

        Result second = await handle.EnsureReservationForContextAsync(
            reservations,
            pricing,
            budget,
            "reasoner",
            breakdown,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsFailure);
        Assert.Equal(ErrorCodes.Budget.Exceeded, second.Error.Code);

        // The estimate did not grow, so the refusal came from the accumulated-spend check, not a raise.
        Assert.Equal(1, reservations.AdjustCount);
    }

    /// <summary>
    /// R-053: the reservation was admitted with a fixed one-hour lifetime and never renewed, so a
    /// turn running past it could end holding an expired reservation that reconciliation skips.
    /// Every pre-call admission now moves the expiry forward, whether or not it raised the amount.
    /// </summary>
    [Fact]
    public async Task EnsureReservationForContextAsync_RenewsTheReservationExpiryEveryRound()
    {
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new();
        PricingSettings pricing = ReasonerPricing();
        TurnAccountingHandle handle = await BeginReasonerTurnAsync(writer, reservations, pricing);
        ContextTokenBreakdown breakdown = ReasonerContextBreakdown();
        DateTimeOffset admittedUntil = reservations.LastRequest!.ExpiresAt;

        for (int round = 0; round < 2; round++)
        {
            Result admitted = await handle.EnsureReservationForContextAsync(
                reservations,
                pricing,
                new BudgetSettings { Enabled = true, DailyLimitUsd = 100m },
                "reasoner",
                breakdown,
                CancellationToken.None);

            Assert.True(admitted.IsSuccess);
        }

        Assert.Equal(2, reservations.ExtendedExpiries.Count);
        Assert.All(reservations.ExtendedExpiries, expiry => Assert.True(expiry >= admittedUntil));
    }

    [Fact]
    public async Task RecordChatUsageAsync_ReconcilesProviderReasoningUsageWithoutDoubleBilling()
    {
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new();
        PricingSettings pricingSettings = new()
        {
            DefaultPricing = new ModelPricingEntry
            {
                OutputPer1M = 20m,
                ReasoningPer1M = 80m,
            },
        };
        Result<TurnAccountingHandle> begun = await TurnAccountingHandle.BeginAsync(
            writer,
            reservations,
            pricingSettings,
            "reasoner",
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "request-2",
            cancellationToken: CancellationToken.None);
        Assert.True(begun.IsSuccess);
        TurnAccountingHandle handle = begun.Value;
        await handle.RecordChatUsageAsync(
                writer,
                "provider",
                "reasoner",
                0L,
                1_000_000L,
                0L,
                250_000L,
                pricingSettings.DefaultPricing,
                CancellationToken.None);

        BillableOperationRecord operation =
            Assert.IsType<BillableOperationRecord>(writer.LastOperation);
        Assert.Equal(250_000L, operation.ReasoningTokens);
        Assert.Equal(1_000_000L, operation.OutputTokens);
        Assert.Equal(35m, operation.ActualCostUsd);

        using JsonDocument snapshot = JsonDocument.Parse(operation.PricingSnapshotJson);
        Assert.Equal(
            80m,
            snapshot.RootElement.GetProperty("ReasoningPer1M").GetDecimal());

        await handle.CompleteAsync(
            writer,
            reservations,
            InferenceRunStatus.Completed,
            CancellationToken.None);

        Assert.Equal(35m, reservations.ReconciledUsd);
        Assert.Equal(InferenceRunStatus.Completed, writer.CompletedStatus);
    }

    [Fact]
    public async Task RecordChatUsageAsync_PersistsCachedCountRateAndReconciledCost()
    {
        RecordingTurnRunWriter writer = new();
        ModelPricingEntry pricing = new()
        {
            InputPer1M = 10m,
            CachedPer1M = 1m,
        };
        TurnAccountingHandle handle = (await TurnAccountingHandle.BeginAsync(
            writer,
            budgetReservations: null,
            new PricingSettings { DefaultPricing = pricing },
            "cache-model",
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "request-cached-input",
            cancellationToken: CancellationToken.None)).Value;

        await handle.RecordChatUsageAsync(
            writer,
            "provider",
            "cache-model",
            promptTokens: 1_000_000,
            completionTokens: 0,
            cachedTokens: 400_000,
            reasoningTokens: 0,
            pricing,
            CancellationToken.None);

        BillableOperationRecord operation =
            Assert.IsType<BillableOperationRecord>(writer.LastOperation);
        Assert.Equal(400_000, operation.CachedTokens);
        Assert.Equal(6.4m, operation.ActualCostUsd);
        Assert.Equal(6.4m, handle.AccumulatedCostUsd);
        using JsonDocument snapshot = JsonDocument.Parse(operation.PricingSnapshotJson);
        Assert.Equal(
            1m,
            snapshot.RootElement.GetProperty("CachedPer1M").GetDecimal());
    }

    [Fact]
    public async Task RecordChatUsageAsync_NullReasoningSnapshotFallsBackToOutputRate()
    {
        RecordingTurnRunWriter writer = new();
        ModelPricingEntry pricing = new()
        {
            OutputPer1M = 20m,
            ReasoningPer1M = null,
        };
        TurnAccountingHandle handle = (await TurnAccountingHandle.BeginAsync(
            writer,
            budgetReservations: null,
            new PricingSettings { DefaultPricing = pricing },
            "reasoner",
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "request-null-reasoning-rate",
            cancellationToken: CancellationToken.None)).Value;

        await handle.RecordChatUsageAsync(
            writer,
            "provider",
            "reasoner",
            promptTokens: 0,
            completionTokens: 1_000_000,
            cachedTokens: 0,
            reasoningTokens: 250_000,
            pricing,
            CancellationToken.None);

        BillableOperationRecord operation =
            Assert.IsType<BillableOperationRecord>(writer.LastOperation);
        Assert.Equal(20m, operation.ActualCostUsd);
        using JsonDocument snapshot = JsonDocument.Parse(operation.PricingSnapshotJson);
        Assert.Equal(
            JsonValueKind.Null,
            snapshot.RootElement.GetProperty("ReasoningPer1M").ValueKind);
    }

    [Fact]
    public async Task RecordUsageAsync_PersistsBeforeAddingCost()
    {
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new();
        PricingSettings pricing = new()
        {
            DefaultPricing = new ModelPricingEntry { InputPer1M = 1_000_000m },
        };
        TurnAccountingHandle handle = (await TurnAccountingHandle.BeginAsync(
            writer,
            reservations,
            pricing,
            "model",
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "request-order",
            cancellationToken: CancellationToken.None)).Value;
        writer.BeforeRecord = () => Assert.Equal(0m, handle.AccumulatedCostUsd);

        await handle.RecordChatUsageAsync(
            writer,
            "provider",
            "model",
            promptTokens: 1,
            completionTokens: 0,
            cachedTokens: 0,
            reasoningTokens: 0,
            pricing.DefaultPricing,
            CancellationToken.None);

        Assert.Equal(1m, handle.AccumulatedCostUsd);
    }

    [Fact]
    public async Task RecordUsageAsync_WhenDurableWriteFails_LeavesReservationUntouched()
    {
        RecordingTurnRunWriter writer = new() { RecordException = new IOException("disk full") };
        RecordingBudgetReservationService reservations = new();
        PricingSettings pricing = new()
        {
            DefaultPricing = new ModelPricingEntry { InputPer1M = 1_000_000m },
        };
        TurnAccountingHandle handle = (await TurnAccountingHandle.BeginAsync(
            writer,
            reservations,
            pricing,
            "model",
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "request-failure",
            cancellationToken: CancellationToken.None)).Value;

        IOException exception = await Assert.ThrowsAsync<IOException>(() =>
            handle.RecordChatUsageAsync(
                writer,
                "provider",
                "model",
                promptTokens: 1,
                completionTokens: 0,
                cachedTokens: 0,
                reasoningTokens: 0,
                pricing.DefaultPricing,
                CancellationToken.None));

        Assert.Equal("disk full", exception.Message);
        Assert.Equal(0m, handle.AccumulatedCostUsd);
        Assert.True(handle.AccountingFailed);

        await handle.CompleteAsync(
            writer,
            reservations,
            InferenceRunStatus.Completed,
            CancellationToken.None);

        Assert.Null(reservations.ReconciledUsd);
        Assert.False(reservations.WasReleased);
        Assert.Equal(InferenceRunStatus.Failed, writer.CompletedStatus);
    }

    [Fact]
    public async Task CompleteAsync_RetriesFailedReservationAndFreezesFirstDisposition()
    {
        RecordingTurnRunWriter writer = new();

        RecordingBudgetReservationService reservations = new()
        {
            ReconcileFailuresRemaining = 1,
        };

        TurnAccountingHandle handle = (await TurnAccountingHandle.BeginAsync(
            writer,
            reservations,
            new PricingSettings(),
            model: null,
            sessionId: null,
            surface: "test",
            purpose: "retry-reservation",
            requestId: "retry-reservation",
            cancellationToken: CancellationToken.None,
            reservedUsdOverride: 5m)).Value;

        handle.AddCost(2m);

        await Assert.ThrowsAsync<IOException>(() => handle.CompleteAsync(
            writer,
            reservations,
            InferenceRunStatus.Completed,
            CancellationToken.None));

        handle.AddCost(7m);

        await handle.CompleteAsync(
            writer,
            reservations,
            InferenceRunStatus.Failed,
            CancellationToken.None);

        Assert.Equal(2, reservations.ReconcileCalls);

        Assert.Equal(2m, reservations.ReconciledUsd);

        Assert.Equal(1, writer.CompleteCalls);

        Assert.Equal(InferenceRunStatus.Completed, writer.CompletedStatus);
    }

    [Fact]
    public async Task CompleteAsync_DoesNotRepeatReservationWhenRunCompletionRetries()
    {
        RecordingTurnRunWriter writer = new()
        {
            CompleteFailuresRemaining = 1,
        };

        RecordingBudgetReservationService reservations = new();

        TurnAccountingHandle handle = (await TurnAccountingHandle.BeginAsync(
            writer,
            reservations,
            new PricingSettings(),
            model: null,
            sessionId: null,
            surface: "test",
            purpose: "retry-run",
            requestId: "retry-run",
            cancellationToken: CancellationToken.None,
            reservedUsdOverride: 5m)).Value;

        handle.AddCost(3m);

        await Assert.ThrowsAsync<IOException>(() => handle.CompleteAsync(
            writer,
            reservations,
            InferenceRunStatus.Completed,
            CancellationToken.None));

        await handle.CompleteAsync(
            writer,
            reservations,
            InferenceRunStatus.Failed,
            CancellationToken.None);

        Assert.Equal(1, reservations.ReconcileCalls);

        Assert.Equal(3m, reservations.ReconciledUsd);

        Assert.Equal(2, writer.CompleteCalls);

        Assert.Equal(InferenceRunStatus.Completed, writer.CompletedStatus);
    }

    [Fact]
    public async Task CompleteAsync_SerializesConcurrentCallersAndKeepsFirstDisposition()
    {
        TaskCompletionSource reconciliationEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowReconciliation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new()
        {
            ReconciliationEntered = reconciliationEntered,
            AllowReconciliation = allowReconciliation,
        };

        TurnAccountingHandle handle = (await TurnAccountingHandle.BeginAsync(
            writer,
            reservations,
            new PricingSettings(),
            model: null,
            sessionId: null,
            surface: "test",
            purpose: "concurrent-completion",
            requestId: "concurrent-completion",
            cancellationToken: CancellationToken.None,
            reservedUsdOverride: 5m)).Value;

        handle.AddCost(2m);

        Task first = handle.CompleteAsync(
            writer,
            reservations,
            InferenceRunStatus.Completed,
            CancellationToken.None);

        await reconciliationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task second = handle.CompleteAsync(
            writer,
            reservations,
            InferenceRunStatus.Failed,
            CancellationToken.None);

        Assert.Equal(1, reservations.ReconcileCalls);
        Assert.False(second.IsCompleted);

        allowReconciliation.TrySetResult();

        await Task.WhenAll(first, second);

        Assert.Equal(1, reservations.ReconcileCalls);
        Assert.Equal(1, writer.CompleteCalls);
        Assert.Equal(InferenceRunStatus.Completed, writer.CompletedStatus);
    }

    [Fact]
    public async Task BeginBatchAsync_SumsResolvedPricingAndPerLineBudgets()
    {
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new();
        PricingSettings pricing = new()
        {
            DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
            ModelPricing =
            {
                ["reasoner"] = new ModelPricingEntry
                {
                    InputPer1M = 3m,
                    OutputPer1M = 4m,
                    ReasoningPer1M = 8m,
                },
            },
        };
        BatchReservationLine[] lines =
        [
            new("reasoner", MaxOutputTokens: 1_000, ReasoningBudgetTokens: 600),
            new("other", MaxOutputTokens: 200, ReasoningBudgetTokens: null),
        ];

        Result<TurnAccountingHandle> result = await TurnAccountingHandle.BeginBatchAsync(
            writer,
            reservations,
            pricing,
            lines,
            "batch-request",
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        decimal expected =
            BudgetReservationService.EstimateWorstCaseBatchLineUsd(
                pricing.ModelPricing["reasoner"],
                maxOutputTokens: 1_000,
                reasoningBudgetTokens: 600)
            + BudgetReservationService.EstimateWorstCaseBatchLineUsd(
                pricing.DefaultPricing,
                maxOutputTokens: 200,
                reasoningBudgetTokens: null);
        Assert.Equal(expected, reservations.LastRequest?.ReservedUsd);
    }

    [Fact]
    public async Task CreateNestedOperationHandle_SharesUnrestrictedBudgetAndCost()
    {
        RecordingTurnRunWriter writer = new();
        PricingSettings pricing = new();
        TurnAccountingHandle parent = (await TurnAccountingHandle.BeginAsync(
            writer,
            budgetReservations: null,
            pricing,
            model: null,
            sessionId: null,
            surface: "batch",
            purpose: "batch",
            requestId: "batch-budgets",
            cancellationToken: CancellationToken.None)).Value;
        TurnAccountingHandle first = parent.CreateNestedOperationHandle();
        TurnAccountingHandle second = parent.CreateNestedOperationHandle();

        Assert.Same(UnrestrictedTurnBudget.Instance, first.Budget);

        Assert.Same(first.Budget, second.Budget);

        await Task.WhenAll(
            Task.Run(() => first.AddCost(1m)),
            Task.Run(() => second.AddCost(2m)));

        Assert.Equal(3m, parent.AccumulatedCostUsd);
    }

    [Fact]
    public async Task AddCost_SaturatesAtDecimalMaximum()
    {
        TurnAccountingHandle handle = (await TurnAccountingHandle.BeginAsync(
            turnRunWriter: null,
            budgetReservations: null,
            new PricingSettings(),
            model: null,
            sessionId: null,
            surface: "test",
            purpose: "cost",
            requestId: "cost-saturation",
            cancellationToken: CancellationToken.None)).Value;

        handle.AddCost(decimal.MaxValue);
        handle.AddCost(1m);

        Assert.Equal(decimal.MaxValue, handle.AccumulatedCostUsd);
    }

    private static PricingSettings ReasonerPricing() =>
        new()
        {
            DefaultPricing = new ModelPricingEntry
            {
                InputPer1M = 10m,
                OutputPer1M = 20m,
                ReasoningPer1M = 80m,
            },
        };

    private static async Task<TurnAccountingHandle> BeginReasonerTurnAsync(
        RecordingTurnRunWriter writer,
        RecordingBudgetReservationService reservations,
        PricingSettings pricing) =>
        (await TurnAccountingHandle.BeginAsync(
            writer,
            reservations,
            pricing,
            "reasoner",
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "context-reservation",
            cancellationToken: CancellationToken.None,
            maxOutputTokens: 1_000,
            reasoningBudgetTokens: 600)).Value;

    private static ContextTokenBreakdown ReasonerContextBreakdown() =>
        new()
        {
            Provider = "provider",
            Model = "reasoner",
            Profile = new ResolvedModelTokenizationProfile
            {
                ProfileId = "test",
                Type = ModelTokenizationProfileType.UnknownFallback,
                TokenizerId = "o200k_base",
                SafetyMarginPercent = 15,
                PerMessageOverheadTokens = 4,
                PerToolOverheadTokens = 8,
                ProviderFramingTokens = 3,
                StopTokenOverheadTokens = 1,
                UnknownImageReserveTokens = 2048,
                Confidence = 0.5,
            },
            Components =
            [
                new ContextTokenComponent(
                    ContextTokenSource.ReservedAnswer,
                    new TokenEstimate(
                        1_000,
                        TokenEstimateClassification.Reserved,
                        "test")),
                new ContextTokenComponent(
                    ContextTokenSource.ReservedReasoning,
                    new TokenEstimate(
                        600,
                        TokenEstimateClassification.Reserved,
                        "test")),
            ],
            InputTokens = 5_000,
            ReservedTokens = 1_600,
            ReservedAnswerTokens = 1_000,
            ReservedReasoningTokens = 600,
            TotalTokens = 6_600,
            OverallClassification = TokenEstimateClassification.Estimated,
            SafetyMarginTokens = 500,
        };

    private sealed class RecordingTurnRunWriter : ITurnRunWriter
    {
        public Guid RunId { get; } = Guid.NewGuid();

        public BillableOperationRecord? LastOperation { get; private set; }

        public InferenceRunStatus? CompletedStatus { get; private set; }

        public int CompleteCalls { get; private set; }

        public Action? BeforeRecord { get; set; }

        public Exception? RecordException { get; init; }

        public int CompleteFailuresRemaining { get; set; }

        /// <summary>The summed actual cost of every operation recorded so far.</summary>
        public decimal RecordedCostUsd { get; private set; }

        public Task<Guid> StartRunAsync(
            InferenceRunStart start,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RunId);

        public Task CompleteRunAsync(
            Guid runId,
            InferenceRunStatus status,
            CancellationToken cancellationToken = default)
        {
            CompleteCalls++;

            if (CompleteFailuresRemaining > 0)
            {
                CompleteFailuresRemaining--;

                return Task.FromException(new IOException("run completion failed"));
            }

            CompletedStatus = status;
            return Task.CompletedTask;
        }

        public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Guid> RecordBillableOperationAsync(
            BillableOperationRecord operation,
            CancellationToken cancellationToken = default)
        {
            BeforeRecord?.Invoke();

            if (RecordException is not null)
            {
                return Task.FromException<Guid>(RecordException);
            }

            LastOperation = operation;
            RecordedCostUsd += operation.ActualCostUsd;
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class RecordingBudgetReservationService : IBudgetReservationService
    {
        private int _reconcileCalls;

        public BudgetReservationRequest? LastRequest { get; private set; }

        public decimal? ReconciledUsd { get; private set; }

        public decimal? AdjustedUsd { get; private set; }

        public int AdjustCount { get; private set; }

        public bool WasReleased { get; private set; }

        public int ReconcileCalls => Volatile.Read(ref _reconcileCalls);

        public Exception? ReserveException { get; init; }

        public int ReconcileFailuresRemaining { get; set; }

        public TaskCompletionSource? ReconciliationEntered { get; init; }

        public TaskCompletionSource? AllowReconciliation { get; init; }

        /// <summary>Today's committed spend, as the ledger behind the writer would report it.</summary>
        public Func<decimal>? CommittedSpend { get; init; }

        /// <summary>The single reservation's current amount: reserved, then raised by each adjust.</summary>
        public decimal OutstandingUsd { get; private set; }

        public List<DateTimeOffset> ExtendedExpiries { get; } = [];

        public Task<Result<BudgetReservation>> ReserveAsync(
            BudgetReservationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;

            if (ReserveException is not null)
            {
                return Task.FromException<Result<BudgetReservation>>(ReserveException);
            }

            OutstandingUsd = request.ReservedUsd;

            return Task.FromResult(Result<BudgetReservation>.Success(new BudgetReservation(
                Guid.NewGuid(),
                request.RunId,
                request.BudgetPeriod,
                request.ReservedUsd,
                0m,
                BudgetReservationStatus.Reserved,
                request.ExpiresAt,
                DateTimeOffset.UtcNow)));
        }

        public async Task ReconcileAsync(
            Guid reservationId,
            decimal actualCostUsd,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _reconcileCalls);

            ReconciliationEntered?.TrySetResult();

            if (AllowReconciliation is not null)
            {
                await AllowReconciliation.Task.WaitAsync(cancellationToken);
            }

            if (ReconcileFailuresRemaining > 0)
            {
                ReconcileFailuresRemaining--;

                throw new IOException("reservation reconciliation failed");
            }

            ReconciledUsd = actualCostUsd;
        }

        public Task<Result> AdjustAsync(
            Guid reservationId,
            decimal reservedUsd,
            CancellationToken cancellationToken = default)
        {
            AdjustedUsd = reservedUsd;
            AdjustCount++;
            OutstandingUsd = Math.Max(OutstandingUsd, reservedUsd);
            return Task.FromResult(Result.Success());
        }

        public Task ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default)
        {
            WasReleased = true;
            return Task.CompletedTask;
        }

        public Task<decimal> GetTodayCommittedSpendAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CommittedSpend?.Invoke() ?? 0m);

        public Task<decimal> GetTodayOutstandingReservationsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OutstandingUsd);

        public Task ExtendExpiryAsync(
            Guid reservationId,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
        {
            ExtendedExpiries.Add(expiresAt);

            return Task.CompletedTask;
        }

        public Task<int> SweepExpiredAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
