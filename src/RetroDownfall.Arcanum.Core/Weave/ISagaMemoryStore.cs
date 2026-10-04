namespace RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// RAG Phase 4 — raw-SQL persistence for Saga memories (<c>saga_memories</c> +
/// <c>saga_memory_embeddings</c> [+ the <c>saga_memory_embeddings_vec</c> mirror where one exists] +
/// <c>saga_extraction_watermarks</c>; see <c>Infrastructure/Data/Schema/Tables/</c>). Shared by
/// <c>SagaExtractionService</c> (writes), the <c>/api/saga</c> endpoints (reads/deletes), and the
/// <c>read_saga</c> MCP tool (reads), so all three surfaces stay consistent without duplicating SQL.
/// </summary>
/// <remarks>
/// One rule governs the vector mirror on every write, and it is read from the database rather than
/// from whether this process loaded a sqlite-vec accelerator. A plain-table mirror has its rows for
/// a memory deleted whatever the accelerator flag says, because a mirror an earlier build filled still
/// holds that memory's embedding; a row is written only while the accelerator is live. A legacy
/// <c>vec0</c> virtual mirror, which this runtime cannot open, is never touched. No schema file
/// installs the mirror, so where none exists there is nothing to write or remove.
/// </remarks>
public interface ISagaMemoryStore
{

