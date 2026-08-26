namespace RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// RAG Phase 4 — raw-SQL persistence for Saga memories (<c>saga_memories</c> +
/// <c>saga_memory_embeddings</c> [+ <c>saga_memory_embeddings_vec</c> when available] +
/// <c>saga_extraction_watermarks</c>; see <c>Infrastructure/Data/Schema/Tables/</c>). Shared by
/// <c>SagaExtractionService</c> (writes), the <c>/api/saga</c> endpoints (reads/deletes), and the
/// <c>read_saga</c> MCP tool (reads), so all three surfaces stay consistent without duplicating SQL.
/// </summary>
public interface ISagaMemoryStore
{

    /// <summary>
    /// Inserts a new memory: a row in <c>saga_memories</c>, its BLOB embedding in
    /// <c>saga_memory_embeddings</c>, and (when sqlite-vec is available) a mirrored row in
    /// <c>saga_memory_embeddings_vec</c>.
    /// </summary>
    Task InsertAsync(
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
    Task InsertAsync(
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
    /// <paramref name="scope"/> narrows the listing to the candidate set retrieval would rank, so an
    /// operator inspecting Saga sees what the model can reach rather than everything on the
    /// installation. <see cref="MemoryScope.Installation"/> narrows nothing, which is the whole listing
    /// this surface has always returned.
    /// </remarks>
    Task<SagaMemoryDto[]> ListAsync(
        string? query,
        Guid? sessionId,
        MemoryScope scope,
        int limit,
        int offset,
        CancellationToken cancellationToken);

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

    /// <summary>Deletes a single memory (and its embedding, from both BLOB and vec0 tables). Returns <c>false</c> when no such memory exists.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Retires a memory: its embedding is removed from both <c>saga_memory_embeddings</c> and (when
    /// available) <c>saga_memory_embeddings_vec</c>, so no retrieval path can reach it, while the
    /// <c>saga_memories</c> row itself survives for inspection and for reversal.
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
    /// Reinstates a retired memory: its embedding is restored to both <c>saga_memory_embeddings</c> and
    /// (when available) <c>saga_memory_embeddings_vec</c> from <paramref name="embedding"/>, and the
    /// retirement suppression over its content-and-scope is released so a later extraction pass may
    /// write it again.
    /// </summary>
    Task<SagaCurationOutcome> ReinstateAsync(
        string id,
        byte[] expectedContentDigest,
        float[] embedding,
        DateTimeOffset reinstatedAt,
        CancellationToken cancellationToken);

    /// <summary>Deletes every Saga memory, embedding, and extraction watermark.</summary>
    Task DeleteAllAsync(CancellationToken cancellationToken);

    /// <summary>Aggregate counts and timestamp bounds across all Saga memories.</summary>
    Task<SagaStats> GetStatsAsync(CancellationToken cancellationToken);

    /// <summary>The <c>CreatedAt</c> of the most recently extracted Grimoire entry for a session, or <c>null</c> when no extraction has occurred yet.</summary>
    Task<DateTimeOffset?> GetWatermarkAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Upserts the extraction watermark for a session.</summary>
    Task SetWatermarkAsync(Guid sessionId, DateTimeOffset lastExtractedEntryCreatedAt, CancellationToken cancellationToken);

}
