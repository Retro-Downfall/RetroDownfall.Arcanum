using System.Globalization;
using System.Threading;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal sealed record BatchReservationLine(
    string? Model,
    int? MaxOutputTokens,
    int? ReasoningBudgetTokens);

/// <summary>
/// Per-turn (or per-batch) run + reservation + model-call budget handle. Failures are logged by the
/// caller; reservation acquisition failures surface as <see cref="ErrorCodes.Budget.Exceeded"/>.
/// When <see cref="OwnsLifecycle"/> is false, the handle is ambient (batch parent / nested work) —
/// callers must not <see cref="CompleteAsync"/>.
/// </summary>
internal sealed class TurnAccountingHandle
{

    private readonly object _costGate = new();

    private readonly SemaphoreSlim _writerGate = new(1, 1);

    private readonly SemaphoreSlim _reservationGate = new(1, 1);

    private readonly TurnAccountingHandle? _accountingOwner;

    private TurnAccountingHandle(
        ITurnBudget budget,
        Guid? runId,
        Guid? reservationId,
        bool reservationActive,
        bool ownsLifecycle,
        decimal reservationHighWaterUsd = 0m,
        TurnAccountingHandle? accountingOwner = null)
    {
        Budget = budget;
        RunId = runId;
        ReservationId = reservationId;
        ReservationActive = reservationActive;
        OwnsLifecycle = ownsLifecycle;
        _reservationHighWaterUsd = Math.Max(0m, reservationHighWaterUsd);
        _accountingOwner = accountingOwner;
    }

    public ITurnBudget Budget { get; }

    public Guid? RunId { get; }

    public Guid? ReservationId { get; }

    public bool ReservationActive { get; }

    /// <summary>When false, this handle is shared ambient accounting — do not complete it.</summary>
    public bool OwnsLifecycle { get; }

    public decimal AccumulatedCostUsd
    {
        get
        {
            TurnAccountingHandle root = AccountingRoot;

            lock (root._costGate)
            {
                return root._accumulatedCostUsd;
            }
        }
    }

    public bool AccountingFailed
    {
        get
        {
            TurnAccountingHandle root = AccountingRoot;

            lock (root._costGate)
            {
                return root._accountingFailed;
            }
        }
    }

    private decimal _accumulatedCostUsd;

    private bool _accountingFailed;

    private bool _hasRecordedOperations;

    private bool _finished;

    private decimal _reservationHighWaterUsd;

    private TurnAccountingHandle AccountingRoot => _accountingOwner ?? this;

    public void AddCost(decimal costUsd)
    {
        if (costUsd <= 0m)
        {
            return;
        }

        TurnAccountingHandle root = AccountingRoot;

        lock (root._costGate)
        {
            root._accumulatedCostUsd = SaturatingCostAdd(root._accumulatedCostUsd, costUsd);
        }
    }

    public TurnAccountingHandle CreateNestedOperationHandle() =>
        new(
            new TurnBudget(),
            RunId,
            ReservationId,
            ReservationActive,
            ownsLifecycle: false,
            reservationHighWaterUsd: 0m,
            accountingOwner: AccountingRoot);

