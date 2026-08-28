using RetroDownfall.Arcanum.Core.Annals;

namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// Whether a memory is currently something retrieval can hand back to a turn.
/// </summary>
/// <remarks>
/// <see cref="Eligible"/> — retrievable now: not retired, its owning Session's binding resolved, and an
/// embedding still on file.
///
/// <para><see cref="Retired"/> — an operator retired this memory. That is a deliberate curation act
/// (<c>RetiredAtUtc</c> is set), reversible by reinstating it, and unrelated to whether the row or its
/// embedding is otherwise intact.</para>
///
/// <para><see cref="OwnershipUnresolved"/> — the owning Session's binding never resolved, mirroring
/// <see cref="SagaMemoryScopeKind.LegacyUnresolved"/>: the row supplies no scope authority, so it is
/// retrievable in no scope at all until an operator resolves the binding. That is a different thing
/// from <see cref="Retired"/> — nobody chose to withhold this memory, it never had standing to be
/// reached — and a different thing from <see cref="EmbeddingMissing"/>, which is a data defect rather
/// than a scope one.</para>
///
/// <para><see cref="EmbeddingMissing"/> — the row survives but <c>saga_memory_embeddings</c> no longer
/// has a matching entry, so no similarity search can surface it even though nothing about its scope or
/// curation state says it should be hidden.</para>
/// </remarks>
public enum SagaRetrievalEligibility
{

    /// <summary>Retrievable now.</summary>
    Eligible = 1,

    /// <summary>An operator retired this memory. Reversible by reinstating it.</summary>
    Retired = 2,

    /// <summary>
    /// The owning Session's binding never resolved. Retrievable in no scope at all until an operator
    /// resolves it — not retired, and not broken.
    /// </summary>
    OwnershipUnresolved = 3,

    /// <summary>The row survives but its embedding does not, so no similarity search can reach it.</summary>
    EmbeddingMissing = 4,

}

/// <summary>One memory's curation timestamps: when it was retired, and when it was pinned.</summary>
/// <remarks>
/// Both are independent and nullable on their own terms. A null <paramref name="RetiredAtUtc"/> means
/// the memory is active; a null <paramref name="PinnedAtUtc"/> means it carries no operator protection.
/// Neither implies anything about the other.
/// </remarks>
public sealed record SagaMemoryLifecycle(DateTimeOffset? RetiredAtUtc, DateTimeOffset? PinnedAtUtc);

/// <summary>One memory's row, its curation lifecycle, and whether it still has an embedding, read together.</summary>
public sealed record SagaMemoryCurationRow(SagaMemoryDto Memory, SagaMemoryLifecycle Lifecycle, bool HasEmbedding);

/// <summary>
/// The full detail view of one memory: its row, its lifecycle, its retrieval eligibility, and — when
/// the Annals is enabled — the claim that governs it and that claim's version history.
/// </summary>
public sealed record SagaMemoryDetail(
    SagaMemoryDto Memory,
    SagaMemoryLifecycle Lifecycle,
    SagaRetrievalEligibility Eligibility,
    AnnalClaimHead? Claim,
    IReadOnlyList<AnnalClaimVersion> History);

/// <summary>The result of attempting one curation verb (correct, retire, reinstate, pin) against one memory.</summary>
public enum SagaCurationOutcomeKind
{

    /// <summary>The verb changed the row as asked.</summary>
    Applied = 1,

    /// <summary>No memory with the given identity exists.</summary>
    NotFound = 2,

    /// <summary>The caller's view of the content is stale relative to what is stored now.</summary>
    StaleContent = 3,

    /// <summary>
    /// A verb was asked for against a memory that is already retired. Nothing was written.
    /// </summary>
    /// <remarks>
    /// Produced by <b>both</b> retirement and correction, which is why the service maps it per verb: a
    /// retire that meets it has given the operator the state they named, and a correction that meets it
    /// has not acted on the memory at all.
    /// </remarks>
    AlreadyRetired = 4,

    /// <summary>A reinstate was asked for against a memory that is not retired. Nothing was written.</summary>
    NotRetired = 5,

    /// <summary>The verb would not have changed anything, so nothing was written.</summary>
    Unchanged = 6,

}

/// <summary>One curation verb's outcome, and the lifecycle that resulted when it applied.</summary>
public sealed record SagaCurationOutcome(SagaCurationOutcomeKind Kind, SagaMemoryLifecycle? Lifecycle);

/// <summary>What one curation verb did, and the memory it left behind.</summary>
/// <remarks>
/// The four write verbs report an outcome beside the projection rather than returning the projection
/// alone, because some of their outcomes write nothing and are still not errors: asking to retire a
/// memory that is already retired, to reinstate one that is not retired, or to correct one to the text
/// it already holds all leave the operator with the state they asked for. Reporting only the projection
/// would make those indistinguishable from the call that did the work.
///
/// <para>Writing nothing is not by itself what makes an outcome a success — correcting a retired memory
/// also writes nothing, and is refused, because a retired memory is reinstated before it is corrected
/// rather than corrected in place. <c>SagaCurationService.MapOutcome</c> is where that is decided, per
/// verb.</para>
///
/// <para>The distinction is load-bearing in two directions. A caller retrying after a dropped
/// connection must not be told its first attempt's success was a failure, and a caller that later
/// reports how many memories it retired must be able to leave out the ones that were already retired —
/// which it can only do by reading <paramref name="Outcome"/> rather than by counting calls that
/// returned without an error.</para>
/// </remarks>
public sealed record SagaCurationResult(SagaCurationOutcomeKind Outcome, SagaMemoryDetail Detail);

/// <summary>Whether a write actually landed, or was refused by retirement suppression.</summary>
public enum SagaMemoryWriteOutcome
{

    /// <summary>The row was written.</summary>
    Written = 1,

    /// <summary>Retirement suppression refused the write; nothing was stored.</summary>
    Suppressed = 2,

}
