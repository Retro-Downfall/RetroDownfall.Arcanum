namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

/// <summary>
/// Dry-run preview of what casting a spell would assemble for inference — the operator sees the
/// composed system prompt, resonant dependencies, attuned tools, and spell scripts without
/// consuming inference tokens (no LLM call is made).
/// </summary>
public sealed record SpellCastResult(
    string SpellName,
    string? SpellDescription,
    string SystemPrompt,
    string[] ResonantDependencies,
    string[] AvailableTools,
    string[] AvailableSpellScripts,
    string? CodexContent,
    bool HasDeclaredToolsFilter = false);
