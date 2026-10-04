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
/// Per-turn (or per-batch) run + reservation + cost-accounting handle. Failures are logged by the
/// caller; reservation acquisition failures surface as <see cref="ErrorCodes.Budget.Exceeded"/>.
/// When <see cref="OwnsLifecycle"/> is false, the handle is ambient (batch parent / nested work) —
/// callers must not <see cref="CompleteAsync"/>.
/// </summary>
internal sealed class TurnAccountingHandle
{
    private readonly object _costGate = new();

    private readonly SemaphoreSlim _writerGate = new(1, 1);

    private readonly SemaphoreSlim _reservationGate = new(1, 1);

    private readonly SemaphoreSlim _completionGate = new(1, 1);

    private readonly TurnAccountingHandle? _accountingOwner;

    /// <summary>
    /// How long a turn's reservation stays outstanding without renewal. Admission sets it and every
    /// pre-call <see cref="EnsureReservationForContextAsync"/> renews it from that moment.
    /// </summary>
    internal static readonly TimeSpan ReservationLifetime = TimeSpan.FromHours(1);

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

    /// <summary>
    /// True once the run this handle ledgers against has frozen its completion: its status and its
    /// reservation disposition are decided, and nothing recorded from here on is part of either.
    /// </summary>
    public bool IsSettled
    {
        get
        {
            TurnAccountingHandle root = AccountingRoot;

            lock (root._costGate)
            {
                return root._completionSnapshotFrozen;
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

    private bool _completionSnapshotFrozen;

    private InferenceRunStatus _completionStatus;

    private decimal _completionCostUsd;

    private bool _completionHasRecordedOperations;

    private bool _completionAccountingFailed;

    private bool _reservationDispositionCompleted;

    private bool _runDispositionCompleted;

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
            UnrestrictedTurnBudget.Instance,
            RunId,
            ReservationId,
            ReservationActive,
            ownsLifecycle: false,
            reservationHighWaterUsd: 0m,
            accountingOwner: AccountingRoot);

    public async Task<Result> EnsureReservationForContextAsync(
        IBudgetReservationService? budgetReservations,
        PricingSettings pricing,
        BudgetSettings? budget,
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
            bool raise;

            lock (root._costGate)
            {
                raise = reservedUsd > root._reservationHighWaterUsd;
            }

            Result admitted;

            if (raise)
            {
                // A raise rechecks committed + outstanding spend atomically inside the service.
                admitted = await budgetReservations
                    .AdjustAsync(reservationId, reservedUsd, cancellationToken)
                    .ConfigureAwait(false);
                if (admitted.IsSuccess)
                {
                    lock (root._costGate)
                    {
                        root._reservationHighWaterUsd = Math.Max(
                            root._reservationHighWaterUsd,
                            reservedUsd);
                    }
                }
            }
            else
            {
                // The estimate plateaued, so nothing is raised — but the spend earlier rounds
                // committed still counts, and without this check N rounds could each spend what one
                // admission was sized for.
                admitted = await CheckAccumulatedSpendAsync(budgetReservations, budget, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (admitted.IsFailure)
            {
                return admitted;
            }

            await budgetReservations
                .ExtendExpiryAsync(
                    reservationId,
                    DateTimeOffset.UtcNow.Add(ReservationLifetime),
                    cancellationToken)
                .ConfigureAwait(false);

            return Result.Success();
        }
        finally
        {
            root._reservationGate.Release();
        }
    }

    /// <summary>
    /// The plateau-round budget check: today's committed spend plus outstanding reservations, this
    /// turn's own included, against the daily limit.
    /// </summary>
    /// <remarks>
    /// A cheap read on the same non-immediate path <see cref="BudgetMonitor"/> takes, rather than a
    /// write transaction per round. Committed spend already holds every earlier round of this turn and
    /// the outstanding reservation is sized for the next call alone, so this checks accumulated actual
    /// spend and never multiplies an estimate by a call count.
    /// </remarks>
    private static async Task<Result> CheckAccumulatedSpendAsync(
        IBudgetReservationService budgetReservations,
        BudgetSettings? budget,
        CancellationToken cancellationToken)
    {
        if (budget is not { Enabled: true } || budget.DailyLimitUsd <= 0m)
        {
            return Result.Success();
        }

        decimal dailyLimit = ArcanumSettingClamps.BudgetDailyLimitUsd(budget.DailyLimitUsd);

        decimal committed = await budgetReservations
            .GetTodayCommittedSpendAsync(cancellationToken)
            .ConfigureAwait(false);

        decimal outstanding = await budgetReservations
            .GetTodayOutstandingReservationsAsync(cancellationToken)
            .ConfigureAwait(false);

        decimal spend = SaturatingCostAdd(committed, outstanding);

        return spend > dailyLimit
            ? Result.Failure(new Error(
                ErrorCodes.Budget.Exceeded,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Daily budget limit of ${dailyLimit:0.00} USD would be exceeded (committed+reserved: ${spend:0.00} USD).")))
            : Result.Success();
    }

    public async Task CompleteAsync(
        ITurnRunWriter? turnRunWriter,
        IBudgetReservationService? budgetReservations,
        InferenceRunStatus status,
        CancellationToken cancellationToken)
    {
        if (!OwnsLifecycle)
        {
            return;
        }

        await _completionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_completionSnapshotFrozen)
            {
                TurnAccountingHandle root = AccountingRoot;

                lock (root._costGate)
                {
                    _completionCostUsd = root._accumulatedCostUsd;
                    _completionHasRecordedOperations = root._hasRecordedOperations;
                    _completionAccountingFailed = root._accountingFailed;
                    _completionStatus = _completionAccountingFailed
                        ? InferenceRunStatus.Failed
                        : status;
                    _completionSnapshotFrozen = true;
                }
            }

            if (!_reservationDispositionCompleted)
            {
                if (!_completionAccountingFailed
                    && ReservationActive
                    && ReservationId is Guid reservationId
                    && budgetReservations is not null)
                {
                    if (_completionStatus == InferenceRunStatus.Completed
                        || _completionCostUsd > 0m
                        || _completionHasRecordedOperations)
                    {
                        await budgetReservations
                            .ReconcileAsync(reservationId, _completionCostUsd, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await budgetReservations
                            .ReleaseAsync(reservationId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                _reservationDispositionCompleted = true;
            }

            if (!_runDispositionCompleted)
            {
                if (RunId is Guid runId && turnRunWriter is not null)
                {
                    await turnRunWriter
                        .CompleteRunAsync(runId, _completionStatus, cancellationToken)
                        .ConfigureAwait(false);
                }

                _runDispositionCompleted = true;
            }
        }
        finally
        {
            _completionGate.Release();
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
        ITurnBudget budget = UnrestrictedTurnBudget.Instance;

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

        Result<BudgetReservation> reserved;

        // The run row is already open. Only BudgetExceededException comes back as a Result failure;
        // cancellation and every other store fault propagate, and an unclosed row stays Running
        // forever — there is no expiry sweep for runs the way there is for reservations, and
        // retention pruning treats a Running run as an active session.
        try
        {
            ModelPricingEntry entry = pricing.ResolveForModel(model);

            decimal reservedUsd = reservedUsdOverride
                ?? BudgetReservationService.EstimateWorstCaseTurnUsd(
                    entry,
                    maxOutputTokens,
                    reasoningBudgetTokens);

            string period = BudgetReservationService.UtcBudgetPeriod(DateTimeOffset.UtcNow);

            reserved = await budgetReservations.ReserveAsync(
                    new BudgetReservationRequest(
                        runId,
                        reservedUsd,
                        ExpiresAt: DateTimeOffset.UtcNow.Add(ReservationLifetime),
                        period),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await turnRunWriter.CompleteRunAsync(runId, InferenceRunStatus.Failed, CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }

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
