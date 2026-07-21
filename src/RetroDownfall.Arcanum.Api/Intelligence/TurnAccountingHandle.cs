using System.Globalization;
using System.Threading;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Per-turn (or per-batch) run + reservation + model-call budget handle. Failures are logged by the
/// caller; reservation acquisition failures surface as <see cref="ErrorCodes.Budget.Exceeded"/>.
/// When <see cref="OwnsLifecycle"/> is false, the handle is ambient (batch parent / nested work) —
/// callers must not <see cref="CompleteAsync"/>.
/// </summary>
internal sealed class TurnAccountingHandle
{

    private readonly object _costGate = new();

    private TurnAccountingHandle(
        ITurnBudget budget,
        Guid? runId,
        Guid? reservationId,
        bool reservationActive,
        bool ownsLifecycle)
    {
        Budget = budget;
        RunId = runId;
        ReservationId = reservationId;
        ReservationActive = reservationActive;
        OwnsLifecycle = ownsLifecycle;
    }

    public ITurnBudget Budget { get; }

    public Guid? RunId { get; }

    public Guid? ReservationId { get; }

    public bool ReservationActive { get; }

    /// <summary>When false, this handle is shared ambient accounting — do not complete it.</summary>
    public bool OwnsLifecycle { get; }

    public decimal AccumulatedCostUsd { get; private set; }

    private bool _finished;

    public void AddCost(decimal costUsd)
    {
        if (costUsd <= 0m)
        {
            return;
        }

        lock (_costGate)
        {
            AccumulatedCostUsd += costUsd;
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

        lock (_costGate)
        {
            accumulated = AccumulatedCostUsd;
        }

        if (ReservationActive && ReservationId is Guid reservationId && budgetReservations is not null)
        {
            if (status == InferenceRunStatus.Completed || accumulated > 0m)
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

        ModelPricingEntry entry = ResolvePricing(pricing, model);

        decimal reservedUsd = reservedUsdOverride
            ?? BudgetReservationService.EstimateWorstCaseTurnUsd(entry);

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
            new TurnAccountingHandle(budget, runId, active ? reserved.Value.Id : null, active, ownsLifecycle: true));
    }

    /// <summary>
    /// One reservation for an entire OpenAI batch (lineCount × single-call worst case; batches force
    /// zero tools so MaxModelCalls does not apply per line).
    /// </summary>
    public static Task<Result<TurnAccountingHandle>> BeginBatchAsync(
        ITurnRunWriter? turnRunWriter,
        IBudgetReservationService? budgetReservations,
        PricingSettings pricing,
        int lineCount,
        string requestId,
        CancellationToken cancellationToken)
    {
        int clampedLines = Math.Max(0, lineCount);
        ModelPricingEntry entry = ResolvePricing(pricing, model: null);
        decimal reservedUsd = BudgetReservationService.EstimateWorstCaseBatchLineUsd(entry) * clampedLines;

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
        ModelPricingEntry pricing,
        CancellationToken cancellationToken)
    {
        if (turnRunWriter is null || RunId is not Guid runId)
        {
            return;
        }

        decimal cost = CostCalculator.CalculateCost(inputTokens, outputTokens, cachedTokens, pricing);
        AddCost(cost);

        string snapshot =
            "{\"InputPer1M\":"
            + pricing.InputPer1M.ToString(CultureInfo.InvariantCulture)
            + ",\"OutputPer1M\":"
            + pricing.OutputPer1M.ToString(CultureInfo.InvariantCulture)
            + ",\"CachedPer1M\":"
            + pricing.CachedPer1M.ToString(CultureInfo.InvariantCulture)
            + "}";

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
                    CachedTokens: cachedTokens,
                    PricingSnapshotJson: snapshot,
                    ActualCostUsd: cost,
                    Status: BillableOperationStatus.Completed,
                    ProviderRequestId: null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ModelPricingEntry ResolvePricing(PricingSettings pricing, string? model)
    {
        ModelPricingEntry entry = pricing.DefaultPricing;

        if (model is not null && pricing.ModelPricing.TryGetValue(model, out ModelPricingEntry? explicitPricing))
        {
            entry = explicitPricing;
        }

        return entry;
    }

}
