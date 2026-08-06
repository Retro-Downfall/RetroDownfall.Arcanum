namespace RetroDownfall.Arcanum.Core.Operations;

/// <summary>
/// When a kind's recovery must run relative to ordinary durable work at host startup.
/// </summary>
public enum LongRunningOperationStartupPriority
{
    /// <summary>
    /// Must reconcile before any other durable workload writes to the state root. Restore-class
    /// work owns whole directories, so a half-published archive has to be resolved before an
    /// ordinary operation can append to the tree it is about to replace or roll back.
    /// </summary>
    BeforeStateWrites = 0,

    /// <summary>Ordinary readiness-phase reconciliation.</summary>
    Readiness = 1,
}

/// <summary>
/// One row of the executable recovery matrix (#40): everything an operator or a recovery pass needs
/// to know about a durable operation kind without reading the handler's source.
/// </summary>
/// <param name="Kind">Bounded kind from <see cref="LongRunningOperationKinds"/>.</param>
/// <param name="Policy">Recovery class applied after a crash.</param>
/// <param name="Owner">Component that creates and completes operations of this kind.</param>
/// <param name="MinCheckpointVersion">
/// Oldest checkpoint payload the owning handler still understands. An operation below this is
/// rejected before handler code rather than misparsed.
/// </param>
/// <param name="MaxCheckpointVersion">Newest checkpoint payload the owning handler writes.</param>
/// <param name="StartupPriority">Ordering constraint relative to ordinary durable work.</param>
/// <param name="RecoveryIntent">What recovery actually does, in operator-facing terms.</param>
/// <param name="ManualRepairGuidance">
/// What an operator should do when this kind reaches
/// <see cref="LongRunningOperationState.ReconciliationRequired"/>.
/// </param>
public sealed record LongRunningOperationRecoveryDescriptor(
    string Kind,
    LongRunningOperationRecoveryPolicy Policy,
    string Owner,
    int MinCheckpointVersion,
    int MaxCheckpointVersion,
    LongRunningOperationStartupPriority StartupPriority,
    string RecoveryIntent,
    string ManualRepairGuidance);

/// <summary>
/// The code-owned recovery matrix. Every kind Arcanum can durably create appears here exactly once;
/// <see cref="LongRunningOperationPolicyCatalog"/> projects its policy column so the two cannot drift.
/// </summary>
public static class LongRunningOperationRecoveryRegistry
{
    private const string InspectGuidance =
        "Run 'arcanum operation show <id>' for the recorded phase, then 'arcanum operation retry <id>' "
        + "once the underlying cause is resolved.";

