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
    /// What one scope's corpus is, answered from the content hashes the store keeps beside each leaf
    /// rather than from the leaves' text: how many there are and the fingerprint of their ids and hashes.
    /// This is the question every sweep tick asks of every scope, and on a scope nobody has touched it
    /// reads no chunk text at all.
    /// </summary>
    /// <remarks>
    /// A leaf's hash is stored the first time anything asks for it and dropped the moment the leaf's row
    /// is inserted, edited or deleted, so a stored hash always describes the text it sits beside; a scope
    /// whose hashes are not stored yet (the first sweep after an upgrade, or new rows) has just those
    /// leaves read, hashed and stored, a page at a time. A scope holding more than
    /// <paramref name="maxLeaves"/> rows is reported as such without reading or hashing any of them. The
    /// fingerprint is <c>TapestryHash.OfCorpus</c> over the leaf ids and their hashes.
    /// </remarks>
    Task<TapestryCorpusIdentity> GetCorpusIdentityAsync(
        TapestryScope scope,
        int maxLeaves,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams one scope's leaf sources in stable id order, a page at a time, carrying each source
    /// feature's already-imprinted embedding when one exists at <paramref name="expectedDimensions"/> so a
    /// rebuild re-embeds only what it must.
    /// </summary>
    /// <remarks>
    /// The scope's ids are listed first and each page then reads only its own leaves' text and vectors, so
    /// no read holds the whole corpus and a caller that stops early never loads the rest. A leaf whose row
    /// disappears between the listing and its page is simply absent; one inserted after the listing waits
    /// for the next sweep. A leaf's hash is computed from the text this read returned, which is what the
    /// generation then records as its corpus.
    /// </remarks>
    IAsyncEnumerable<IReadOnlyList<TapestryLeafSource>> EnumerateLeafPagesAsync(
        TapestryScope scope,
        int expectedDimensions,
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

    /// <summary>
    /// Removes the published generation of every scope that no longer exists, and returns how many
    /// were removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ReconcileGenerationsAsync"/> deliberately preserves each scope's current complete
    /// generation, so a scope that disappears entirely — a deleted session, a workspace that is no
    /// longer indexed — keeps its whole tree forever. Nothing reads it again:
    /// <see cref="DiscoverScopesAsync"/> never yields that scope, so it is never rebuilt or replaced,
    /// and retrieval scopes every query to a live scope. It is unreachable storage that also inflates
    /// the counts reported by the memory-status surfaces.
    /// </para>
    /// <para>
    /// The corpus flags carry the same meaning as on <see cref="DiscoverScopesAsync"/> and are a
    /// safety boundary, not a filter: a corpus that is switched off is left completely untouched.
    /// Treating "the operator disabled session trees" as "every session scope is gone" would delete
    /// derived data that is expensive to rebuild — every summary is a billed model call — the moment
    /// a feature flag flips.
    /// </para>
    /// </remarks>
    Task<int> PruneRemovedScopesAsync(
        bool includeWorkspace,
        bool includeSessionAttachments,
        bool includeSessions,
        CancellationToken cancellationToken);

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
