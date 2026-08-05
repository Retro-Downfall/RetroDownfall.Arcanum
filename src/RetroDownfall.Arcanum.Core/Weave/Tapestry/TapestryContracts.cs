using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry;

/// <summary>
/// Which corpus a Tapestry tree covers. Each scope is clustered and published independently, so a
/// failure or rebuild in one never affects another (DESIGN §21.11).
/// </summary>
public enum TapestryScopeKind
{

    /// <summary>One registered workspace's indexed code chunks (<c>workspace_file_chunks</c>).</summary>
    Workspace,

    /// <summary>One session's indexed attachment chunks (<c>session_attachment_chunks</c>).</summary>
    SessionAttachment,

    /// <summary>One session's Grimoire <c>Entries</c>.</summary>
    Session,

}

/// <summary>Leaf nodes mirror an existing corpus row; summary nodes are abstractive and model-authored.</summary>
public enum TapestryNodeKind
{

    Leaf,

    Summary,

}

/// <summary>The durable row a leaf node references by stable id rather than copying.</summary>
public enum TapestryLeafSourceKind
{

    WorkspaceFileChunk,

    SessionAttachmentChunk,

    Entry,

}

/// <summary>
/// Generation lifecycle. Retrieval only ever reads <see cref="Complete"/>; a
/// <see cref="Building"/> generation is invisible until the atomic publish, and
/// <see cref="Superseded"/> rows exist only until reconciliation removes them.
/// </summary>
public enum TapestryGenerationStatus
{

    Building,

    Complete,

    Superseded,

}

/// <summary>Why recursion stopped. Persisted so a multi-root tree is never misreported as rooted.</summary>
public enum TapestryTerminalReason
{

    /// <summary>The whole remaining layer fit one summary request, so a single root was written.</summary>
    SingleRoot,

    /// <summary>The configured maximum depth was reached; the terminal layer has several roots.</summary>
    MaxDepth,

    /// <summary>The corpus was small enough that the leaf layer is also the terminal layer.</summary>
    LeafOnly,

}

/// <summary>
/// Why a cluster's membership differs from raw K-Means output. Recorded per summary node so an
/// operator can tell a semantic grouping from a deterministic repair.
/// </summary>
public enum TapestryPartitionReason
{

    /// <summary>Raw K-Means membership, unmodified.</summary>
    None,

    /// <summary>The cluster exceeded the child-count or model-token bound and was split again.</summary>
    OversizedSplit,

    /// <summary>Identical vectors made semantic splitting impossible; members were partitioned by stable id.</summary>
    IdenticalVectorPartition,

    /// <summary>An undersized cluster was merged into its most similar sibling.</summary>
    UndersizedMerge,

    /// <summary>A single-child cluster was carried directly to the next layer without a new summary.</summary>
    SingletonCarry,

}

/// <summary>Which retrieval strategy produced a node (DESIGN §21.11).</summary>
public enum TapestryRetrievalMode
{

    /// <summary>Leaf and summary nodes are searched as one flat pool. The default.</summary>
    CollapsedTree,

    /// <summary>Descends level by level from the terminal layer, expanding only the selected nodes' children.</summary>
    TreeTraversal,

}

/// <summary>One tree's corpus identity.</summary>
public readonly record struct TapestryScope(TapestryScopeKind Kind, string Id);

