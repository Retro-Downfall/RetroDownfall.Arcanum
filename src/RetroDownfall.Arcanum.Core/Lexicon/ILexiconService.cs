using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.Lexicon;

/// <summary>
/// Agent-directed Lexicon memory: a structured entity graph (Name + Type + Facts) persisted in the
/// Grimoire via raw SQL over <c>lexicon_entries</c> with an FTS5 <c>lexicon_fts</c> index (see
/// <c>LexiconSchemaInitializer</c>). Abstracted behind an interface so the same domain logic backs
/// the in-process MCP tools and (later) Minimal API routes. Not used by the legacy operator
/// key-value Lore surface (<c>/api/lore</c>, <c>arcanum lore</c>, <c>MageSettings</c>).
/// </summary>
public interface ILexiconService
{

    /// <summary>
    /// Creates a new entity or appends non-duplicate facts to an existing entity matched by
    /// case-insensitive name. Type semantics: new entity + blank type defaults to
    /// <c>General</c>; existing + blank preserves the stored type; non-empty refreshes it.
    /// </summary>
    Task<Result<LexiconEntryDto>> UpsertAsync(
        string name,
        string? type,
        IReadOnlyList<string> facts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores facts derived from a successfully materialized attachment version together with typed
    /// provenance. Callers must validate current-turn materialization before invoking this overload.
    /// </summary>
    Task<Result<LexiconEntryDto>> UpsertAsync(
        string name,
        string? type,
        IReadOnlyList<string> facts,
        AttachmentMemoryProvenance provenance,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(name, type, facts, cancellationToken);

    /// <summary>Removes an entity (and its FTS row) by case-insensitive name. Returns false when not found.</summary>
    Task<Result<bool>> DeleteByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tiered retrieval: exact <c>NameNormalized</c> matches first, then column-weighted FTS5
    /// (<c>bm25(lexicon_fts, 3.0, 2.0, 1.0)</c>) for unresolved terms. Deduplicated by Id; exact
    /// hits ordered before FTS hits. Empty entity input returns an empty result without querying.
    /// </summary>
    Task<Result<IReadOnlyList<LexiconEntryDto>>> MatchEntitiesAsync(
        IReadOnlyList<string> entities,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Looks up a single entity by case-insensitive name; null when not found.</summary>
    Task<Result<LexiconEntryDto?>> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every Lexicon entity for explicit inspection. This is intentionally not tied to the
    /// prompt-time match limit because inspection must not hide durable memory from the operator.
    /// </summary>
    Task<Result<IReadOnlyList<LexiconEntryDto>>> ListAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Result<IReadOnlyList<LexiconEntryDto>>.Failure(
                new Error(
                    ErrorCodes.Lexicon.SearchFailed,
                    "Lexicon listing is unavailable.")));

}
