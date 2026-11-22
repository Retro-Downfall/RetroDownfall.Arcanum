using System.Globalization;
using System.Threading;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Per-turn or per-batch run and usage accounting. Actual billed usage is persisted as it arrives.
/// When <see cref="OwnsLifecycle"/> is false, the handle is ambient (batch parent / nested work) —
/// callers must not <see cref="CompleteAsync"/>.
/// </summary>
internal sealed class TurnAccountingHandle
{
    private readonly object _costGate = new();

    private readonly SemaphoreSlim _writerGate = new(1, 1);

    private readonly SemaphoreSlim _completionGate = new(1, 1);

    private readonly TurnAccountingHandle? _accountingOwner;

    private TurnAccountingHandle(
        ITurnBudget budget,
        Guid? runId,
        bool ownsLifecycle,
        TurnAccountingHandle? accountingOwner = null)
    {
        Budget = budget;
        RunId = runId;
        OwnsLifecycle = ownsLifecycle;
        _accountingOwner = accountingOwner;
    }

    public ITurnBudget Budget { get; }

    public Guid? RunId { get; }

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
    /// True once the terminal status is frozen for the run. Ambient reuse ends here,
    /// even if persisting that status needs a retry.
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

    private bool _possiblyDispatched;

    private bool _uncertainProviderEffect;

    private bool _hasRecordedOperations;

    private bool _completionSnapshotFrozen;

    private InferenceRunStatus _completionStatus;

    private bool _runDispositionCompleted;

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
            ownsLifecycle: false,
            accountingOwner: AccountingRoot);

    /// <summary>Records a confirmed attempted send whose usage is unknown.</summary>
    internal void RecordUncertainProviderEffect()
    {
        TurnAccountingHandle root = AccountingRoot;

        lock (root._costGate)
        {
            if (root._completionSnapshotFrozen)
            {
                throw new InvalidOperationException("A provider effect cannot be reported after accounting completion has frozen.");
            }

            root._uncertainProviderEffect = true;
        }
    }

    /// <summary>Marks a possible dispatch before usage is known.</summary>
    public void MarkPossiblyDispatched()
    {
        if (!OwnsLifecycle || RunId is null)
        {
            throw new InvalidOperationException("An owning accounted run is required.");
        }

        lock (_costGate)
        {
            _possiblyDispatched = true;
        }
    }

    public async Task CompleteAsync(
        ITurnRunWriter? turnRunWriter,
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
            lock (_costGate)
            {
                if (!_completionSnapshotFrozen)
                {
                    _completionStatus = _accountingFailed || _uncertainProviderEffect || (_possiblyDispatched && !_hasRecordedOperations)
                        ? InferenceRunStatus.Failed
                        : status;
                    _completionSnapshotFrozen = true;
                }
            }

            if (!_runDispositionCompleted && RunId is Guid runId && turnRunWriter is not null)
            {
                await turnRunWriter.CompleteRunAsync(runId, _completionStatus, cancellationToken).ConfigureAwait(false);

                _runDispositionCompleted = true;
            }
        }
        finally
        {
            _completionGate.Release();
        }
    }

    public static async Task<TurnAccountingHandle> BeginAsync(
        ITurnRunWriter? turnRunWriter,
        Guid? sessionId,
        string surface,
        string purpose,
        string requestId,
        CancellationToken cancellationToken)
    {
        Guid? runId = turnRunWriter is null
            ? null
            : await turnRunWriter.StartRunAsync(
                    new InferenceRunStart(
                        RequestId: requestId,
                        SessionId: sessionId,
                        Surface: surface,
                        Purpose: purpose,
                        IdempotencyClaimId: null,
                        StartedAt: DateTimeOffset.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);

        return new TurnAccountingHandle(UnrestrictedTurnBudget.Instance, runId, ownsLifecycle: true);
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
