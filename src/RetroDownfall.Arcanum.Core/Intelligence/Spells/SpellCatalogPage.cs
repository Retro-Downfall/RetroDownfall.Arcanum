using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

/// <summary>
/// Arcanum-owned metadata query for the bounded spell-catalog surface. It deliberately excludes
/// campaign and other Forge concepts.
/// </summary>
public sealed record SpellCatalogQuery(
    string? Query = null,
    string? Tag = null,
    string? Tool = null,
    SpellSource? Source = null,
    string? Cursor = null);

/// <summary>One deterministic catalog page. Full spell bodies are never part of this contract.</summary>
public sealed record SpellCatalogPage(
    SpellSummary[] Items,
    bool HasMore,
    string? NextCursor,
    string? ContinuationAction);

public interface IArcanumSpellCatalog
{

    Task<Result<SpellCatalogPage>> PageAsync(
        string? workingDirectory,
        SpellCatalogQuery query,
        CancellationToken cancellationToken);

}
