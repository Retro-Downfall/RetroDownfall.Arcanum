using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.DataLifecycle;

/// <summary>
/// Who performs the deletion for one artifact kind.
/// </summary>
/// <remarks>
/// Two arms, because exactly one kind lives outside SQLite. Modelling that as a boolean on the rule
/// would make "database" the default a new kind silently inherits; an explicit executor forces the
/// question to be answered when the rule is written.
/// </remarks>
public enum CovenantArtifactPurgeExecutor : byte
{

    /// <summary>One immediate SQLite transaction deletes content, projections, and the label.</summary>
    DatabaseTransaction = 1,

    /// <summary>Delegated once to the single managed-file erasure kernel.</summary>
    ManagedFileKernel = 2,

}

/// <summary>
/// The complete deletion policy for one <see cref="SensitiveArtifactKind"/>.
/// </summary>
/// <remarks>
/// Every field is a decision an erasure path would otherwise have to make for itself. The registry
/// exists so that decision is made once: a purge that deletes a summary without repairing the
/// Session's current pointer and a purge that deletes an assistant Entry without appending its
/// erasure receipt are the same class of defect, and both are invisible until an operator asks the
/// database a question it can no longer answer honestly (§10.17).
/// </remarks>
/// <param name="Kind">The artifact kind this rule is the sole policy for.</param>
/// <param name="Code">The persisted numeric code, pinned here so a renumbering cannot go unnoticed.</param>
/// <param name="Executor">Database transaction, or the one managed-file kernel.</param>
/// <param name="DeletesDerivedProjections">
/// Whether every derived projection of the artifact is deleted in the same transaction as the
/// artifact itself.
/// </param>
/// <param name="RepairsCurrentPointer">
/// Whether a Session-scoped "current" pointer and its value must be repaired after the delete.
/// </param>
/// <param name="RepairsSessionSensitivityState">
/// Whether <c>session_sensitivity_state</c> is recomputed in the same transaction.
/// </param>
/// <param name="AppendsErasureReceipt">
/// Whether an immutable erasure receipt replaces the deleted content, so a purged artifact is
/// distinguishable from one that never existed.
/// </param>
/// <param name="RequiresDurableTerminalEvidenceFirst">
/// Whether the delete is refused until terminal or erasure evidence is already durable.
/// </param>
/// <param name="PreservesFinalizationAndClaimEvidence">
/// Whether the finalization guard and turn claim survive the purge so a retry answers
/// <c>Covenant.ArtifactErased</c> instead of recreating the response.
/// </param>
/// <param name="PreservesReplayDenialEvidence">
/// Whether the typed replay-denial evidence survives, so a replayed request is refused rather than
/// re-served.
/// </param>
/// <param name="Policy">The operator-facing statement of the rule, in one sentence.</param>
public sealed record CovenantSensitiveArtifactPurgeRule(
    SensitiveArtifactKind Kind,
    byte Code,
    CovenantArtifactPurgeExecutor Executor,
    bool DeletesDerivedProjections,
    bool RepairsCurrentPointer,
    bool RepairsSessionSensitivityState,
    bool AppendsErasureReceipt,
    bool RequiresDurableTerminalEvidenceFirst,
    bool PreservesFinalizationAndClaimEvidence,
    bool PreservesReplayDenialEvidence,
    string Policy)
{

    /// <summary>
    /// Whether this kind's label is removed by the same database transaction that deletes its content.
    /// </summary>
    /// <remarks>
    /// Projected rather than stored. The managed-file kernel removes the label in its own completion
    /// transaction after the unlink and parent fsync are proven, and every other kind removes it
    /// beside the content, so a second stored field could only ever disagree with the executor.
    /// </remarks>
    public bool RemovesLabelWithContent => Executor == CovenantArtifactPurgeExecutor.DatabaseTransaction;

}

/// <summary>
/// The one exhaustive code-to-policy table for deleting a Covenant-derived artifact.
/// </summary>
/// <remarks>
/// Thirteen kinds, thirteen literal rules, one owner. Retention purge (#94), family reinitialize, and
/// Covenant reset all delete the same artifacts for different reasons, and three independent switch
/// statements over the same enum is how one of them ends up preserving evidence the other two remove.
/// Every erasure path resolves its policy here or does not run.
///
/// <para><c>external_disclosure_receipts</c>, the folded disclosure aggregates, and joined disclosure
/// state are deliberately outside this table and cannot be selected through it. A disclosure that
/// already happened is not an artifact: erasing the local copy of a byte a provider received does not
/// un-receive it, and a purge that could quietly drop the evidence would turn an honest
/// "nonrevocable" report into a clean one (§10.15).</para>
/// </remarks>
public static class CovenantSensitiveArtifactPurgePolicy
{

