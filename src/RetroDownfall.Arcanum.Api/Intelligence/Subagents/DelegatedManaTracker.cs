using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Api.Intelligence.Subagents;

public sealed record DelegatedManaUsage(
    long Tokens,
    decimal CostUsd,
    int ModelCalls);

/// <summary>
/// Records the observed usage of a single isolated subagent run.
/// </summary>
public sealed class DelegatedManaTracker
{
    private readonly object _costGate = new();

    private long _tokens;

    private decimal _costUsd;

    private int _modelCalls;

    public void BeginModelCall()
    {
        _ = Interlocked.Increment(ref _modelCalls);
    }

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
            Volatile.Read(ref _modelCalls));
    }

    public void RecordUsage(
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
    }

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
