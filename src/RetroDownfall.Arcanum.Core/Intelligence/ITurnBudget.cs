using RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Compatibility marker carried by turn-accounting handles.
/// Provider calls are admitted by context, cache, fingerprint, and cost checks rather than counts.
/// </summary>
public interface ITurnBudget
{
}