    private static readonly CovenantSensitiveArtifactPurgeRule[] Rules =
    [
        new(
            SensitiveArtifactKind.AssistantEntry,
            Code: 1,
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            DeletesDerivedProjections: true,
            RepairsCurrentPointer: false,
            RepairsSessionSensitivityState: true,
            AppendsErasureReceipt: true,
            RequiresDurableTerminalEvidenceFirst: false,
            PreservesFinalizationAndClaimEvidence: true,
            PreservesReplayDenialEvidence: true,
            Policy:
                "Delete dependent content and projections, the Entry, and its label in one transaction; "
                + "append the matching erasure receipt and preserve the finalization guard plus turn claim "
                + "so a retry replays as a typed 410."),

        new(
            SensitiveArtifactKind.TurnEvidence,
            Code: 2,
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            DeletesDerivedProjections: true,
            RepairsCurrentPointer: false,
            RepairsSessionSensitivityState: false,
            AppendsErasureReceipt: false,
            RequiresDurableTerminalEvidenceFirst: true,
            PreservesFinalizationAndClaimEvidence: true,
            PreservesReplayDenialEvidence: true,
            Policy:
                "Delete only content-bearing turn projections, and only after terminal guard or erasure "
                + "evidence is durable; preserve the terminal claim, finalization, disclosure, and "
                + "replay-denial evidence."),

        new(
            SensitiveArtifactKind.Summary,
            Code: 3,
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            DeletesDerivedProjections: true,
            RepairsCurrentPointer: true,
            RepairsSessionSensitivityState: true,
            AppendsErasureReceipt: false,
            RequiresDurableTerminalEvidenceFirst: false,
            PreservesFinalizationAndClaimEvidence: false,
            PreservesReplayDenialEvidence: false,
            Policy:
                "Delete the summary artifact, its dependent projections, the current pointer and value, "
                + "and its label in one transaction, then repair Session sensitivity state."),

        new(
            SensitiveArtifactKind.ToolArtifact,
            Code: 4,
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            DeletesDerivedProjections: true,
            RepairsCurrentPointer: false,
            RepairsSessionSensitivityState: true,
            AppendsErasureReceipt: false,
            RequiresDurableTerminalEvidenceFirst: false,
            PreservesFinalizationAndClaimEvidence: false,
            PreservesReplayDenialEvidence: false,
            Policy:
                "Delete the tool artifact, every derived projection, and its label in one transaction."),

        new(
            SensitiveArtifactKind.SessionTitle,
            Code: 5,
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            DeletesDerivedProjections: true,
            RepairsCurrentPointer: true,
            RepairsSessionSensitivityState: true,
            AppendsErasureReceipt: false,
            RequiresDurableTerminalEvidenceFirst: false,
            PreservesFinalizationAndClaimEvidence: false,
            PreservesReplayDenialEvidence: false,
            Policy:
                "Delete the title artifact, its dependent projections, the current pointer and value, and "
                + "its label in one transaction, then repair Session sensitivity state."),

        new(
            SensitiveArtifactKind.Saga,
            Code: 6,
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            DeletesDerivedProjections: true,
            RepairsCurrentPointer: false,
            RepairsSessionSensitivityState: false,
            AppendsErasureReceipt: false,
            RequiresDurableTerminalEvidenceFirst: false,
            PreservesFinalizationAndClaimEvidence: false,
            PreservesReplayDenialEvidence: false,
            Policy:
                "Delete the Saga row, its embedding and FTS projections, its provenance, and its label in "
                + "one transaction."),

        new(
            SensitiveArtifactKind.Lexicon,
            Code: 7,
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            DeletesDerivedProjections: true,
            RepairsCurrentPointer: false,
            RepairsSessionSensitivityState: false,
            AppendsErasureReceipt: false,
            RequiresDurableTerminalEvidenceFirst: false,
            PreservesFinalizationAndClaimEvidence: false,
            PreservesReplayDenialEvidence: false,
            Policy:
                "Delete the Lexicon row, its facts and FTS projections, its provenance, and its label in "
                + "one transaction."),

        new(
            SensitiveArtifactKind.Embedding,
            Code: 8,
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            DeletesDerivedProjections: true,
            RepairsCurrentPointer: false,
            RepairsSessionSensitivityState: false,
            AppendsErasureReceipt: false,
            RequiresDurableTerminalEvidenceFirst: false,
            PreservesFinalizationAndClaimEvidence: false,
            PreservesReplayDenialEvidence: false,
            Policy: "Delete the embedding, its vector projection, and its label in one transaction."),

        new(
            SensitiveArtifactKind.SearchProjection,
            Code: 9,
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            DeletesDerivedProjections: true,
            RepairsCurrentPointer: false,
            RepairsSessionSensitivityState: false,
            AppendsErasureReceipt: false,
            RequiresDurableTerminalEvidenceFirst: false,
            PreservesFinalizationAndClaimEvidence: false,
            PreservesReplayDenialEvidence: false,
            Policy:
                "Delete the protected search projection and its label in one transaction. Generic search "
                + "never contains this artifact, so there is no second index to repair."),

        new(
            SensitiveArtifactKind.AuditProjection,
            Code: 10,
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            DeletesDerivedProjections: true,
            RepairsCurrentPointer: false,
            RepairsSessionSensitivityState: false,
            AppendsErasureReceipt: false,
            RequiresDurableTerminalEvidenceFirst: false,
            PreservesFinalizationAndClaimEvidence: true,
            PreservesReplayDenialEvidence: false,
            Policy:
                "Delete the content-bearing audit projection and its label in one transaction while "
                + "retaining content-free disclosure and terminal audit evidence."),

        new(
            SensitiveArtifactKind.Notification,
            Code: 11,
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            DeletesDerivedProjections: true,
            RepairsCurrentPointer: false,
            RepairsSessionSensitivityState: false,
            AppendsErasureReceipt: false,
            RequiresDurableTerminalEvidenceFirst: false,
            PreservesFinalizationAndClaimEvidence: false,
            PreservesReplayDenialEvidence: false,
            Policy: "Delete the notification payload or projection and its label in one transaction."),

        new(
            SensitiveArtifactKind.ManagedWorkspaceFile,
            Code: 12,
            CovenantArtifactPurgeExecutor.ManagedFileKernel,
            DeletesDerivedProjections: false,
            RepairsCurrentPointer: false,
            RepairsSessionSensitivityState: false,
            AppendsErasureReceipt: false,
            RequiresDurableTerminalEvidenceFirst: false,
            PreservesFinalizationAndClaimEvidence: false,
            PreservesReplayDenialEvidence: false,
            Policy:
                "Delegate once to the managed-file erasure kernel, which exclusively owns durable "
                + "work-item persistence, no-follow open, same-handle verification, compare-delete, "
                + "parent fsync, absence, mismatch, and label completion."),

        new(
            SensitiveArtifactKind.IdempotencyClaim,
            Code: 13,
            CovenantArtifactPurgeExecutor.DatabaseTransaction,
            DeletesDerivedProjections: true,
            RepairsCurrentPointer: false,
            RepairsSessionSensitivityState: false,
            AppendsErasureReceipt: false,
            RequiresDurableTerminalEvidenceFirst: true,
            PreservesFinalizationAndClaimEvidence: true,
            PreservesReplayDenialEvidence: true,
            Policy:
                "Purge the cached protected response body and its label only after the claim is durably "
                + "non-replayable or bound to erasure evidence; preserve the claim identity and its typed "
                + "replay denial."),
    ];

