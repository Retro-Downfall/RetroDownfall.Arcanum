namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Where a managed workspace file write has got to, and therefore what a restart is allowed to do
/// next.
/// </summary>
/// <remarks>
/// The codes describe filesystem effects that SQLite cannot roll back, so each one means "this
/// syscall completed and the compare-and-swap that records it committed". Normal creation advances
/// one code at a time through <c>ParentFsynced</c>. <c>Cleaned</c> and <c>ManualNonrevocable</c> are
/// the two terminal outcomes recovery reaches when it can, or cannot, authenticate what it found on
/// disk; <c>Erased</c> is reached only by a matching local-erasure completion. The literals are
/// persisted in <c>managed_file_write_intents.PhaseCode</c> and pinned here so that a renumbering
/// cannot silently reinterpret rows written by an earlier build.
/// </remarks>
internal enum ManagedFileWriteIntentPhase : byte
{

    Prepared = 1,

    TempCreated = 2,

    TempWritten = 3,

    TempFsynced = 4,

    RenamedNoReplace = 5,

    ParentFsynced = 6,

    AdoptedAndLabeled = 7,

    Cleaned = 8,

    ManualNonrevocable = 9,

    Erased = 10,

}

/// <summary>
/// How far the deletion of one Arcanum-owned file outside SQLite has got.
/// </summary>
/// <remarks>
/// <c>DeletionVerified</c> means the file is provably gone and the parent directory was flushed;
/// only then may the producing write intent be terminalized and its label removed.
/// <c>ManualBlocker</c> means the deleter refused to act because ownership, identity, or content did
/// not match, and it deliberately leaves the file, the write intent, and the label untouched. The
/// literals are persisted in <c>local_erasure_work_items.StateCode</c>.
/// </remarks>
internal enum LocalErasureWorkItemState : byte
{

    Prepared = 1,

    DeletionVerified = 2,

    Completed = 3,

    ManualBlocker = 4,

}

/// <summary>
/// Which of the two acceptable proofs of absence a verified local erasure recorded.
/// </summary>
/// <remarks>
/// Both codes mean the file is gone, but they are kept distinct because they answer different
/// questions after a crash: <c>AlreadyAbsent</c> is what a recovery pass observes when the unlink
/// committed before the state change, while <c>SameHandleDeletedAndParentFsynced</c> is what the
/// live deleter records when it compare-deleted the exact opened handle itself. The literals are
/// persisted in <c>local_erasure_work_items.DeletionEvidenceCode</c>.
/// </remarks>
internal enum LocalErasureDeletionEvidenceCode : byte
{

    AlreadyAbsent = 1,

    SameHandleDeletedAndParentFsynced = 2,

}

/// <summary>
/// Which of the two managed-file tables a restore-staging tombstone replaced.
/// </summary>
/// <remarks>
/// Exhaustive by design: staging sanitation must inventory every restored row, and a third source
/// that no code covered would leave inherited file authority in a restored dataset. The literals are
/// persisted in <c>restored_managed_file_authority_tombstones.SourceKind</c>, which also uses the
/// value to pick the closed range its <c>OriginalStateCode</c> must fall in.
/// </remarks>
internal enum RestoredManagedFileAuthoritySourceKind : byte
{

    ManagedWriteIntent = 1,

    LocalErasureWorkItem = 2,

}

/// <summary>
/// What staging sanitation found and did about the sensitivity label of a stripped row.
/// </summary>
/// <remarks>
/// An <c>AdoptedAndLabeled</c> source must have had a matching live label, and recording
/// <c>ExactLabelRemoved</c> commits to deleting exactly that row later in the same transaction.
/// Every other phase must have had no live label, and an unexpected one aborts sanitation rather
/// than being cleaned up quietly. The literals are persisted in
/// <c>restored_managed_file_authority_tombstones.LabelDispositionCode</c>, and the managed-source
/// delete guard reads them back to decide which arm a delete is taking.
/// </remarks>
internal enum RestoredManagedFileLabelDisposition : byte
{

    NoLiveLabel = 1,

    ExactLabelRemoved = 2,

}