    /// <summary>
    /// Inserts a new memory: a row in <c>saga_memories</c>, its BLOB embedding in
    /// <c>saga_memory_embeddings</c>, and, only while the accelerator is live, a mirrored row in a
    /// plain <c>saga_memory_embeddings_vec</c>.
    /// </summary>
    /// <remarks>
    /// <para>Returns <see cref="SagaMemoryWriteOutcome.Suppressed"/>, writing nothing, when an operator
    /// has already retired an equivalent conclusion in this scope, or erased this exact content in this
    /// exact scope. Both checks run inside the insert transaction, after scope is derived and before any
    /// row lands, so no writer — extraction included — can reach around them. This is the authoritative
    /// erasure chokepoint; extraction's earlier checks only save it a model or embedding call.</para>
    ///
    /// <para>When the store holds erasure fingerprints that the erasure key cannot verify, because the
    /// key is lost, unreadable, or not the key that recorded them, the insert fails closed and throws
    /// rather than writing anything.</para>
    /// </remarks>
    Task<SagaMemoryWriteOutcome> InsertAsync(
        string id,
        string content,
        DateTimeOffset createdAt,
        Guid? sessionId,
        string? tags,
        string? source,
        float[] embedding,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts attachment-derived memory together with typed provenance. Implementations must keep
    /// provenance after source deletion and surface the source as unavailable.
    /// </summary>
    Task<SagaMemoryWriteOutcome> InsertAsync(
        string id,
        string content,
        DateTimeOffset createdAt,
        Guid? sessionId,
        string? tags,
        string? source,
        float[] embedding,
        AttachmentMemoryProvenance provenance,
        CancellationToken cancellationToken) =>
        InsertAsync(
            id,
            content,
            createdAt,
            sessionId,
            tags,
            source,
            embedding,
            cancellationToken);

    /// <summary>Total number of Saga memories across all sessions.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken);

    /// <summary>Number of Saga memories associated with a single session.</summary>
    Task<int> CountBySessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Paginated listing, optionally filtered by a case-insensitive substring match on
    /// <c>Content</c> and/or an exact <c>SessionId</c> match. Ordered by <c>CreatedAt DESC</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="scope"/> narrows the listing by the same ownership retrieval ranks by, so an
    /// operator inspecting Saga is not shown memories a turn in that scope could never reach.
    /// <see cref="MemoryScope.Installation"/> narrows nothing, which is the whole listing this surface
    /// has always returned.
    ///
    /// <para>Ownership is all that is shared. A retired memory is listed exactly as a live one is --
    /// this reads <c>saga_memories</c> and retirement removes only the embeddings retrieval ranks
    /// through -- so the two surfaces agree about who owns a memory and not about whether a turn can
    /// recall it. Each row carries <see cref="SagaMemoryDto.RetiredAtUtc"/> and
    /// <see cref="SagaMemoryDto.PinnedAtUtc"/>, which <c>saga list</c> renders as its <c>State</c>
    /// column.</para>
    /// </remarks>
    Task<SagaMemoryDto[]> ListAsync(
        string? query,
        Guid? sessionId,
        MemoryScope scope,
        int limit,
        int offset,
        CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="ListAsync"/>'s page, each memory read together with its curation lifecycle and
    /// whether it still has an embedding.
    /// </summary>
    /// <remarks>
    /// The same query, the same arguments, and so the same memories in the same order as
    /// <see cref="ListAsync"/>: the embedding probe is one projected column and never a filter. What it
    /// adds is what <see cref="SagaRetrievalEligibilityClassifier"/> needs, read in the same statement
    /// as the row, so search can say whether a turn can still recall each hit.
    /// </remarks>
    Task<SagaMemoryCurationRow[]> ListCurationRowsAsync(
        string? query,
        Guid? sessionId,
        MemoryScope scope,
        int limit,
        int offset,
        CancellationToken cancellationToken);

    /// <summary>
    /// One bounded page of memory positions for a walk that removes what it reads: every memory, in one
    /// total order, strictly after <paramref name="after"/>, or from the newest when it is
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>Newest first, with the identity breaking a tie, so no two memories share a place and a page
    /// boundary can fall inside a group of memories that share a <c>CreatedAt</c>. The next page is
    /// whatever follows the last position this one returned, never a count of rows to skip: a row removed
    /// in the meantime cannot slide a later one past the walk, and a row already removed is never read
    /// again. The position that resumes a walk does not have to belong to a row that still exists.</para>
    ///
    /// <para>Deliberately takes no scope, Session or text filter. Erasure has to reach every memory,
    /// including the ones no turn in any Campaign can currently retrieve, so this lists them all.
    /// <paramref name="limit"/> must be at least 1; a non-positive limit is refused rather than read as
    /// "no limit".</para>
    /// </remarks>
    Task<SagaMemoryPosition[]> ListPositionsAfterAsync(
        SagaMemoryPosition? after,
        int limit,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromException<SagaMemoryPosition[]>(
            new NotSupportedException(
                "This Saga memory store does not expose an ordered keyset walk over its memories."));

    }

    /// <summary>
    /// Whether a turn in <paramref name="scope"/> could reach at least one memory: one that is not
    /// retired, still has an embedding, and is owned by that scope.
    /// </summary>
    /// <remarks>
    /// Mirrors retrieval's own choice rather than restating it. With Campaign scoping off a turn ranks
    /// every embedded memory, so this asks about all of them; with it on, only installation-scoped
    /// memories and the resolved Campaign's own, as the scoped search ranks. This is what <c>memory
    /// explain</c> reports; <see cref="CountAsync"/> keeps reporting what is stored.
    /// </remarks>
    Task<bool> AnyRetrievableAsync(MemoryScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Looks up memories by id (as returned by <c>IDivinationService.SearchAsync</c> against
    /// <c>saga_memory_embeddings_vec</c>), for joining Divination hits against their content/metadata.
    /// Missing ids are simply absent from the result — never an error.
    /// </summary>
    Task<IReadOnlyDictionary<string, SagaMemoryDto>> GetByIdsAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken);

    /// <summary>
    /// One memory's row, its curation lifecycle, and whether it still has an embedding, read together.
    /// </summary>
    /// <remarks>
    /// One read rather than three. A caller that asked for the row, then the lifecycle, then the embedding
    /// would be describing three instants as though they were one, and the detail view exists to say what
    /// is true now.
    /// </remarks>
    Task<SagaMemoryCurationRow?> ReadCurationRowAsync(string id, CancellationToken cancellationToken);

    /// <summary>Deletes a single memory (and its embedding, from the BLOB table and from a plain mirror whatever the accelerator flag says). Returns <c>false</c> when no such memory exists.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Retires a memory: its embedding is removed from <c>saga_memory_embeddings</c> and from a plain
    /// <c>saga_memory_embeddings_vec</c> whatever the accelerator flag says, so no retrieval path can
    /// reach it, while the <c>saga_memories</c> row itself survives for inspection and for reversal.
    /// </summary>
    /// <remarks>
    /// <paramref name="expectedContentDigest"/> is the caller's proof that it read the content it is
    /// retiring, compared against <c>AnnalContentDigest.ForSagaMemory</c> of the content stored now.
    /// A mismatch means the caller's view is stale and nothing is written.
    /// </remarks>
    Task<SagaCurationOutcome> RetireAsync(
        string id,
        byte[] expectedContentDigest,
        DateTimeOffset retiredAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reinstates a retired memory: its embedding is restored to <c>saga_memory_embeddings</c> from
    /// <paramref name="embedding"/>, and mirrored into a plain <c>saga_memory_embeddings_vec</c> only
    /// while the accelerator is live (otherwise any mirror row the memory still has is removed), and the
    /// retirement suppression over its content-and-scope is released so a later extraction pass may
    /// write it again.
    /// </summary>
    Task<SagaCurationOutcome> ReinstateAsync(
        string id,
        byte[] expectedContentDigest,
        float[] embedding,
        DateTimeOffset reinstatedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces one memory's text in place: <c>saga_memories.Content</c>, its BLOB embedding in
    /// <c>saga_memory_embeddings</c>, and its row in a plain <c>saga_memory_embeddings_vec</c> — rewritten
    /// while the accelerator is live, and otherwise removed, because the old vector describes text the
    /// memory no longer holds.
    /// </summary>
    /// <remarks>
    /// <paramref name="expectedContentDigest"/> is the caller's proof that it read the content it is
    /// correcting, exactly as <see cref="RetireAsync"/>'s does. Correcting a retired memory is refused —
    /// reinstate it first — and correcting to the text already stored returns
    /// <see cref="SagaCurationOutcomeKind.Unchanged"/> rather than recording a revision that changed
    /// nothing. The memory's own <c>CreatedAt</c> and any sensitivity label it carries are left alone: a
    /// correction is a new statement about the same memory, not a new memory.
    /// </remarks>
    Task<SagaCurationOutcome> CorrectAsync(
        string id,
        byte[] expectedContentDigest,
        string content,
        float[] embedding,
        DateTimeOffset correctedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks (or unmarks) one memory as durable, so a later retention pass will not prune it.
    /// </summary>
    /// <remarks>
    /// Takes no content digest, deliberately: a pin is not a content mutation, and requiring proof of
    /// what the text says would make pinning fail after an unrelated correction — friction with no
    /// safety behind it. Pinning what is already pinned re-stamps <c>PinnedAtUtc</c> and still returns
    /// <see cref="SagaCurationOutcomeKind.Applied"/>; there is no history table here for a no-op to
    /// pollute. A pin binds only the automatic retention path — it never blocks an operator's own
    /// correct, retire, or delete.
    /// </remarks>
    Task<SagaCurationOutcome> SetPinAsync(
        string id,
        bool pinned,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken);

    /// <summary>Deletes every Saga memory, embedding (including a plain mirror's rows, whatever the accelerator flag says), and extraction watermark.</summary>
    Task DeleteAllAsync(CancellationToken cancellationToken);

    /// <summary>Aggregate counts and timestamp bounds across all Saga memories.</summary>
    Task<SagaStats> GetStatsAsync(CancellationToken cancellationToken);

    /// <summary>The exact Grimoire entry through which Saga extraction has committed, or <c>null</c>.</summary>
    Task<SagaExtractionCursor?> GetExtractionCursorAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromException<SagaExtractionCursor?>(
            new NotSupportedException(
                "This Saga memory store does not expose an exact extraction sequence cursor."));

    }

    /// <summary>Upserts the exact committed extraction cursor for a session.</summary>
    Task SetExtractionCursorAsync(
        Guid sessionId,
        SagaExtractionCursor cursor,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromException(
            new NotSupportedException(
                "This Saga memory store does not persist an exact extraction sequence cursor."));

    }

    /// <summary>The <c>CreatedAt</c> of the most recently extracted Grimoire entry for a session, or <c>null</c> when no extraction has occurred yet.</summary>
    Task<DateTimeOffset?> GetWatermarkAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Upserts the extraction watermark for a session.</summary>
    Task SetWatermarkAsync(Guid sessionId, DateTimeOffset lastExtractedEntryCreatedAt, CancellationToken cancellationToken);

}

/// <summary>
/// Where one memory falls in the order a bulk walk visits Saga memories: newest first, the identity
/// breaking a tie.
/// </summary>
/// <param name="CreatedAt">
/// The memory's <c>CreatedAt</c> exactly as stored, which is the text the database orders by. A caller
/// hands it back unchanged as part of the position and never parses or reformats it, so a stored value
/// that would not survive a round trip through a timestamp still resumes the walk at the right row.
/// </param>
/// <param name="Id">The memory's identity exactly as stored.</param>
public sealed record SagaMemoryPosition(string CreatedAt, string Id);
