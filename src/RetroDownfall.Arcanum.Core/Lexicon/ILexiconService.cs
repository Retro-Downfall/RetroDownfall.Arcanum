using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.Lexicon;

/// <summary>
/// Agent-directed Lexicon memory: a structured entity graph (Name + Type + Facts) persisted in the
/// Grimoire via raw SQL over <c>lexicon_entries</c> with an FTS5 <c>lexicon_fts</c> index (see
/// <c>Infrastructure/Data/Schema/</c>). Abstracted behind an interface so the same domain logic backs
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
    /// <remarks>
    /// <paramref name="scope"/> selects the tier written, and is never inferred from the name: a
    /// Campaign-scoped write creates or appends to that Campaign's entity and leaves the global one of
    /// the same name untouched, and the reverse.
    /// </remarks>
    Task<Result<LexiconEntryDto>> UpsertAsync(
        string name,
        string? type,
        IReadOnlyList<string> facts,
        LexiconScope scope,
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
        LexiconScope scope,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(name, type, facts, scope, cancellationToken);

    /// <summary>
    /// Removes an entity (and its FTS row) by case-insensitive name, within one tier. Returns false when
    /// that tier holds no such entity — deleting a Campaign's entity never removes the global one.
    /// </summary>
    Task<Result<bool>> DeleteByNameAsync(
        string name,
        LexiconScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tiered retrieval: exact <c>NameNormalized</c> matches first, then column-weighted FTS5
    /// (<c>bm25(lexicon_fts, 3.0, 2.0, 1.0)</c>) for unresolved terms. Deduplicated by Id; exact
    /// hits ordered before FTS hits. Empty entity input returns an empty result without querying.
    /// </summary>
    /// <remarks>
    /// With a Campaign <paramref name="scope"/> the whole exact-then-FTS pass runs against that
    /// Campaign's tier first and then against the global one, and a Campaign entity <i>shadows</i> a
    /// global entity of the same name. Shadowing rather than merging is the point: the model is never
    /// handed two contradictory answers to one term and left to pick.
    ///
    /// <para>A global scope reads the global tier alone, which is exactly what every match saw before
    /// scopes existed.</para>
    /// </remarks>
    Task<Result<IReadOnlyList<LexiconEntryDto>>> MatchEntitiesAsync(
        IReadOnlyList<string> entities,
        int limit,
        LexiconScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a single entity by case-insensitive name, resolving the Campaign tier before the global
    /// one exactly as <see cref="MatchEntitiesAsync"/> does; null when neither holds it.
    /// </summary>
    Task<Result<LexiconEntryDto?>> GetByNameAsync(
        string name,
        LexiconScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up the entity held by exactly one tier, with no fallback to the global one.
    /// </summary>
    /// <remarks>
    /// The tier-exact counterpart to <see cref="GetByNameAsync"/>, for the callers that must act on one
    /// scope's entity rather than on whichever entity a turn would see. Deletion is the reason it
    /// exists: resolving a Campaign delete through the shadowing lookup would find the global entity
    /// whenever the Campaign held none, and act on it.
    /// </remarks>
    Task<Result<LexiconEntryDto?>> GetByNameInScopeAsync(
        string name,
        LexiconScope scope,
        CancellationToken cancellationToken = default);

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
