using RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Compatibility marker carried by turn-accounting handles.
/// Provider admission checks the selected model context and the prepared request identity.
/// </summary>
public interface ITurnBudget
{
}

/// <summary>
/// Shared marker for inference paths that have no arbitrary turn counter, elapsed-time deadline,
/// or cumulative tool-result quota.
/// </summary>
public sealed class UnrestrictedTurnBudget : ITurnBudget
{

    public static UnrestrictedTurnBudget Instance { get; } = new();

    private UnrestrictedTurnBudget()
    {
    }

}
