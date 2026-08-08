namespace RetroDownfall.Arcanum.Core.Weave.Tapestry;

/// <summary>
/// Raw-SQL persistence for The Tapestry (DESIGN §21.11). None of <c>tapestry_generations</c>,
/// <c>tapestry_nodes</c>, <c>tapestry_node_embeddings</c>, or <c>tapestry_node_embeddings_vec</c> is
/// part of the compiled EF model — they are declared in <c>Infrastructure/Data/Schema/Tables/</c>, mirroring
/// <c>ISagaMemoryStore</c>.
///
/// <para>Generations are immutable. A build stages into a <see cref="TapestryGenerationStatus.Building"/>
/// generation that retrieval never sees; <see cref="PublishGenerationAsync"/> is the single atomic
/// switch that supersedes the previous complete generation. A corpus with no complete generation
/// simply contributes no context.</para>
/// </summary>
public interface ITapestryStore
{

    /// <summary>
    /// Enumerates every corpus that currently has indexable rows, respecting the code-owned
    /// per-corpus participation flags. A corpus whose source feature never indexed anything yields
    /// no scope, which is how the Tapestry degrades when (say) codebase retrieval is off.
    /// </summary>
    Task<IReadOnlyList<TapestryScope>> DiscoverScopesAsync(
        bool includeWorkspace,
        bool includeSessionAttachments,
        bool includeSessions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates one scope's leaf sources in stable id order, carrying each source feature's
    /// already-imprinted embedding when one exists at <paramref name="expectedDimensions"/> so a
    /// rebuild re-embeds only what it must.
    ///
    /// Pass <paramref name="includeEmbeddings"/> as <c>false</c> to skip the embedding join entirely and
    /// leave every <see cref="TapestryLeafSource.ExistingEmbedding"/> null. The corpus fingerprint that decides
    /// whether a rebuild is needed at all consumes only source ids and content hashes, so reading and
    /// decoding a vector per leaf to answer "has anything changed?" is pure waste on the overwhelmingly
    /// common unchanged path.
    /// </summary>
    Task<IReadOnlyList<TapestryLeafSource>> EnumerateLeafSourcesAsync(
        TapestryScope scope,
        int expectedDimensions,
        bool includeEmbeddings,
        CancellationToken cancellationToken);

    /// <summary>The single published generation for a scope, or <c>null</c> when none is complete.</summary>
    Task<TapestryGeneration?> GetCurrentGenerationAsync(
        TapestryScope scope,
        CancellationToken cancellationToken);

    /// <summary>Creates a staging generation. Returns its id; retrieval cannot see it.</summary>
    Task<string> BeginGenerationAsync(
        TapestryScope scope,
        string algorithmVersion,
        string settingsFingerprint,
        string? summaryModel,
        string summaryRecipeVersion,
        int embeddingDimension,
        string corpusFingerprint,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken);

    /// <summary>Appends one checkpoint's nodes and embeddings to a staging generation.</summary>
    Task AppendNodesAsync(
        IReadOnlyList<TapestryNodeWrite> nodes,
        CancellationToken cancellationToken);

    /// <summary>Links a set of child nodes to the summary node that now covers them.</summary>
    Task SetParentAsync(
        string generationId,
        string parentNodeId,
        IReadOnlyList<string> childNodeIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// The atomic current-generation switch: marks the staging generation complete and every prior
    /// complete generation for the same scope superseded, in one transaction. Called only after every
    /// required layer, node, and embedding is durable.
    /// </summary>
    Task PublishGenerationAsync(
        string generationId,
        int layerCount,
        int nodeCount,
        int rootNodeCount,
        TapestryTerminalReason terminalReason,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    /// <summary>Drops a staging generation and everything under it. The prior complete generation is untouched.</summary>
    Task AbandonGenerationAsync(string generationId, CancellationToken cancellationToken);

    /// <summary>
    /// Reconciliation after an interrupted or cancelled build: removes every generation that is not
    /// the current complete one. Returns how many were removed.
    /// </summary>
    Task<int> ReconcileGenerationsAsync(CancellationToken cancellationToken);

    /// <summary>The nodes of one layer of a generation, in stable id order.</summary>
    Task<IReadOnlyList<TapestryNode>> GetLayerNodesAsync(
        string generationId,
        int layer,
        CancellationToken cancellationToken);

    /// <summary>The embeddings for a set of nodes, keyed by node id.</summary>
    Task<IReadOnlyDictionary<string, float[]>> GetNodeEmbeddingsAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Looks up one reusable summary in a scope's current complete generation by exact
    /// child-membership hash, so an unchanged cluster skips another model call.
    ///
    /// <para>Deliberately a point lookup rather than a whole-generation materialization: a large
    /// corpus can hold thousands of summary nodes, and loading every one of them (with its
    /// embedding) into memory just to answer per-cluster questions is exactly the unbounded
    /// materialization the rest of The Weave avoids. The index on
    /// <c>(GenerationId, ChildMembershipHash)</c> makes each lookup cheap, and it runs at most once
    /// per cluster — alongside a model call that would otherwise cost far more.</para>
    /// </summary>
    Task<TapestrySummaryReuseCandidate?> TryGetReusableSummaryAsync(
        TapestryScope scope,
        string childMembershipHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// Hydrates retrieved nodes with content, lineage, and provenance. Leaf content is resolved from
    /// the referenced corpus row and re-hashed: a leaf whose source has changed since the generation
    /// was built is dropped rather than injected under a stale hash.
    /// </summary>
    Task<IReadOnlyList<TapestryRetrievedNode>> HydrateRetrievedNodesAsync(
        TapestryGeneration generation,
        IReadOnlyList<(string NodeId, float Similarity)> hits,
        TapestryRetrievalMode mode,
        CancellationToken cancellationToken);

    /// <summary>The terminal (highest) layer index of a generation, for tree-traversal retrieval.</summary>
    Task<int> GetTerminalLayerAsync(string generationId, CancellationToken cancellationToken);

    /// <summary>Read-only counters for the memory-inspection surfaces.</summary>
    Task<IReadOnlyList<TapestryScopeStatus>> GetScopeStatusesAsync(
        Guid? sessionId,
        CancellationToken cancellationToken);

    /// <summary>Total published node count, optionally narrowed to one session's trees.</summary>
    Task<int> CountPublishedNodesAsync(Guid? sessionId, CancellationToken cancellationToken);

}
