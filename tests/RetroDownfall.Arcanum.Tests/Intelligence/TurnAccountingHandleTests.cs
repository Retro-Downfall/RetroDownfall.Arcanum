using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class TurnAccountingHandleTests
{
    [Fact]
    public async Task BeginAsync_OrdinaryRun_DoesNotConsultCostReservation()
    {
        RecordingTurnRunWriter writer = new();

        TurnAccountingHandle beginning = await TurnAccountingHandle.BeginAsync(
            writer, null, "test", "ordinary", "request", CancellationToken.None);

        Assert.Equal(writer.RunId, beginning.RunId);

        await beginning.CompleteAsync(writer, InferenceRunStatus.Completed, CancellationToken.None);

        Assert.Equal(InferenceRunStatus.Completed, writer.CompletedStatus);
    }

    [Fact]
    public async Task Completion_CanceledGateDoesNotFreezeOrWrite()
    {
        RecordingTurnRunWriter writer = new();

        TurnAccountingHandle handle = await BeginReasonerTurnAsync(writer);

        using CancellationTokenSource caller = new();

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle.CompleteAsync(writer, InferenceRunStatus.Failed, caller.Token));

        Assert.False(handle.IsSettled);

        Assert.Equal(0, writer.CompleteCalls);

        await handle.CompleteAsync(writer, InferenceRunStatus.Completed, CancellationToken.None);

        Assert.Equal(InferenceRunStatus.Completed, writer.CompletedStatus);
    }

    [Fact]
    public async Task AmbientPush_RestoresNestedHandleAndWriterAfterException()
    {
        TurnAccountingAmbient.Clear();
        RecordingTurnRunWriter parentWriter = new();
        RecordingTurnRunWriter nestedWriter = new();
        TurnAccountingHandle parent = await TurnAccountingHandle.BeginAsync(
            turnRunWriter: null,
            sessionId: null,
            surface: "test",
            purpose: "ambient-parent",
            requestId: "ambient-parent",
            cancellationToken: CancellationToken.None);
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
        TurnAccountingHandle parent = await TurnAccountingHandle.BeginAsync(
            writer,
            sessionId: null,
            surface: "test",
            purpose: "suspend",
            requestId: "suspend",
            cancellationToken: CancellationToken.None);

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
    /// accounting. Nested work in that task accounts for itself when the prior run has settled.
    /// </summary>
    [Fact]
    public async Task Ambient_HidesTheHandleAndWriterOfARunThatHasSettled()
    {
        TurnAccountingAmbient.Clear();
        RecordingTurnRunWriter writer = new();
        TurnAccountingHandle turn = await TurnAccountingHandle.BeginAsync(
            writer,
            sessionId: null,
            surface: "test",
            purpose: "settled",
            requestId: "settled",
            cancellationToken: CancellationToken.None);
        TurnAccountingHandle nested = turn.CreateNestedOperationHandle();

        try
        {
            TurnAccountingAmbient.Publish(nested, writer);

            Assert.Same(nested, TurnAccountingAmbient.Current);

            await turn.CompleteAsync(
                writer,
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

    /// <summary>
    /// A tool that captured an accounting handle before settlement can still record measured usage.
    /// </summary>
    [Fact]
    public async Task Settlement_KeepsLateMeasuredUsage()
    {
        TurnAccountingAmbient.Clear();
        RecordingTurnRunWriter writer = new();
        PricingSettings pricing = ReasonerPricing();
        TurnAccountingHandle turn = await BeginReasonerTurnAsync(writer);
        TurnAccountingHandle nested = turn.CreateNestedOperationHandle();

        try
        {
            TurnAccountingAmbient.Publish(nested, writer);

            await turn.CompleteAsync(
                writer,
                InferenceRunStatus.Completed,
                CancellationToken.None);

            Assert.Null(TurnAccountingAmbient.Current);

            await nested.RecordChatUsageAsync(
                writer,
                "provider",
                "reasoner",
                promptTokens: 10_000,
                completionTokens: 0,
                cachedTokens: 0,
                reasoningTokens: 0,
                pricing.DefaultPricing,
                CancellationToken.None);

            Assert.NotNull(writer.LastOperation);
            Assert.Equal(0.10m, writer.RecordedCostUsd);
        }
        finally
        {
            TurnAccountingAmbient.Clear();
        }
    }

    [Fact]
    public async Task RecordChatUsageAsync_ReconcilesProviderReasoningUsageWithoutDoubleBilling()
    {
        RecordingTurnRunWriter writer = new();
        PricingSettings pricingSettings = new()
        {
            DefaultPricing = new ModelPricingEntry
            {
                OutputPer1M = 20m,
                ReasoningPer1M = 80m,
            },
        };
        TurnAccountingHandle handle = await TurnAccountingHandle.BeginAsync(
            writer,
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "request-2",
            cancellationToken: CancellationToken.None);
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
            InferenceRunStatus.Completed,
            CancellationToken.None);

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
        TurnAccountingHandle handle = await TurnAccountingHandle.BeginAsync(
            writer,
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "request-cached-input",
            cancellationToken: CancellationToken.None);

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
        TurnAccountingHandle handle = await TurnAccountingHandle.BeginAsync(
            writer,
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "request-null-reasoning-rate",
            cancellationToken: CancellationToken.None);

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
        PricingSettings pricing = new()
        {
            DefaultPricing = new ModelPricingEntry { InputPer1M = 1_000_000m },
        };
        TurnAccountingHandle handle = await TurnAccountingHandle.BeginAsync(
            writer,
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "request-order",
            cancellationToken: CancellationToken.None);
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
    public async Task RecordUsageAsync_WhenDurableWriteFails_MarksRunFailed()
    {
        RecordingTurnRunWriter writer = new() { RecordException = new IOException("disk full") };
        PricingSettings pricing = new()
        {
            DefaultPricing = new ModelPricingEntry { InputPer1M = 1_000_000m },
        };
        TurnAccountingHandle handle = await TurnAccountingHandle.BeginAsync(
            writer,
            sessionId: null,
            surface: "test",
            purpose: "chat",
            requestId: "request-failure",
            cancellationToken: CancellationToken.None);

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
            InferenceRunStatus.Completed,
            CancellationToken.None);

        Assert.Equal(InferenceRunStatus.Failed, writer.CompletedStatus);
    }

    [Fact]
    public async Task CompleteAsync_RetriesRunCompletionWithFirstDisposition()
    {
        RecordingTurnRunWriter writer = new()
        {
            CompleteFailuresRemaining = 1,
        };

        TurnAccountingHandle handle = await TurnAccountingHandle.BeginAsync(
            writer,
            sessionId: null,
            surface: "test",
            purpose: "retry-run",
            requestId: "retry-run",
            cancellationToken: CancellationToken.None);

        handle.AddCost(3m);

        await Assert.ThrowsAsync<IOException>(() => handle.CompleteAsync(
            writer,
            InferenceRunStatus.Completed,
            CancellationToken.None));

        await handle.CompleteAsync(
            writer,
            InferenceRunStatus.Failed,
            CancellationToken.None);

        Assert.Equal(2, writer.CompleteCalls);

        Assert.Equal(InferenceRunStatus.Completed, writer.CompletedStatus);
    }

    [Fact]
    public async Task CompleteAsync_SerializesConcurrentCallersAndKeepsFirstDisposition()
    {
        TaskCompletionSource completionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource allowCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        RecordingTurnRunWriter writer = new()
        {
            BeforeComplete = async () =>
            {
                completionEntered.SetResult();

                await allowCompletion.Task;
            },
        };

        TurnAccountingHandle handle = await BeginReasonerTurnAsync(writer);

        Task first = handle.CompleteAsync(writer, InferenceRunStatus.Completed, CancellationToken.None);

        await completionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task second = handle.CompleteAsync(writer, InferenceRunStatus.Failed, CancellationToken.None);

        try
        {
            Assert.False(second.IsCompleted);
        }
        finally
        {
            allowCompletion.TrySetResult();
        }

        await Task.WhenAll(first, second);

        Assert.Equal(1, writer.CompleteCalls);

        Assert.Equal(InferenceRunStatus.Completed, writer.CompletedStatus);
    }

    [Fact]
    public async Task CreateNestedOperationHandle_SharesUnrestrictedBudgetAndCost()
    {
        RecordingTurnRunWriter writer = new();
        TurnAccountingHandle parent = await TurnAccountingHandle.BeginAsync(
            writer,
            sessionId: null,
            surface: "batch",
            purpose: "batch",
            requestId: "batch-budgets",
            cancellationToken: CancellationToken.None);
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
        TurnAccountingHandle handle = await TurnAccountingHandle.BeginAsync(
            turnRunWriter: null,
            sessionId: null,
            surface: "test",
            purpose: "cost",
            requestId: "cost-saturation",
            cancellationToken: CancellationToken.None);

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

    private static Task<TurnAccountingHandle> BeginReasonerTurnAsync(RecordingTurnRunWriter writer) =>
        TurnAccountingHandle.BeginAsync(writer, null, "test", "chat", "reasoner", CancellationToken.None);

    private sealed class RecordingTurnRunWriter : ITurnRunWriter
    {
        public Guid RunId { get; } = Guid.NewGuid();

        public BillableOperationRecord? LastOperation { get; private set; }

        public InferenceRunStatus? CompletedStatus { get; private set; }

        public int CompleteCalls { get; private set; }

        public Action? BeforeRecord { get; set; }

        public Exception? RecordException { get; init; }

        public int CompleteFailuresRemaining { get; set; }
        public Exception? CompleteException { get; init; }
        public CancellationToken CompletionToken { get; private set; }

        /// <summary>The summed actual cost of every operation recorded so far.</summary>
        public decimal RecordedCostUsd { get; private set; }

        public Task<Guid> StartRunAsync(
            InferenceRunStart start,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RunId);

        public Func<Task>? BeforeComplete { get; init; }

        public async Task CompleteRunAsync(
            Guid runId,
            InferenceRunStatus status,
            CancellationToken cancellationToken = default)
        {
            if (BeforeComplete is not null) await BeforeComplete();

            CompleteCalls++;
            CompletionToken = cancellationToken;

            if (CompleteFailuresRemaining > 0)
            {
                CompleteFailuresRemaining--;

                throw CompleteException ?? new IOException("run completion failed");
            }

            CompletedStatus = status;
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
}
