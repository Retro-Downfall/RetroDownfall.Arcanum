namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Per-turn resource ceilings seeded from <see cref="Configuration.TurnLimitsDefaults"/>.
/// Enforced call-by-call before provider invocations; shared by reservations and model-call gating.
/// </summary>
public interface ITurnBudget
{

    int MaxToolRounds { get; }

    int MaxModelCalls { get; }

    int MaxToolCalls { get; }

    int MaxToolCallsPerRound { get; }

    int MaxSideEffectingToolCalls { get; }

    int RemainingModelCalls { get; }

    int RemainingToolRounds { get; }

    int RemainingToolCalls { get; }

    /// <summary>Records one provider model invocation; returns false when the model-call ceiling is exhausted.</summary>
    bool TryConsumeModelCall();

    /// <summary>Records completion of one tool round; returns false when the round ceiling is exhausted.</summary>
    bool TryConsumeToolRound();

    /// <summary>Records <paramref name="count"/> tool invocations in the current round.</summary>
    bool TryConsumeToolCalls(int count, int sideEffectingCount = 0);

}

/// <summary>Default mutable turn budget.</summary>
public sealed class TurnBudget : ITurnBudget
{

    private int _modelCalls;

    private int _toolRounds;

    private int _toolCalls;

    private int _sideEffectingToolCalls;

    public TurnBudget(
        int maxToolRounds = Configuration.TurnLimitsDefaults.MaxToolRounds,
        int maxModelCalls = Configuration.TurnLimitsDefaults.MaxModelCalls,
        int maxToolCalls = Configuration.TurnLimitsDefaults.MaxToolCalls,
        int maxToolCallsPerRound = Configuration.TurnLimitsDefaults.MaxToolCallsPerRound,
        int maxSideEffectingToolCalls = Configuration.TurnLimitsDefaults.MaxSideEffectingToolCalls)
    {
        MaxToolRounds = Math.Max(1, maxToolRounds);
        MaxModelCalls = Math.Max(1, maxModelCalls);
        MaxToolCalls = Math.Max(1, maxToolCalls);
        MaxToolCallsPerRound = Math.Max(1, maxToolCallsPerRound);
        MaxSideEffectingToolCalls = Math.Max(0, maxSideEffectingToolCalls);
    }

    public int MaxToolRounds { get; }

    public int MaxModelCalls { get; }

    public int MaxToolCalls { get; }

    public int MaxToolCallsPerRound { get; }

    public int MaxSideEffectingToolCalls { get; }

    public int RemainingModelCalls => Math.Max(0, MaxModelCalls - _modelCalls);

    public int RemainingToolRounds => Math.Max(0, MaxToolRounds - _toolRounds);

    public int RemainingToolCalls => Math.Max(0, MaxToolCalls - _toolCalls);

    public bool TryConsumeModelCall()
    {
        if (_modelCalls >= MaxModelCalls)
        {
            return false;
        }

        _modelCalls++;

        return true;
    }

    public bool TryConsumeToolRound()
    {
        if (_toolRounds >= MaxToolRounds)
        {
            return false;
        }

        _toolRounds++;

        return true;
    }

    public bool TryConsumeToolCalls(int count, int sideEffectingCount = 0)
    {
        if (count < 0 || sideEffectingCount < 0)
        {
            return false;
        }

        if (count > MaxToolCallsPerRound)
        {
            return false;
        }

        if (_toolCalls + count > MaxToolCalls)
        {
            return false;
        }

        if (_sideEffectingToolCalls + sideEffectingCount > MaxSideEffectingToolCalls)
        {
            return false;
        }

        _toolCalls += count;
        _sideEffectingToolCalls += sideEffectingCount;

        return true;
    }

}
