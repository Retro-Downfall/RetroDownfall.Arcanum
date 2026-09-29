namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>
/// The plan flags an erasure's effect digest binds.
/// </summary>
/// <remarks>
/// Each store accepts only its own: Saga <see cref="Pinned"/>; Lexicon <see cref="Pinned"/> and
/// <see cref="GlobalEntryResurfaces"/>; Covenant <see cref="Pinned"/>, <see cref="ReclaimsKey"/>,
/// <see cref="RetainsCampaignMask"/>, and <see cref="GlobalConfirmedResurfaces"/>. The values are
/// written into a keyed preimage, so they are append-only.
/// </remarks>
[Flags]
public enum MemoryErasureEffectFlags
{
    None = 0,

    Pinned = 1,

    GlobalEntryResurfaces = 2,

    ReclaimsKey = 4,

    RetainsCampaignMask = 8,

    GlobalConfirmedResurfaces = 16,
}

/// <summary>One plan target: a registered table and how many of its rows the erase removes.</summary>
public sealed record MemoryErasureTableCount(string Table, long Rows);

/// <summary>The Covenant facts a Covenant entry erasure's effect binds, and no other store's.</summary>
public sealed record CovenantErasureEffectFacts(Guid DatasetGeneration, long KeyEpoch, long KeyReclamationEpoch);

/// <summary>
/// Everything one erasure plan would do, in the content-free form its effect digest binds.
/// </summary>
/// <param name="RowIds">The erased rows' ids in any GUID spelling; the digest canonicalizes and sorts them.</param>
/// <param name="Versions">One optional version per row, aligned with <paramref name="RowIds"/>.</param>
/// <param name="Targets">Every plan target, each table at most once, in plan order.</param>
/// <param name="Evidence">
/// Exactly five external-exposure evidence values, in <see cref="MemoryExternalChannel"/> order:
/// inference-provider authorship, inference-provider context, embedding provider, encrypted backup,
/// and other external.
/// </param>
/// <param name="RetainedCopiesMask">The <see cref="MemoryRetainedLocalCopies"/> mask of retained copies.</param>
public sealed record MemoryErasureEffectFacts(
    MemoryReviewStore Store,
    IReadOnlyList<string> RowIds,
    IReadOnlyList<Guid?> Versions,
    IReadOnlyList<MemoryErasureTableCount> Targets,
    int Labels,
    int RetirementSuppressions,
    MemoryErasureEffectFlags Flags,
    CovenantErasureEffectFacts? Covenant,
    IReadOnlyList<MemoryExternalEvidence> Evidence,
    int RetainedCopiesMask);

/// <summary>
/// The append-only registry that gives every erasure plan target table a one-byte code.
/// </summary>
/// <remarks>
/// A table's code is its position in <see cref="Tables"/> plus one. Codes are written into keyed effect
/// digests that receipts store durably, so an entry is never removed, renamed, or reordered; a new
/// target table is appended.
/// </remarks>
public static class MemoryErasureTableCodes
{
    public static IReadOnlyList<string> Tables { get; } =
    [
        "saga_memories",
        "saga_memory_embeddings",
        "saga_memory_embeddings_vec",
        "saga_memory_attachment_provenance",
        "saga_retirement_suppressions",
        "lexicon_entries",
        "lexicon_fact_attachment_provenance",
        "lexicon_annal_fact_provenance",
        "annal_claims",
        "annal_versions",
        "annal_heads",
        "annal_dependencies",
        "annal_review_events",
        "annal_review_decision_receipts",
        "artifact_sensitivity",
        "covenant_entries",
        "covenant_versions",
        "covenant_heads",
        "covenant_version_attachment_provenance",
        "covenant_mutation_receipts",
        "covenant_search_outbox",
        "covenant_search_documents",
        "covenant_curation_heads",
        "covenant_curation_versions",
        "covenant_curation_receipts",
        "covenant_key_epochs",
        "covenant_review_events",
        "covenant_review_decision_receipts",
    ];

    /// <summary>The code of one registered table.</summary>
    /// <exception cref="ArgumentException">The table is not registered.</exception>
    public static byte For(string table)
    {
        ArgumentNullException.ThrowIfNull(table);

        for (int index = 0; index < Tables.Count; index++)
        {
            if (string.Equals(Tables[index], table, StringComparison.Ordinal))
            {
                return checked((byte)(index + 1));
            }
        }

        throw new ArgumentException("This table has no erasure table code.", nameof(table));
    }
}