    public async Task<Result> EnsureReservationForContextAsync(
        IBudgetReservationService? budgetReservations,
        PricingSettings pricing,
        string? model,
        ContextTokenBreakdown breakdown,
        CancellationToken cancellationToken)
    {
        if (!OwnsLifecycle
            || !ReservationActive
            || ReservationId is not Guid reservationId
            || budgetReservations is null)
        {
            return Result.Success();
        }

        ModelPricingEntry entry = pricing.ResolveForModel(model);
        decimal reservedUsd = BudgetReservationService.EstimateWorstCaseTurnUsd(
            entry,
            maxOutputTokens: breakdown.ReservedAnswerTokens,
            reasoningBudgetTokens: breakdown.ReservedReasoningTokens,
            estimatedInputTokens: breakdown.InputTokens);
        TurnAccountingHandle root = AccountingRoot;
        await root._reservationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (root._costGate)
            {
                if (reservedUsd <= root._reservationHighWaterUsd)
                {
                    return Result.Success();
                }
            }

            Result adjusted = await budgetReservations
                .AdjustAsync(reservationId, reservedUsd, cancellationToken)
                .ConfigureAwait(false);
            if (adjusted.IsSuccess)
            {
                lock (root._costGate)
                {
                    root._reservationHighWaterUsd = Math.Max(
                        root._reservationHighWaterUsd,
                        reservedUsd);
                }
            }

            return adjusted;
        }
        finally
        {
            root._reservationGate.Release();
        }
    }

    public async Task CompleteAsync(
        ITurnRunWriter? turnRunWriter,
        IBudgetReservationService? budgetReservations,
        InferenceRunStatus status,
        CancellationToken cancellationToken)
    {
        if (!OwnsLifecycle || _finished)
        {
            return;
        }

        _finished = true;

        decimal accumulated;
        bool hasRecordedOperations;

        TurnAccountingHandle root = AccountingRoot;

        lock (root._costGate)
        {
            accumulated = root._accumulatedCostUsd;
            hasRecordedOperations = root._hasRecordedOperations;

            if (root._accountingFailed)
            {
                status = InferenceRunStatus.Failed;
            }
        }

        if (!AccountingFailed
            && ReservationActive
            && ReservationId is Guid reservationId
            && budgetReservations is not null)
        {
            if (status == InferenceRunStatus.Completed || accumulated > 0m || hasRecordedOperations)
            {
                await budgetReservations.ReconcileAsync(reservationId, accumulated, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await budgetReservations.ReleaseAsync(reservationId, cancellationToken).ConfigureAwait(false);
            }
        }

        if (RunId is Guid runId && turnRunWriter is not null)
        {
            await turnRunWriter.CompleteRunAsync(runId, status, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<Result<TurnAccountingHandle>> BeginAsync(
        ITurnRunWriter? turnRunWriter,
        IBudgetReservationService? budgetReservations,
        PricingSettings pricing,
        string? model,
        Guid? sessionId,
        string surface,
        string purpose,
        string requestId,
        CancellationToken cancellationToken,
        int? maxOutputTokens = null,
        int? reasoningBudgetTokens = null,
        decimal? reservedUsdOverride = null)
    {
        TurnBudget budget = new();

        if (turnRunWriter is null)
        {
            return Result<TurnAccountingHandle>.Success(
                new TurnAccountingHandle(budget, runId: null, reservationId: null, reservationActive: false, ownsLifecycle: true));
        }

        Guid runId = await turnRunWriter.StartRunAsync(
                new InferenceRunStart(
                    RequestId: requestId,
                    SessionId: sessionId,
                    Surface: surface,
                    Purpose: purpose,
                    IdempotencyClaimId: null,
                    StartedAt: DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);

        if (budgetReservations is null)
        {
            return Result<TurnAccountingHandle>.Success(
                new TurnAccountingHandle(budget, runId, reservationId: null, reservationActive: false, ownsLifecycle: true));
        }

        ModelPricingEntry entry = pricing.ResolveForModel(model);

        decimal reservedUsd = reservedUsdOverride
            ?? BudgetReservationService.EstimateWorstCaseTurnUsd(
                entry,
                maxOutputTokens,
                reasoningBudgetTokens);

        string period = BudgetReservationService.UtcBudgetPeriod(DateTimeOffset.UtcNow);

        Result<BudgetReservation> reserved = await budgetReservations.ReserveAsync(
                new BudgetReservationRequest(
                    runId,
                    reservedUsd,
                    ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                    period),
                cancellationToken)
            .ConfigureAwait(false);

        if (reserved.IsFailure)
        {
            await turnRunWriter.CompleteRunAsync(runId, InferenceRunStatus.Failed, CancellationToken.None)
                .ConfigureAwait(false);

            return Result<TurnAccountingHandle>.Failure(reserved.Error);
        }

        bool active = reserved.Value.Status == BudgetReservationStatus.Reserved;

        return Result<TurnAccountingHandle>.Success(
            new TurnAccountingHandle(
                budget,
                runId,
                active ? reserved.Value.Id : null,
                active,
                ownsLifecycle: true,
                reservationHighWaterUsd: active ? reserved.Value.ReservedUsd : 0m));
    }

    /// <summary>
    /// One reservation for an entire OpenAI batch, summed from each valid line's resolved pricing and
    /// typed output/reasoning budget. Batches force zero tools, so each line reserves one model call.
    /// </summary>
    public static Task<Result<TurnAccountingHandle>> BeginBatchAsync(
        ITurnRunWriter? turnRunWriter,
        IBudgetReservationService? budgetReservations,
        PricingSettings pricing,
        IReadOnlyList<BatchReservationLine> lines,
        string requestId,
        CancellationToken cancellationToken)
    {
        decimal reservedUsd = 0m;

        foreach (BatchReservationLine line in lines)
        {
            ModelPricingEntry entry = pricing.ResolveForModel(line.Model);
            reservedUsd += BudgetReservationService.EstimateWorstCaseBatchLineUsd(
                entry,
                line.MaxOutputTokens,
                line.ReasoningBudgetTokens);
        }

        return BeginAsync(
            turnRunWriter,
            budgetReservations,
            pricing,
            model: null,
            sessionId: null,
            surface: "batch",
            purpose: "batch",
            requestId,
            cancellationToken,
            reservedUsdOverride: reservedUsd);
    }

    public Task RecordChatUsageAsync(
        ITurnRunWriter? turnRunWriter,
        string provider,
        string model,
        long promptTokens,
        long completionTokens,
        long cachedTokens,
        long reasoningTokens,
        ModelPricingEntry pricing,
        CancellationToken cancellationToken) =>
        RecordUsageAsync(
            turnRunWriter,
            BillableOperationType.Chat,
            provider,
            model,
            purpose: "chat",
            promptTokens,
            completionTokens,
            cachedTokens,
            reasoningTokens,
            pricing,
            cancellationToken);

    public async Task RecordUsageAsync(
        ITurnRunWriter? turnRunWriter,
        BillableOperationType operationType,
        string provider,
        string model,
        string purpose,
        long inputTokens,
        long outputTokens,
        long cachedTokens,
        long reasoningTokens,
        ModelPricingEntry pricing,
        CancellationToken cancellationToken)
    {
        if (turnRunWriter is null || RunId is not Guid runId)
        {
            return;
        }

        decimal cost = CostCalculator.CalculateCost(
            inputTokens,
            outputTokens,
            cachedTokens,
            reasoningTokens,
            pricing);
        string snapshot =
            "{\"InputPer1M\":"
            + pricing.InputPer1M.ToString(CultureInfo.InvariantCulture)
            + ",\"OutputPer1M\":"
            + pricing.OutputPer1M.ToString(CultureInfo.InvariantCulture)
            + ",\"ReasoningPer1M\":"
            + (pricing.ReasoningPer1M?.ToString(CultureInfo.InvariantCulture) ?? "null")
            + ",\"CachedPer1M\":"
            + pricing.CachedPer1M.ToString(CultureInfo.InvariantCulture)
            + "}";

        TurnAccountingHandle accountingRoot = AccountingRoot;

        await accountingRoot._writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _ = await turnRunWriter.RecordBillableOperationAsync(
                    new BillableOperationRecord(
                        runId,
                        operationType,
                        provider,
                        model,
                        Purpose: purpose,
                        StartedAt: DateTimeOffset.UtcNow,
                        CompletedAt: DateTimeOffset.UtcNow,
                        InputTokens: inputTokens,
                        OutputTokens: outputTokens,
                        ReasoningTokens: reasoningTokens,
                        CachedTokens: cachedTokens,
                        PricingSnapshotJson: snapshot,
                        ActualCostUsd: cost,
                        Status: BillableOperationStatus.Completed,
                        ProviderRequestId: null),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (accountingRoot._costGate)
            {
                accountingRoot._accountingFailed = true;
            }

            throw;
        }
        finally
        {
            accountingRoot._writerGate.Release();
        }

        lock (accountingRoot._costGate)
        {
            accountingRoot._hasRecordedOperations = true;

            if (cost > 0m)
            {
                accountingRoot._accumulatedCostUsd =
                    SaturatingCostAdd(accountingRoot._accumulatedCostUsd, cost);
            }
        }
    }

    private static decimal SaturatingCostAdd(decimal left, decimal right) =>
        right >= decimal.MaxValue - left ? decimal.MaxValue : left + right;

}
