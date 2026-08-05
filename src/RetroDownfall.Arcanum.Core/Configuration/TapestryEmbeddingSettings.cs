using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// The Tapestry — hierarchical (RAPTOR-style) memory woven over The Weave. Only relevant when
/// <c>Arcanum:Features:Tapestry</c> is enabled; enabling it also derives
/// <see cref="EmbeddingSettings.Enabled"/>.
///
/// <para>Everything here except <see cref="RetrievalMode"/> and <see cref="SummaryModel"/> is a
/// <b>code-owned mechanic</b>, following §21.3: tree-shaping bounds, rebuild cadence, and retrieval
/// slice sizes protect one clustering computation, one provider request, or one prompt injection —
/// they are not public user restrictions and the generation builder keeps working through
/// checkpoints until a tree is complete or cancelled. The clustering algorithm id, seed derivation,
/// tie rules, convergence policy, and hard one-parent membership are versioned behavior in
/// <see cref="SphericalKMeans"/>, not knobs at all.</para>
/// </summary>
public sealed record TapestryEmbeddingSettings
{

    /// <summary>
    /// Maximum number of summary layers above the leaves. Reaching it terminates the tree with an
    /// explicit multi-root terminal layer rather than pretending a single root exists. Default
    /// <c>5</c>; clamped 1–16 at runtime.
    /// </summary>
    public int MaxTreeDepth { get; set; } = 5;

    /// <summary>
    /// Target children per summary node, from which each layer's <c>k</c> is derived deterministically
    /// as <c>ceil(n / target)</c>. Default <c>8</c>; clamped 2–64 at runtime.
    /// </summary>
    public int TargetChildrenPerSummary { get; set; } = 8;

    /// <summary>
    /// Hard child-count bound for one summary node. K-Means does not guarantee balanced clusters, so
    /// an oversized cluster is deterministically repartitioned — never truncated. Default <c>24</c>;
    /// clamped 2–256 at runtime.
    /// </summary>
    public int MaxChildrenPerSummary { get; set; } = 24;

    /// <summary>
    /// Upper bound on <c>k</c> for one layer, bounding one clustering computation's centroid
    /// allocation. Default <c>256</c>; clamped 2–4,096 at runtime.
    /// </summary>
    public int MaxClustersPerLayer { get; set; } = 256;

    /// <summary>
    /// Output tokens reserved for one cluster summary. Default <c>512</c>; clamped 64–8,192 at
    /// runtime.
    /// </summary>
    public int MaxSummaryTokens { get; set; } = 512;

    /// <summary>
    /// Interval between reconciliation sweeps that detect corpus changes and rebuild affected tree
    /// scopes. Default <c>60</c> minutes; clamped 1–1,440 at runtime.
    /// </summary>
    public int RebuildIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Maximum Tapestry nodes injected into one turn's system prompt. Default <c>5</c>; clamped 1–50
    /// at runtime.
    /// </summary>
    public int MaxRetrievedNodes { get; set; } = 5;

    /// <summary>
    /// Maximum materialized bytes across one turn's Tapestry context. Default <c>256 KiB</c>; clamped
    /// 1 KiB–16 MiB at runtime.
    /// </summary>
    public int MaxRetrievedBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Maximum estimated tokens across one turn's Tapestry context. Default <c>32,768</c>; clamped
    /// 128–1,048,576 at runtime.
    /// </summary>
    public int MaxRetrievedTokens { get; set; } = 32 * 1024;

    /// <summary>
    /// Operator policy — how retrieval reads the tree. <see cref="TapestryRetrievalMode.CollapsedTree"/>
    /// (default) searches leaf and summary nodes as one pool;
    /// <see cref="TapestryRetrievalMode.TreeTraversal"/> descends level by level from the terminal
    /// layer. Projected from <c>Arcanum:Integrations:Embeddings:Tapestry:RetrievalMode</c>.
    /// </summary>
    public TapestryRetrievalMode RetrievalMode { get; set; } = TapestryRetrievalMode.CollapsedTree;

    /// <summary>
    /// Operator policy — the model used for cluster summaries. When null/empty, falls back to
    /// <see cref="ArcanumSettings.FastModel"/>, then <see cref="ArcanumSettings.DefaultModel"/>.
    /// Projected from <c>Arcanum:Integrations:Embeddings:Tapestry:SummaryModel</c>. Summary spend is
    /// accounted, reserved, and audited exactly like any other provider call (§22.2).
    /// </summary>
    public string? SummaryModel { get; set; }

    /// <summary>
    /// Code-owned corpus participation. All three default to <c>true</c>: a corpus with no indexed
    /// rows simply contributes no tree, so these are build seams rather than user restrictions.
    /// </summary>
    public bool WorkspaceTreesEnabled { get; set; } = true;

    /// <inheritdoc cref="WorkspaceTreesEnabled"/>
    public bool SessionAttachmentTreesEnabled { get; set; } = true;

    /// <inheritdoc cref="WorkspaceTreesEnabled"/>
    public bool SessionTreesEnabled { get; set; } = true;

}
