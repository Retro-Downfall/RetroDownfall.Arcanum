namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// The table and column names Saga's own retrieval code owns, in one place, the way
/// <c>TapestryStorageKeys</c> owns The Tapestry's.
/// </summary>
/// <remarks>
/// Every surface that searches Saga - the turn path, <c>POST /api/saga/divine</c>, <c>arcanum saga</c>,
/// and the <c>read_saga</c> tool - builds its scope from here. That is what makes "inspection matches
/// retrieval" structural rather than a promise four call sites have to keep separately.
///
/// <para>These names reach <c>DivinationService</c> as interpolated SQL identifiers, which is the same
/// trust model the <c>Data/Schema/</c> object files rely on: they are internal constants, never input.
/// Only the Campaign identity is ever bound as a parameter.</para>
/// </remarks>
public static class SagaStorageKeys
{

    /// <summary>The vec0 acceleration table. Divination strips the suffix for the managed path.</summary>
    public const string VectorTable = "saga_memory_embeddings_vec";

    /// <summary>The memory table joined for scoped searches; it carries the scope classification.</summary>
    public const string MemoryTable = "saga_memories";

    public const string EmbeddingKeyColumn = "MemoryId";

    public const string MemoryKeyColumn = "Id";

    public const string EmbeddingColumn = "Embedding";

    public const string ScopeKindColumn = "ScopeKindCode";

    public const string CampaignColumn = "CampaignId";

    /// <summary>
    /// The candidate set a turn resolved to <paramref name="campaignId"/> may draw on: every explicitly
    /// installation-scoped memory, plus that Campaign's own.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="campaignId"/> is a turn that resolved to no Campaign, and it leaves the
    /// installation-scoped memories alone in the candidate set. That is also, exactly, what such a turn's
    /// own Session contributes: a Global-only Session's memories are themselves Global.
    ///
    /// <para>Neither unclassified nor unresolved memories are ever admitted, in any scope. A memory whose
    /// ownership nobody has stated is not installation-global by default, and treating it as such is what
    /// would let a half-drained upgrade publish one Campaign's conclusions to every other.</para>
    /// </remarks>
    public static DivinationCampaignScope CampaignScope(Guid? campaignId) =>
        new(
            MemoryTable,
            MemoryKeyColumn,
            ScopeKindColumn,
            CampaignColumn,
            (int)SagaMemoryScopeKind.Global,
            (int)SagaMemoryScopeKind.Campaign,
            campaignId);

}