    private static readonly IReadOnlyDictionary<string, LongRunningOperationRecoveryDescriptor> Matrix =
        new[]
        {
            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.InferenceRun,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                Owner: "TurnEngine / WizardIntelligenceProvider",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 0,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent:
                    "Resolve the interrupted run, keep already-ledgered provider usage, release or reconcile "
                    + "its reservation, and make a claim without fully captured terminal bytes non-replayable. "
                    + "A live stream is never replayed.",
                ManualRepairGuidance:
                    "Compare 'arcanum operation show <id>' against the provider's own usage dashboard before "
                    + "retrying; Arcanum cannot prove what a crashed provider call was billed for."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.Subagent,
                LongRunningOperationRecoveryPolicy.AbandonSafely,
                Owner: "SubagentRunner",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 0,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent:
                    "A crashed child has no resumable conversational context. Abandon it, settle its own run "
                    + "and reservation once, and never restart it — restarting is how a recursion storm begins.",
                ManualRepairGuidance:
                    "Re-issue the parent request. The child is not resumable and was not re-billed."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.BudgetReservation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                Owner: "BudgetReservationService",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 0,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent:
                    "Idempotently release a stranded reservation when actual cost cannot be established, so "
                    + "the daily limit is not permanently consumed by a dead process.",
                ManualRepairGuidance:
                    "Check 'arcanum budget status' for outstanding reservations; releasing is safe and idempotent."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.Batch,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                Owner: "BatchRecoveryService",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 0,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent:
                    "Seal dispatched-but-unrecorded lines, clear partial output artifacts, and re-queue from "
                    + "durable line checkpoints. Completed lines are never dispatched twice.",
                ManualRepairGuidance:
                    "Use 'POST /v1/batches/{id}/reset' when the input file is still present; a batch whose input "
                    + "is gone is failed deliberately and must be resubmitted."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.Apprentice,
                LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
                Owner: "ApprenticeService / ConclaveArchmage",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 1,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent:
                    "Hand the Apprentice back to its own documented checkpoint resume path, preserving step "
                    + "position, completed tool-call ids, and parent/child lineage.",
                ManualRepairGuidance:
                    "Inspect 'arcanum apprentice show <id>'; cancel it with 'arcanum apprentice cancel <id>' if "
                    + "its checkpoint is no longer meaningful."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.AttachmentPromotion,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                Owner: "SessionAttachmentStore",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 0,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent:
                    "Verify content hashes, drop incomplete temp files and half-written versions, and keep every "
                    + "valid prior version. A promotion is either fully applied or fully absent.",
                ManualRepairGuidance:
                    "Re-upload the attachment. Prior versions are intact; no duplicate version was created."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.WorkspaceIndex,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                Owner: "WorkspaceIndexService",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 0,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent:
                    "Re-enumerate deterministically from durable file state. Already-indexed rows remain the "
                    + "authority, so a restart costs work but never correctness.",
                ManualRepairGuidance:
                    "Re-run 'arcanum workspace index'; indexing is idempotent by file identity and content hash."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.IdempotencyClaim,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                Owner: "IdempotencyClaimStore",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 0,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent:
                    "Settle the linked claim so it cannot stay stranded. A claim whose terminal bytes were fully "
                    + "captured stays replayable; anything else is marked abandoned and must be re-sent.",
                ManualRepairGuidance:
                    "Re-send the original request with the same Idempotency-Key; an abandoned claim is safe to retry."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.BlobEncryptionMigration,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                Owner: "BlobEncryptionLifecycleService",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 1,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent:
                    "Re-scan blob metadata and reconcile plaintext, envelope, and replace-before-metadata states.",
                ManualRepairGuidance:
                    "Run 'arcanum key status' and re-run the migration; per-file state makes it resumable."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.BlobEncryptionKeyRotation,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                Owner: "BlobEncryptionLifecycleService",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 1,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent:
                    "Continue toward the active write key while retaining every still-referenced prior key.",
                ManualRepairGuidance:
                    "Run 'arcanum key status'; never delete a prior key until rotation reports no references to it."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.BackupCreate,
                LongRunningOperationRecoveryPolicy.AbandonSafely,
                Owner: "BackupService",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 2,
                LongRunningOperationStartupPriority.BeforeStateWrites,
                RecoveryIntent:
                    "Remove or quarantine an incomplete archive and preserve phase metadata for inspection. The "
                    + "recovery passphrase is deliberately not persisted, so creation is never resumed.",
                ManualRepairGuidance:
                    "Re-run 'arcanum backup create'. A fully self-verified published archive stays valid; anything "
                    + "else was removed rather than left to look complete."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.DataRetentionPrune,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                Owner: "DataRetentionService",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 2,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent: "Restart idempotently from the durable prune cursor; pruned rows are already gone.",
                ManualRepairGuidance: "Re-run 'arcanum retention prune'; the cursor makes repetition safe."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                Owner: "DataRetentionService",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 2,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent: "Reconcile a partially applied retention mutation against its durable journal.",
                ManualRepairGuidance: "Inspect 'arcanum retention status' before re-applying the policy change."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.DataRetentionFactoryReset,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                Owner: "DataRetentionService",
                MinCheckpointVersion: 0,
                MaxCheckpointVersion: 0,
                LongRunningOperationStartupPriority.BeforeStateWrites,
                RecoveryIntent:
                    "Restart the reset from its durable quarantine state so a half-erased state root is never "
                    + "presented as a working installation.",
                ManualRepairGuidance:
                    "Re-run the factory reset; quarantined trees are listed by 'arcanum doctor' until removed."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.A2AInboundSending,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                Owner: "A2ASendingLedger",
                MinCheckpointVersion: 1,
                MaxCheckpointVersion: 1,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent:
                    "The peer's connection died with the process, so the relay is explicitly abandoned with a named "
                    + "reason. The serving Apprentice is durable and untouched.",
                ManualRepairGuidance:
                    "Ask the peer to re-send. 'arcanum apprentice show <id>' still reports the local work."),

            new LongRunningOperationRecoveryDescriptor(
                LongRunningOperationKinds.A2AOutboundSending,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                Owner: "A2ASendingLedger",
                MinCheckpointVersion: 1,
                MaxCheckpointVersion: 1,
                LongRunningOperationStartupPriority.Readiness,
                RecoveryIntent:
                    "Cancel the orphaned remote task so it stops running and billing on the peer, and record whether "
                    + "the cancel was confirmed or the peer was unreachable.",
                ManualRepairGuidance:
                    "An 'a2a.outbound_remote_abandoned' terminal code means the remote task may still be running; "
                    + "check the peer directly."),
        }.ToDictionary(static descriptor => descriptor.Kind, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, LongRunningOperationRecoveryDescriptor> Descriptors => Matrix;

    public static bool Contains(string kind) => Matrix.ContainsKey(kind);

    public static LongRunningOperationRecoveryDescriptor? Find(string kind) =>
        Matrix.TryGetValue(kind, out LongRunningOperationRecoveryDescriptor? descriptor) ? descriptor : null;

    /// <summary>
    /// Kinds ordered the way a startup pass must apply them: restore-class work first, then ordinary
    /// reconciliation, each group in stable declaration order.
    /// </summary>
    public static IReadOnlyList<string> KindsByStartupPriority { get; } =
    [
        .. Matrix.Values
            .OrderBy(static descriptor => (int)descriptor.StartupPriority)
            .Select(static descriptor => descriptor.Kind),
    ];
}