/// <summary>
/// One immutable build of one scope's tree. Algorithm/settings/model provenance is stored with the
/// generation so changing any of them forces an explicit rebuild rather than silently mixing shapes.
/// </summary>
public sealed record TapestryGeneration(
    string GenerationId,
    TapestryScopeKind ScopeKind,
    string ScopeId,
    TapestryGenerationStatus Status,
    string AlgorithmVersion,
    string SettingsFingerprint,
    string? SummaryModel,
    string SummaryRecipeVersion,
    int EmbeddingDimension,
    string CorpusFingerprint,
    int LayerCount,
    int NodeCount,
    int RootNodeCount,
    TapestryTerminalReason? TerminalReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>One node of a Tapestry tree. Leaves carry no content of their own — they reference a corpus row.</summary>
public sealed record TapestryNode(
    string NodeId,
    string GenerationId,
    TapestryScopeKind ScopeKind,
    string ScopeId,
    int Layer,
    TapestryNodeKind NodeKind,
    string? ParentNodeId,
    TapestryLeafSourceKind? SourceKind,
    string? SourceId,
    string SourceLabel,
    string? Content,
    string ContentHash,
    string? ChildMembershipHash,
    int DescendantLeafCount,
    int ClusterOrdinal,
    TapestryPartitionReason PartitionReason,
    int EmbeddingDimension,
    DateTimeOffset CreatedAt);

/// <summary>A node plus the embedding persisted alongside it.</summary>
public sealed record TapestryNodeWrite(TapestryNode Node, float[] Embedding);

/// <summary>
/// One enumerated corpus row eligible to become a leaf. <see cref="ExistingEmbedding"/> is the
/// source feature's already-imprinted vector when one exists at the configured dimension, so a
/// rebuild re-embeds nothing it does not have to.
/// </summary>
public sealed record TapestryLeafSource(
    string SourceId,
    string Label,
    string Content,
    string ContentHash,
    float[]? ExistingEmbedding);

/// <summary>A prior generation's summary node offered for reuse when its identity matches exactly.</summary>
public sealed record TapestrySummaryReuseCandidate(
    string ChildMembershipHash,
    string Content,
    string ContentHash,
    float[] Embedding);

/// <summary>
/// The Tapestry's durable table and column identifiers. Retrieval passes these to
/// <c>IDivinationService</c>, and the store writes them, so they live in one place rather than as
/// matching magic strings on both sides of the layer boundary.
/// </summary>
public static class TapestryStorageKeys
{

    /// <summary>The vec0 acceleration table. Divination strips the suffix for the managed fallback.</summary>
    public const string VectorTable = "tapestry_node_embeddings_vec";

    /// <summary>The node metadata table joined for scoped searches.</summary>
    public const string NodeTable = "tapestry_nodes";

    public const string KeyColumn = "NodeId";

    public const string EmbeddingColumn = "Embedding";

    /// <summary>Scopes a collapsed-tree search to exactly one generation.</summary>
    public const string GenerationColumn = "GenerationId";

    /// <summary>
    /// Scopes a tree-traversal step. <c>"{generationId}#root"</c> selects a generation's roots;
    /// <c>"{generationId}#{parentNodeId}"</c> selects one node's children.
    /// </summary>
    public const string ParentScopeColumn = "ParentScopeKey";

    /// <summary>The parent placeholder used by a node that no summary has claimed.</summary>
    public const string RootParentMarker = "root";

    public static string ParentScopeKey(string generationId, string? parentNodeId) =>
        $"{generationId}#{(string.IsNullOrEmpty(parentNodeId) ? RootParentMarker : parentNodeId)}";

}

/// <summary>Why one scope's weave attempt ended.</summary>
public enum TapestryWeaveStatus
{

    /// <summary>The current generation already matches the corpus, settings, algorithm, recipe, and model.</summary>
    UpToDate,

    /// <summary>A new generation was built and published.</summary>
    Woven,

    /// <summary>The corpus has no indexable rows; the scope contributes no tree.</summary>
    NoCorpus,

    /// <summary>The embedding provider was unavailable; the prior complete generation stays current.</summary>
    EmbeddingUnavailable,

    /// <summary>No summary model could be resolved, so no tree above the leaf layer is possible.</summary>
    SummaryModelUnavailable,

    /// <summary>The build failed; the staging generation was abandoned and the prior one stays current.</summary>
    Failed,

}

/// <summary>The result of one scope's weave attempt, including what the build actually spent.</summary>
public sealed record TapestryWeaveOutcome(
    TapestryWeaveStatus Status,
    string? GenerationId = null,
    int LayerCount = 0,
    int NodeCount = 0,
    int RootNodeCount = 0,
    TapestryTerminalReason? TerminalReason = null,
    int SummaryCallsMade = 0,
    int SummariesReused = 0,
    int LeavesEmbedded = 0,
    int QuarantinedVectors = 0);

/// <summary>Read-only Tapestry counters for the memory-inspection surfaces.</summary>
public sealed record TapestryScopeStatus(
    TapestryScopeKind ScopeKind,
    string ScopeId,
    int LayerCount,
    int NodeCount,
    int LeafCount,
    int SummaryCount,
    int RootNodeCount,
    TapestryTerminalReason? TerminalReason,
    string AlgorithmVersion,
    DateTimeOffset? LastBuildCompletedAt);

/// <summary>
/// One retrieved Tapestry node, ready for lineage-aware selection and prompt injection.
/// <see cref="AncestorNodeIds"/> is the node's complete ancestor chain within its generation, which
/// is what makes ancestor/descendant overlap detectable when content hashes necessarily differ.
/// </summary>
public sealed record TapestryRetrievedNode(
    string NodeId,
    string GenerationId,
    TapestryScopeKind ScopeKind,
    string ScopeId,
    int Layer,
    TapestryNodeKind NodeKind,
    string SourceLabel,
    string Content,
    string ContentHash,
    int DescendantLeafCount,
    float Similarity,
    TapestryRetrievalMode Mode,
    IReadOnlyList<string> AncestorNodeIds);

/// <summary>
/// The projection <c>SystemPromptBuilder</c> renders under
/// <c>### Hierarchical Context (The Tapestry)</c>. Deliberately slimmer than
/// <see cref="TapestryRetrievedNode"/>: the prompt never needs generation ids or lineage chains.
/// </summary>
public sealed record TapestryContextNode(
    string ScopeLabel,
    string SourceLabel,
    int Layer,
    bool IsSummary,
    int DescendantLeafCount,
    string ContentHash,
    float Similarity,
    string Content);

/// <summary>
/// Content hashing and provenance fingerprints for the Tapestry. All of these are stable across
/// processes and runs (unlike <see cref="string.GetHashCode()"/>), which is what makes summary reuse
/// and generation invalidation correct rather than best-effort.
/// </summary>
public static class TapestryHash
{

    /// <summary>
    /// Bumped whenever the summarization prompt or its output contract changes. Part of a summary
    /// node's reuse identity: an older recipe's prose is never silently carried into a new shape.
    /// </summary>
    public const string SummaryRecipeVersion = "tapestry-summary-v1";

    public static string OfContent(string? content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)));

    /// <summary>
    /// Hashes an ordered sequence of parts with an unambiguous separator, so
    /// <c>["ab", "c"]</c> and <c>["a", "bc"]</c> can never collide.
    /// </summary>
    public static string OfParts(IEnumerable<string> parts)
    {

        ArgumentNullException.ThrowIfNull(parts);

        StringBuilder builder = new();

        foreach (string part in parts)
        {

            builder.Append(part).Append('\u001f');

        }

        return OfContent(builder.ToString());

    }

    /// <summary>
    /// A summary node's reuse identity: the exact sorted child-membership plus the recipe, model, and
    /// input hashes. Reuse requires all of them to match — deterministic clustering does not make
    /// model prose reproducible, so identity is the only safe basis for skipping a summary call.
    /// </summary>
    public static string OfChildMembership(
        IEnumerable<string> childContentHashes,
        string summaryRecipeVersion,
        string? summaryModel)
    {

        ArgumentNullException.ThrowIfNull(childContentHashes);

        List<string> sorted = [.. childContentHashes];

        sorted.Sort(StringComparer.Ordinal);

        return OfParts(
        [
            summaryRecipeVersion,
            summaryModel ?? string.Empty,
            .. sorted,
        ]);

    }

    /// <summary>
    /// A corpus fingerprint over the ordered leaf identities. Any add, remove, or edit changes it,
    /// which is what triggers a whole-scope re-cluster — K-Means centroids are relative to the
    /// complete layer, so there is no honest subtree-only invalidation.
    /// </summary>
    public static string OfCorpus(IEnumerable<TapestryLeafSource> leaves)
    {

        ArgumentNullException.ThrowIfNull(leaves);

        List<string> identities = [.. leaves.Select(static leaf => $"{leaf.SourceId}\u001f{leaf.ContentHash}")];

        identities.Sort(StringComparer.Ordinal);

        return OfParts(identities);

    }

    /// <summary>
    /// The tree-shaping settings a generation was built under. A change here invalidates the
    /// generation exactly like an algorithm-version change.
    /// </summary>
    public static string OfSettings(
        int maxTreeDepth,
        int targetChildrenPerSummary,
        int maxChildrenPerSummary,
        int maxClustersPerLayer,
        int maxSummaryTokens,
        int dimensions) =>
        OfParts(
        [
            maxTreeDepth.ToString(CultureInfo.InvariantCulture),
            targetChildrenPerSummary.ToString(CultureInfo.InvariantCulture),
            maxChildrenPerSummary.ToString(CultureInfo.InvariantCulture),
            maxClustersPerLayer.ToString(CultureInfo.InvariantCulture),
            maxSummaryTokens.ToString(CultureInfo.InvariantCulture),
            dimensions.ToString(CultureInfo.InvariantCulture),
        ]);

}
