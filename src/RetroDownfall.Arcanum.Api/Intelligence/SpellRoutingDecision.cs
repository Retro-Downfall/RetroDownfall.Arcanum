using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// RAG Phase 5 — the three modes <see cref="SemanticSpellRouter"/> can resolve to for a given turn.
/// </summary>
public enum SpellRoutingDecisionMode
{

    /// <summary>Pure vector match: <see cref="SpellRoutingDecision.ResolvedSpell"/> is authoritative, no LLM call is made.</summary>
    DirectResonance,

    /// <summary>Hybrid mode: <see cref="SpellRoutingDecision.Candidates"/> is a pre-filtered top-K list for the LLM router.</summary>
    FilteredDivination,

    /// <summary>Disabled, or a graceful-degradation fallback: the LLM router picks from the full spell catalog, unchanged from pre-Phase-5 behavior.</summary>
    FullGrimoire,

}

/// <summary>
/// RAG Phase 5 — result of <see cref="SemanticSpellRouter.ResolveAsync"/>. Exactly one of
/// <see cref="ResolvedSpell"/> / <see cref="Candidates"/> is meaningful, depending on <see cref="Mode"/>.
/// </summary>
public sealed record SpellRoutingDecision(
    SpellRoutingDecisionMode Mode,
    SpellMetadata? ResolvedSpell,
    IReadOnlyList<SpellMetadata>? Candidates)
{

    public static SpellRoutingDecision DirectResonance(SpellMetadata? resolvedSpell) =>
        new(SpellRoutingDecisionMode.DirectResonance, resolvedSpell, null);

    public static SpellRoutingDecision FilteredDivination(IReadOnlyList<SpellMetadata> candidates) =>
        new(SpellRoutingDecisionMode.FilteredDivination, null, candidates);

    public static SpellRoutingDecision FullGrimoire() =>
        new(SpellRoutingDecisionMode.FullGrimoire, null, null);

}