    private static readonly Dictionary<SensitiveArtifactKind, CovenantSensitiveArtifactPurgeRule> ByKind =
        Rules.ToDictionary(static rule => rule.Kind);

    /// <summary>Every rule, in ascending code order.</summary>
    public static IReadOnlyList<CovenantSensitiveArtifactPurgeRule> All => Rules;

    /// <summary>
    /// The one rule for a kind, or a typed refusal for a value this build has no policy for.
    /// </summary>
    /// <remarks>
    /// A <see cref="Result{T}"/> rather than a nullable, because the only correct response to an
    /// unmapped kind is to stop: a purge that skipped an artifact it could not classify would report
    /// success while leaving Covenant-derived content in place.
    /// </remarks>
    public static Result<CovenantSensitiveArtifactPurgeRule> Resolve(SensitiveArtifactKind kind) =>
        ByKind.TryGetValue(kind, out CovenantSensitiveArtifactPurgeRule? rule)
            ? Result<CovenantSensitiveArtifactPurgeRule>.Success(rule)
            : Result<CovenantSensitiveArtifactPurgeRule>.Failure(
                new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "This build has no protected-artifact purge policy for the requested artifact kind."));

    /// <summary>Whether a value is one of the thirteen kinds this table covers.</summary>
    public static bool IsCovered(SensitiveArtifactKind kind) => ByKind.ContainsKey(kind);

}
