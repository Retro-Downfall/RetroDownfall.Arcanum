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
            "reasoner",
            breakdown,
            CancellationToken.None);
        Result repeated = await handle.EnsureReservationForContextAsync(
            reservations,
            pricing,
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

    private sealed class RecordingTurnRunWriter : ITurnRunWriter
    {
        public Guid RunId { get; } = Guid.NewGuid();

        public BillableOperationRecord? LastOperation { get; private set; }

        public InferenceRunStatus? CompletedStatus { get; private set; }

        public Action? BeforeRecord { get; set; }

        public Exception? RecordException { get; init; }

        public Task<Guid> StartRunAsync(
            InferenceRunStart start,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RunId);

        public Task CompleteRunAsync(
            Guid runId,
            InferenceRunStatus status,
            CancellationToken cancellationToken = default)
        {
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
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class RecordingBudgetReservationService : IBudgetReservationService
    {
        public BudgetReservationRequest? LastRequest { get; private set; }

        public decimal? ReconciledUsd { get; private set; }

        public decimal? AdjustedUsd { get; private set; }

        public int AdjustCount { get; private set; }

        public bool WasReleased { get; private set; }

        public Task<Result<BudgetReservation>> ReserveAsync(
            BudgetReservationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
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

        public Task ReconcileAsync(
            Guid reservationId,
            decimal actualCostUsd,
            CancellationToken cancellationToken = default)
        {
            ReconciledUsd = actualCostUsd;
            return Task.CompletedTask;
        }

        public Task<Result> AdjustAsync(
            Guid reservationId,
            decimal reservedUsd,
            CancellationToken cancellationToken = default)
        {
            AdjustedUsd = reservedUsd;
            AdjustCount++;
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
            Task.FromResult(0m);

        public Task<decimal> GetTodayOutstandingReservationsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<int> SweepExpiredAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
