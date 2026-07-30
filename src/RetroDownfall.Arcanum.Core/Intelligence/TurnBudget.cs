namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Bounded turn budget with count/time/cost limits integrated into the pipeline.
/// Provider calls pass through TryConsumeModelCall; tool rounds pass through TryConsumeToolRound.
/// </summary>
public sealed class TurnBudget : ITurnBudget
{
    private readonly TurnLimits _limits;
    private int _modelCalls;
    private int _toolRounds;
    private int _toolCalls;
    private int _toolResultTokens;
    private int _toolResultBytes;
    private readonly DateTimeOffset _startedAt;
    private decimal _reservedCostUsd;

    public TurnBudget(TurnLimits limits)
    {
        _limits = limits;
        _startedAt = DateTimeOffset.UtcNow;
    }

    public bool TryConsumeModelCall(ContextTokenBreakdown breakdown, out string? violation)
    {
        if (_modelCalls >= _limits.MaxModelCalls)
        {
            violation = $"Turn budget: max model calls ({_limits.MaxModelCalls}) exceeded.";
            return false;
        }
        _modelCalls++;
        violation = null;
        return true;
    }

    public bool TryConsumeToolRound(out string? violation)
    {
        if (_toolRounds >= _limits.MaxToolRounds)
        {
            violation = $"Turn budget: max tool rounds ({_limits.MaxToolRounds}) exceeded.";
            return false;
        }
        _toolRounds++;
        violation = null;
        return true;
    }

    public bool TryConsumeToolCall(string resultText, out string? violation)
    {
        if (_toolCalls >= _limits.MaxToolCalls)
        {
            violation = $"Turn budget: max tool calls ({_limits.MaxToolCalls}) exceeded.";
            return false;
        }
        _toolCalls++;

        int resultBytes = System.Text.Encoding.UTF8.GetByteCount(resultText);
        _toolResultBytes += resultBytes;
        if (_toolResultBytes > _limits.MaxToolResultBytes)
        {
            violation = $"Turn budget: max tool result bytes ({_limits.MaxToolResultBytes}) exceeded.";
            return false;
        }

        violation = null;
        return true;
    }

    public bool TryConsumeElapsedTime(out string? violation)
    {
        if (DateTimeOffset.UtcNow - _startedAt > _limits.MaxElapsedTime)
        {
            violation = $"Turn budget: max elapsed time ({_limits.MaxElapsedTime}) exceeded.";
            return false;
        }
        violation = null;
        return true;
    }

    public bool TryConsumeEstimatedCost(decimal cost, out string? violation)
    {
        if (cost > _limits.MaxEstimatedCostUsd)
        {
            violation = $"Turn budget: estimated cost exceeds limit ({_limits.MaxEstimatedCostUsd}).";
            return false;
        }
        violation = null;
        return true;
    }

    public bool TryConsumeReservedCost(decimal cost, out string? violation)
    {
        _reservedCostUsd += cost;
        if (_reservedCostUsd > _limits.MaxReservedCostUsd)
        {
            violation = $"Turn budget: reserved cost exceeds limit ({_limits.MaxReservedCostUsd}).";
            return false;
        }
        violation = null;
        return true;
    }
}
