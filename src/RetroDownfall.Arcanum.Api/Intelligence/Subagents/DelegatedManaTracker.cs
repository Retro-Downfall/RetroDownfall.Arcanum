using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Api.Intelligence.Subagents;

public enum DelegatedBudgetExhaustionReason
{
    Tokens = 1,
    Cost = 2,
    Turns = 3,
}

public sealed record DelegatedManaUsage(
    long Tokens,
    decimal CostUsd,
    int ModelCalls,
    bool Exhausted);

public sealed class BudgetExhaustedException : Exception
{
    public BudgetExhaustedException(
        DelegatedBudgetExhaustionReason reason,
        DelegatedManaUsage usage)
        : base($"Delegated subagent budget exhausted ({reason}).")
    {
        Reason = reason;
        Usage = usage;
    }

    public DelegatedBudgetExhaustionReason Reason { get; }

    public DelegatedManaUsage Usage { get; }
}

/// <summary>
/// Thread-safe hierarchical accounting ledger for a single isolated subagent run.
/// Provider-authoritative usage is charged after every model call.
/// </summary>
public sealed class DelegatedManaTracker
{
    private readonly long? _maxTokens;

    private readonly decimal? _maxCostUsd;

    private readonly int _maxTurns;

    private readonly object _costGate = new();

    private long _tokens;

    private decimal _costUsd;

    private int _modelCalls;

    private int _exhaustionReason;

    public DelegatedManaTracker(
        long? maxTokens,
        decimal? maxCostUsd,
        int maxTurns)
    {
        if (maxTokens is null && maxCostUsd is null)
        {
            throw new ArgumentException(
                "A delegated token or cost ceiling is required.");
        }

        if (maxTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens));
        }

        if (maxCostUsd is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCostUsd));
        }

        if (maxTurns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTurns));
        }

        _maxTokens = maxTokens;
        _maxCostUsd = maxCostUsd;
        _maxTurns = maxTurns;
    }

    public void BeginModelCall()
    {
        ThrowIfExhausted();

        while (true)
        {
            int current = Volatile.Read(ref _modelCalls);

            if (current >= _maxTurns)
            {
                MarkExhausted(DelegatedBudgetExhaustionReason.Turns);
                ThrowIfExhausted();
            }

            if (Interlocked.CompareExchange(
                    ref _modelCalls,
                    current + 1,
                    current) == current)
            {
                return;
            }
        }
    }

    public void RecordUsage(
        UsageDetails? usage,
        decimal costUsd)
    {
        RecordUsageCore(usage, costUsd);
        ThrowIfExhausted();
    }

    internal void RecordUsageDeferred(
        UsageDetails? usage,
        decimal costUsd) =>
        RecordUsageCore(usage, costUsd);

    public DelegatedManaUsage GetUsage()
    {
        decimal cost;

        lock (_costGate)
        {
            cost = _costUsd;
        }

        return new DelegatedManaUsage(
            Interlocked.Read(ref _tokens),
            cost,
            Volatile.Read(ref _modelCalls),
            Volatile.Read(ref _exhaustionReason) != 0);
    }

    public void ThrowIfExhausted()
    {
        int rawReason = Volatile.Read(ref _exhaustionReason);

        if (rawReason == 0)
        {
            return;
        }

        throw new BudgetExhaustedException(
            (DelegatedBudgetExhaustionReason)rawReason,
            GetUsage());
    }

    private void RecordUsageCore(
        UsageDetails? usage,
        decimal costUsd)
    {
        long tokens = ResolveTotalTokens(usage);

        if (tokens > 0)
        {
            _ = Interlocked.Add(ref _tokens, tokens);
        }

        if (costUsd > 0)
        {
            lock (_costGate)
            {
                _costUsd += costUsd;
            }
        }

        if (_maxTokens is { } maxTokens
            && Interlocked.Read(ref _tokens) > maxTokens)
        {
            MarkExhausted(DelegatedBudgetExhaustionReason.Tokens);
        }

        decimal currentCost;

        lock (_costGate)
        {
            currentCost = _costUsd;
        }

        if (_maxCostUsd is { } maxCostUsd
            && currentCost > maxCostUsd)
        {
            MarkExhausted(DelegatedBudgetExhaustionReason.Cost);
        }
    }

    private void MarkExhausted(DelegatedBudgetExhaustionReason reason) =>
        _ = Interlocked.CompareExchange(
            ref _exhaustionReason,
            (int)reason,
            comparand: 0);

    private static long ResolveTotalTokens(UsageDetails? usage)
    {
        if (usage is null)
        {
            return 0;
        }

        if (usage.TotalTokenCount is { } total)
        {
            return Math.Max(0L, total);
        }

        long input = Math.Max(0L, usage.InputTokenCount ?? 0L);
        long output = Math.Max(0L, usage.OutputTokenCount ?? 0L);

        return input > long.MaxValue - output
            ? long.MaxValue
            : input + output;
    }
}
