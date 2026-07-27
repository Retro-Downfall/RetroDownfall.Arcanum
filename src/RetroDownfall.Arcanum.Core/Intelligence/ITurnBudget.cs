namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Compatibility marker carried by turn-accounting handles.
/// Provider calls are admitted by context, cache, fingerprint, and cost checks rather than counts.
/// </summary>
public interface ITurnBudget
{
}

/// <summary>Default count-free turn-accounting marker.</summary>
public sealed class TurnBudget : ITurnBudget
{
}
