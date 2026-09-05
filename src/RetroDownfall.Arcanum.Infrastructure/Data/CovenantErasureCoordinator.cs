using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Security;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The one owner of every durable storage transition a Covenant erasure performs.
/// </summary>
/// <remarks>
/// A seam, for the same reason <c>ICovenantFamilyReinitializeTransition</c> is one: these are the
/// steps that cannot be undone, and keeping them behind one interface means the phase machine can be
/// exercised against every crash point without a real database being destroyed. It also means there
/// is a single place to look for "what does a reset actually do to the file" (§10.20.4).
///
/// <para>Every method is idempotent by phase. Recovery re-enters at the phase the checkpoint records
/// and calls the same methods again, so a step that had already committed must observe that and
/// return rather than repeat. The coordinator's guard skips whole recorded phases, but a crash
/// between a step's external effect and its checkpoint save leaves the phase unrecorded — so the
/// step itself has to be safe to run twice.</para>
///
/// <para>Nothing here takes, completes, or disposes a lease. The coordinator holds exactly one, and a
/// storage owner that could acquire a second would be able to reopen admission underneath the
/// operation that closed it.</para>
/// </remarks>
internal interface ICovenantErasureTransition
{

    /// <summary>
    /// Erases the canonical family in one exclusive initialized secure-delete transaction and reports
    /// the generation of the single new dataset it created.
    /// </summary>
    /// <remarks>
    /// The operation code selects what is preserved rather than what is deleted: a healthy-catalog
    /// factory erasure keeps schema objects, <c>grimoire_feature_schemas</c>, authority taint, and
    /// nonrevocable disclosure evidence that an ordinary reset also keeps, and additionally reseeds
    /// the canonical and accelerator singletons. The two arms do the same thing to storage and differ
    /// only in what survives, which is why one method takes both.
    /// </remarks>
    Task<Result<Guid>> ApplyCanonicalErasureAsync(
        CovenantExclusiveOperation operation,
        CovenantCanonicalDatasetTransition dataset,
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    /// <summary>Clears every pool and drains direct handles through the central connection owner.</summary>
    Task<Result> CloseHandlesAsync(
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a checked <c>wal_checkpoint(TRUNCATE)</c>, refusing on a busy flag or a remaining frame.
    /// </summary>
    /// <remarks>
    /// Called twice — once before compaction and once after the accelerator is installed — because
    /// each of those steps can leave frames of its own. Checked rather than best-effort: the shutdown
    /// checkpointer discards its result, which is correct for shutdown and useless as a proof that
    /// erased pages are actually gone.
    /// </remarks>
    Task<Result> TruncateWalAsync(
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inventories sidecars and staging artifacts and compacts, reporting whether <c>VACUUM</c> alone
    /// proved the freed pages gone or a verified SQLCipher export-and-atomic-replace is still needed.
    /// </summary>
    Task<Result<bool>> CompactAsync(
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    /// <summary>Writes the candidate this transition will install, and says which file it is.</summary>
    Task<Result<CovenantDigest>> StageCandidateAsync(
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    /// <summary>Proves the recorded candidate, and says what the proof was made over.</summary>
    Task<Result<CovenantDigest>> ProveStagedCandidateAsync(
        CovenantClosedPeriodAuthority authority,
        CovenantDigest stagingIdentity,
        CancellationToken cancellationToken);

    /// <summary>Installs the proven candidate, once the journal has recorded all three facts.</summary>
    Task<Result> InstallCompactionReplacementAsync(
        CovenantClosedPeriodAuthority authority,
        CovenantDigest stagingIdentity,
        CovenantDigest stagedContent,
        CovenantDigest destinationIdentity,
        CancellationToken cancellationToken);

    /// <summary>Establishes which file the installation's database currently is.</summary>
    Task<Result<CovenantDigest>> ReadCanonicalIdentityAsync(
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    /// <summary>Installs the empty accelerator and runs rank-1 integrity over it.</summary>
    Task<Result> InitializeAcceleratorAsync(
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears pools and handles a final time and proves the absence of every sidecar, journal, temp,
    /// staging, and replaced file.
    /// </summary>
    Task<Result> VerifySidecarAbsenceAsync(
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reopens read-only on the unpublished candidate state, on a handle that cannot create WAL or
    /// SHM, verifies both tiers, and closes that handle.
    /// </summary>
    Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    /// <summary>
    /// Publishes the committed dataset, master, authority, and capability snapshot while the caller's
    /// exclusive gate is still held.
    /// </summary>
    /// <remarks>
    /// It borrows the lease rather than owning it. Publication has to happen inside the closure or
    /// there is a window in which new work could be authorized under keys this erasure just
    /// invalidated.
    /// </remarks>
    Task<Result> PublishCommittedAsync(
        ICovenantExclusiveOperationLease lease,
        CovenantVerifiedCandidateState candidate,
        CancellationToken cancellationToken);

}

/// <summary>The checked fold of nonrevocable disclosure buckets preserved across local erasure.</summary>
internal sealed record CovenantDisclosureExposure
{

    internal CovenantDisclosureExposure(
        long possibleAttempts,
        CovenantDisclosureCountKind countKind)
    {

        if (possibleAttempts < 0)
        {

            throw new ArgumentOutOfRangeException(nameof(possibleAttempts));

        }

        if (countKind is not CovenantDisclosureCountKind.Exact
            and not CovenantDisclosureCountKind.LowerBound)
        {

            throw new ArgumentOutOfRangeException(nameof(countKind));

        }

        PossibleAttempts = possibleAttempts;

        CountKind = countKind;

    }

    internal long PossibleAttempts { get; }

    internal CovenantDisclosureCountKind CountKind { get; }

}

/// <summary>Effect-free facts retained from the complete pre-canonical inventory pass.</summary>
internal sealed record CovenantErasureInventorySummary
{

    internal CovenantErasureInventorySummary(
        long databaseArtifactCount,
        long managedFileArtifactCount,
        CovenantDisclosureExposure exposure)
    {

        ArgumentNullException.ThrowIfNull(exposure);

        if (databaseArtifactCount < 0 || managedFileArtifactCount < 0)
        {

            throw new ArgumentOutOfRangeException(nameof(databaseArtifactCount));

        }

        DatabaseArtifactCount = databaseArtifactCount;

        ManagedFileArtifactCount = managedFileArtifactCount;

        Exposure = exposure;

    }

    internal long DatabaseArtifactCount { get; }

    internal long ManagedFileArtifactCount { get; }

    internal CovenantDisclosureExposure Exposure { get; }

}

/// <summary>One raw-label keyset step and at most one database-kernel page.</summary>
internal sealed record CovenantDatabaseErasureBatch
{

    internal CovenantDatabaseErasureBatch(
        Guid? nextCursor,
        bool isComplete,
        CovenantProtectedArtifactErasurePage? page)
    {

        if (nextCursor == Guid.Empty || (!isComplete && nextCursor is null))
        {

            throw new ArgumentException("An incomplete database erasure batch requires a nonempty cursor.");

        }

        NextCursor = nextCursor;

        IsComplete = isComplete;

        Page = page;

    }

    internal Guid? NextCursor { get; }

    internal bool IsComplete { get; }

    internal CovenantProtectedArtifactErasurePage? Page { get; }

}

/// <summary>One raw-label keyset step and at most 256 managed-file kernel requests.</summary>
internal sealed record CovenantManagedFileErasureBatch
{

    private readonly CovenantManagedFileErasureRequest[] _requests;

    internal CovenantManagedFileErasureBatch(
        Guid? nextCursor,
        bool isComplete,
        IReadOnlyList<CovenantManagedFileErasureRequest> requests)
    {

        ArgumentNullException.ThrowIfNull(requests);

        if (nextCursor == Guid.Empty || (!isComplete && nextCursor is null))
        {

            throw new ArgumentException("An incomplete managed erasure batch requires a nonempty cursor.");

        }

        if (requests.Count > CovenantProtectedArtifactErasurePage.MaxItems)
        {

            throw new ArgumentOutOfRangeException(nameof(requests));

        }

        NextCursor = nextCursor;

        IsComplete = isComplete;

        _requests = [.. requests];

    }

    internal Guid? NextCursor { get; }

    internal bool IsComplete { get; }

    internal IReadOnlyList<CovenantManagedFileErasureRequest> Requests => _requests;

}

/// <summary>
/// Supplies the bounded, comprehensive protected state one erasure must remove.
/// </summary>
internal interface ICovenantErasureInventorySource
{

    Task<Result<CovenantErasureInventorySummary>> PreflightBeforeCanonicalAsync(
        CovenantExclusiveOperation operation,
        Guid datasetGeneration,
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    Task<Result> PreflightRemainingManagedAsync(
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    Task<Result<CovenantDatabaseErasureBatch>> ReadNextDatabaseBatchAsync(
        Guid datasetGeneration,
        Guid? afterLabelId,
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    Task<Result<CovenantManagedFileErasureBatch>> ReadNextManagedFileBatchAsync(
        Guid operationId,
        Guid? afterLabelId,
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    Task<Result<CovenantDisclosureExposure>> ReadDisclosureExposureAsync(
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken);

    Task<Result<CovenantOfflineTransitionSourceState>> ReadOfflineTransitionSourceStateAsync(
        CancellationToken cancellationToken);

}

/// <summary>
/// The immutable facts a durable erasure checkpoint carries, whichever shape recorded them.
/// </summary>
/// <remarks>
/// A Covenant reset commits <c>CovenantOfflineTransitionLaunchV4</c> and a healthy-catalog factory
/// erasure commits <c>DataRetentionFactoryTransitionLaunchV2</c>. The two shapes differ because their
/// journal headers do, but the four facts an erasure resumes from are identical, and one projection
/// is what lets a single coordinator own both without a second phase machine (§10.20.4).
/// </remarks>
internal sealed record CovenantErasureCheckpointState(
    Guid OperationId,
    CovenantExclusiveOperation Operation,
    CovenantDigest EffectDigest,
    CovenantResetPhase Phase,
    CovenantCanonicalDatasetTransition Dataset)
{

    /// <summary>
    /// The exact gate identity this checkpoint describes, and the only one it may be resumed under.
    /// </summary>
    public CovenantExclusiveRecoveryOwner Owner => new(OperationId, Operation, EffectDigest);

    /// <summary>
    /// Projects the launch a retention-mutation row commits when that mutation is a Covenant reset.
    /// </summary>
    /// <remarks>
    /// <paramref name="operationId"/> is the durable server operation the row belongs to, and the
    /// projection refuses a payload naming anything else. That check is the whole reason this lives in
    /// one place: a retry with a changed plan must not be able to rebuild an owner matching a closed
    /// scope it has no right to adopt, and a rule enforced at two call sites is a rule that eventually
    /// holds at one.
    ///
    /// <para><paramref name="describesCovenantErasure"/> separates the two failures that must not be
    /// reported as one. An ordinary retention mutation closed nothing, and its remedy is ordinary
    /// reconciliation; a launch this build cannot resume has admission closed behind it, and its
    /// remedy is an operator. Collapsing them would tell somebody to leave a stuck ordinary mutation
    /// alone forever.</para>
    ///
    /// <para>The row's own version decides which of those two it is, rather than whether the payload
    /// happens to decode. An ordinary mutation's journal is a different shape under a different
    /// version, so reading a decode failure as "erasure" would park every ordinary mutation whose
    /// payload this build could not read for any reason at all.</para>
    /// </remarks>
    public static Result<CovenantErasureCheckpointState> FromMutationCheckpoint(
        Guid operationId,
        int checkpointVersion,
        ReadOnlySpan<byte> payload,
        out bool describesCovenantErasure)
    {

        describesCovenantErasure =
            checkpointVersion == CovenantOfflineTransitionLaunchV4.CurrentVersion;

        if (!describesCovenantErasure)
        {

            return Unresumable();

        }

        Result<CovenantOfflineTransitionLaunchV4> decoded =
            CovenantRecoveryCheckpointCodec.DecodeCovenantOfflineTransitionLaunch(payload);

        return decoded.IsFailure
            ? Result<CovenantErasureCheckpointState>.Failure(decoded.Error)
            : Owned(
                operationId,
                CovenantRecoveryCheckpointCodec.RecoveryOwner(decoded.Value),
                DatasetTransition(decoded.Value.SourceDatasetGeneration, decoded.Value.SourceEpochs, decoded.Value.TargetDatasetGeneration, decoded.Value.TargetEpochs));

    }

    /// <summary>
    /// Projects the launch a healthy-catalog factory erasure commits.
    /// </summary>
    /// <remarks>
    /// The phase is always the first one. A launch records what was committed to, and an offline
    /// transition's progress past that point lives in the authenticated journal rather than in this
    /// row — so a projection that read a phase out of the row would be reporting a step the row is no
    /// longer the authority for.
    /// </remarks>
    public static Result<CovenantErasureCheckpointState> FromFactoryResetCheckpoint(
        Guid operationId,
        int checkpointVersion,
        ReadOnlySpan<byte> payload)
    {

        if (checkpointVersion != DataRetentionFactoryTransitionLaunchV2.CurrentVersion)
        {

            return Unresumable();

        }

        Result<DataRetentionFactoryTransitionLaunchV2> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryTransitionLaunch(payload);

        return decoded.IsFailure
            ? Result<CovenantErasureCheckpointState>.Failure(decoded.Error)
            : Owned(
                operationId,
                CovenantRecoveryCheckpointCodec.RecoveryOwner(decoded.Value),
                DatasetTransition(decoded.Value.SourceDatasetGeneration, decoded.Value.SourceEpochs, decoded.Value.TargetDatasetGeneration, decoded.Value.TargetEpochs));

    }

    private static CovenantCanonicalDatasetTransition DatasetTransition(
        Guid sourceGeneration,
        CovenantOfflineTransitionEpochsV1 sourceEpochs,
        Guid targetGeneration,
        CovenantOfflineTransitionEpochsV1 targetEpochs) =>
        new(sourceGeneration, sourceEpochs, targetGeneration, targetEpochs);

    private static Result<CovenantErasureCheckpointState> Owned(
        Guid operationId,
        Result<CovenantExclusiveRecoveryOwner> owner,
        CovenantCanonicalDatasetTransition dataset) =>
        owner.IsFailure
            ? Unresumable()
            : Project(operationId, owner.Value, CovenantResetPhaseMachine.First, dataset);

    private static Result<CovenantErasureCheckpointState> Project(
        Guid operationId,
        CovenantExclusiveRecoveryOwner owner,
        CovenantResetPhase phase,
        CovenantCanonicalDatasetTransition dataset) =>
        owner.OperationId == operationId
        && CovenantResetPhaseMachine.IsDeclared(phase)
        && dataset.IsCoherent
            ? Result<CovenantErasureCheckpointState>.Success(
                new CovenantErasureCheckpointState(
                    owner.OperationId,
                    owner.Operation,
                    owner.EffectDigest,
                    phase,
                    dataset))
            : Unresumable();

    private static Result<CovenantErasureCheckpointState> Unresumable() =>
        Result<CovenantErasureCheckpointState>.Failure(
            new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "This Covenant erasure checkpoint cannot be resumed by this build."));

}

/// <summary>
/// How one erasure finished, in facts an operator surface can report independently.
/// </summary>
/// <remarks>
/// Three separate booleans rather than one status code, because they answer three different questions
/// and an operator needs all three. <see cref="CanonicalResetApplied"/> says the family's rows are
/// gone. <see cref="LocalSecureErasureComplete"/> says the bytes were proven unrecoverable, which is
/// strictly later and can fail on its own. <see cref="ExternalDisclosuresNotRevocable"/> says nothing
/// about local work at all: it is what this installation cannot undo no matter how completely it
/// erases itself. Collapsing them would let a half-proven erasure read as a finished one.
/// </remarks>
internal sealed record CovenantErasureCompletion(
    CovenantExclusiveLeaseDisposition Disposition,
    bool CanonicalResetApplied,
    bool LocalSecureErasureComplete,
    CovenantDisclosureExposure Exposure,
    string? BlockingErrorCode)
{

    /// <summary>The legacy projection, derived only from irreversible disclosure evidence.</summary>
    internal bool ExternalDisclosuresNotRevocable => Exposure.PossibleAttempts > 0;

}

/// <summary>
/// The points inside one phase a crash may fall between.
/// </summary>
/// <remarks>
/// Named rather than counted, because what makes each one interesting is what the durable record
/// says at that instant, and the names are what a failing case has to report. Between the in-flight
/// publication and the effect the journal says an effect may have begun; between the effect and the
/// completion it says the same thing while the effect has in fact happened. That asymmetry is the
/// whole reason the pair cannot be collapsed into one write, so it is the pair a matrix has to
/// interrupt.
/// </remarks>
internal enum CovenantErasureFaultBoundary : byte
{

    /// <summary>Nothing of this phase has been published or performed.</summary>
    BeforePhaseBegin = 1,

    /// <summary>The in-flight publication landed; the effect has not run.</summary>
    AfterPhaseBegin = 2,

    /// <summary>The effect ran; nothing durable yet says it completed.</summary>
    AfterPhaseEffect = 3,

    /// <summary>The completion landed; the next phase has not begun.</summary>
    AfterPhaseComplete = 4,

}

/// <summary>
/// The seam a crash matrix interrupts an erasure at, and a production no-op everywhere else.
/// </summary>
/// <remarks>
/// A constructor-supplied delegate rather than a virtual method or an injected policy, because it has
/// exactly one production implementation and that implementation does nothing. What it buys is the
/// only honest way to test a resumption: stop a real erasure at a real boundary, leave the durable
/// state exactly as the crash left it, and let a second coordinator find it.
/// </remarks>
internal delegate Task<Result> CovenantErasureFaultSeam(
    CovenantErasureFaultBoundary boundary,
    CovenantResetPhase phase,
    CancellationToken cancellationToken);

/// <summary>
/// The single coordinator for Covenant reset and healthy-catalog factory erasure.
/// </summary>
/// <remarks>
/// It owns the phase machine, the exclusive recovery owner, the two shared kernels' authority, and
/// the one disposition. It owns no deletion algorithm: database artifacts go through the shared
/// protected-artifact kernel and managed files through the shared managed-file kernel, both borrowing
/// this coordinator's own exclusive lease rather than acquiring one, so no kernel can reopen admission
/// or deadlock against this operation's drain. It never resolves a managed-file capability opener or
/// ownership verifier, because a second opinion about which file is Arcanum's is one opinion too many
/// (§10.17).
///
/// <para>The gate owner always uses the durable server operation identity. An optional caller
/// <c>RequestedOperationId</c> is the normalized replay key and never a gate owner: two callers
/// replaying the same name must resolve to the same operation, and an owner built from the replay key
/// would let the second adopt the first's closed scope.</para>
///
/// <para>The phase order puts the whole database half before the first managed file is touched. The
/// managed-file kernel persists its durable work item before its first external effect, and that work
/// item is a database row — so a file deleted before the transaction that authorized it would be a
/// deletion no surviving row can explain.</para>
/// </remarks>
internal sealed class CovenantErasureCoordinator(
    ILongRunningOperationCoordinator operations,
    ILongRunningOperationStore store,
    ICovenantOperationGate gate,
    ICovenantProtectedArtifactErasureKernel artifacts,
    ICovenantManagedFileErasureKernel managedFiles,
    ICovenantErasureInventorySource inventory,
    ICovenantErasureTransition transition,
    ICovenantDisclosureWriterLifecycle disclosureWriter,
    IGrimoireOfflineTransitionPhaseAuthority phaseAuthority,
    GrimoireOfflineTransitionEffectHandlerRegistry effects,
    IGrimoireConnectionAdmissionGate admissionGate,
    IGrimoireMaintenanceConnectionFactory maintenanceConnections,
    IGrimoireMaintenancePathAuthority maintenancePaths,
    IGrimoireDbPassphraseSource passphrase,
    ICovenantClosedPeriodLedgerConnection ledgerConnection,
    ICovenantConnectionDrain drain,
    GrimoireOfflineTransitionDatabaseReconciler reconciler,
    LongRunningOperationOwnership ownership,
    TimeProvider timeProvider,
    ILogger<CovenantErasureCoordinator> logger,
    CovenantErasureFaultSeam? faultSeam = null)
{

    /// <summary>
    /// How long the one reopening decision may take, on a token this coordinator owns.
    /// </summary>
    /// <remarks>
    /// The caller's token is an HTTP request token. After a durable mutation it is exactly the wrong
    /// token to decide admission with: a client that hung up would leave the installation closed with
    /// a proven-complete erasure behind it, and the gate's disposition throws on a cancelled token
    /// rather than quietly proceeding.
    /// </remarks>
    internal static readonly TimeSpan DispositionBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the post-proof checkpoint, publication, and warm-writer restart may take.
    /// </summary>
    internal static readonly TimeSpan PublicationAndWriterBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long an unchanged-generation writer restoration may take before rollback is refused.
    /// </summary>
    internal static readonly TimeSpan WriterRestorationBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the durable ledger gets to record an uncertain disposition independently.
    /// </summary>
    internal static readonly TimeSpan FailureRecordingBound = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan FailureRecordingRetryDelay = TimeSpan.FromMilliseconds(10);

    private const string ResetSummary = "Erasing the Covenant family.";

    private readonly ILongRunningOperationCoordinator _operations =
        operations ?? throw new ArgumentNullException(nameof(operations));

    private readonly ILongRunningOperationStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly ICovenantOperationGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    private readonly ICovenantProtectedArtifactErasureKernel _artifacts =
        artifacts ?? throw new ArgumentNullException(nameof(artifacts));

    private readonly ICovenantManagedFileErasureKernel _managedFiles =
        managedFiles ?? throw new ArgumentNullException(nameof(managedFiles));

    private readonly ICovenantErasureInventorySource _inventory =
        inventory ?? throw new ArgumentNullException(nameof(inventory));

    private readonly ICovenantErasureTransition _transition =
        transition ?? throw new ArgumentNullException(nameof(transition));

    private readonly ICovenantDisclosureWriterLifecycle _disclosureWriter =
        disclosureWriter ?? throw new ArgumentNullException(nameof(disclosureWriter));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<CovenantErasureCoordinator> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IGrimoireConnectionAdmissionGate _admissionGate =
        admissionGate ?? throw new ArgumentNullException(nameof(admissionGate));

    /// <summary>
    /// The closed table saying what each journal kind is allowed to be and owes.
    /// </summary>
    /// <remarks>
    /// Injected rather than reached for statically, because it is the seam a third kind of offline
    /// transition arrives through. A coordinator that composed the table itself would make adding one
    /// an edit to this file.
    /// </remarks>
    private readonly GrimoireOfflineTransitionEffectHandlerRegistry _effects =
        effects ?? throw new ArgumentNullException(nameof(effects));

    private readonly IGrimoireMaintenanceConnectionFactory _maintenanceConnections =
        maintenanceConnections ?? throw new ArgumentNullException(nameof(maintenanceConnections));

    private readonly IGrimoireMaintenancePathAuthority _maintenancePaths =
        maintenancePaths ?? throw new ArgumentNullException(nameof(maintenancePaths));

    private readonly IGrimoireDbPassphraseSource _passphrase =
        passphrase ?? throw new ArgumentNullException(nameof(passphrase));

    private readonly ICovenantClosedPeriodLedgerConnection _ledgerConnection =
        ledgerConnection ?? throw new ArgumentNullException(nameof(ledgerConnection));

    private readonly ICovenantConnectionDrain _drain =
        drain ?? throw new ArgumentNullException(nameof(drain));

    private readonly CovenantErasureFaultSeam _faultSeam = faultSeam ?? NoFault;

    private static Task<Result> NoFault(
        CovenantErasureFaultBoundary boundary,
        CovenantResetPhase phase,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    /// <summary>
    /// The Grimoire closure one run holds, spent exactly once on one disposition.
    /// </summary>
    /// <remarks>
    /// Two paths legitimately reach the spending: the terminal suffix, which knows which disposition
    /// was earned, and the run's own unwind, which does not and therefore keeps the installation
    /// closed. Releasing twice would reopen ordinary admission on a generation somebody else may
    /// already have closed, so the first one wins and the second is a no-op.
    ///
    /// <para>The lane goes first, and the gate enforces that rather than trusting it: a closure with
    /// a live maintenance authority refuses to disposition, which is the right refusal, because
    /// reopening ordinary admission beside a live exclusive handle is what the closed period exists
    /// to prevent. The ledger permit goes with it for the same reason.</para>
    /// </remarks>
    private sealed class CovenantGrimoireClosure(
        IGrimoireClosingOwner closingOwner,
        IGrimoireExclusiveClosedLease closed,
        IGrimoireMaintenanceIoLane lane,
        IGrimoireScopedConnectionPermit ledger,
        ICovenantClosedPeriodLedgerConnection ledgerConnection,
        ICovenantConnectionDrain drain)
    {

        private int _spent;

        /// <summary>The closed authority every purpose-bound capability is issued from.</summary>
        internal IGrimoireExclusiveClosedLease Closed => closed;

        /// <summary>The one lane every maintenance open of this closed period is spent on.</summary>
        internal IGrimoireMaintenanceIoLane Lane => lane;

        /// <summary>The permit that keeps the durable ledger's own connection usable while closed.</summary>
        internal IGrimoireScopedConnectionPermit Permit => ledger;

        /// <summary>The ledger this closed period opens its durable windows through.</summary>
        internal ICovenantClosedPeriodLedgerConnection Ledger => ledgerConnection;

        /// <summary>The exact connection object the permit was bound to.</summary>
        internal SqliteConnection LedgerConnection => (SqliteConnection)ledgerConnection.Connection;

        /// <summary>The drain whose pool clear follows every ledger close.</summary>
        internal ICovenantConnectionDrain Drain => drain;

        internal async Task<Result> ReleaseAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken)
        {

            if (Interlocked.Exchange(ref _spent, 1) != 0)
            {

                return Result.Success();

            }

            await ledger.DisposeAsync().ConfigureAwait(false);

            await lane.DisposeAsync().ConfigureAwait(false);

            Result completed = await closed
                .CompleteAsync(disposition, cancellationToken)
                .ConfigureAwait(false);

            await closed.DisposeAsync().ConfigureAwait(false);

            await closingOwner.DisposeAsync().ConfigureAwait(false);

            return completed;

        }

    }

    /// <summary>The one closure a run takes, so its unwind can find it without threading it back.</summary>
    private sealed class GrimoireClosureSlot
    {

        internal CovenantGrimoireClosure? Closure { get; set; }

    }

    /// <summary>
    /// Takes the Grimoire's own closure, the one maintenance lane, and the ledger's permit.
    /// </summary>
    /// <remarks>
    /// This is the fifth and last item of the authority order - the held installation lock, the
    /// validated launch, the exact Covenant lease, the verified journal publication, and only then
    /// the Grimoire closing and closed owner. Each earlier item is what makes the next legitimate,
    /// and reversing any pair would let something be closed that nothing durable had yet claimed.
    ///
    /// <para>The permit is taken here rather than at the terminal write, because a closure that
    /// cannot keep the ledger usable is one this erasure must not enter at all: the compare-exchange
    /// the journal binds itself to happens while admission is closed, and discovering at that point
    /// that it cannot be made would leave a family already erased and no row able to say so.</para>
    ///
    /// <para>The lane's revalidation is answered from the Covenant lease this run already holds. A
    /// second opinion about whether this operation still owns the scope would be a second authority,
    /// and the lease is the one that closed the scope in the first place.</para>
    /// </remarks>
    private async Task<Result<CovenantGrimoireClosure>> CloseGrimoireAsync(
        CovenantExclusiveRecoveryOwner owner,
        CovenantExclusiveLease lease,
        CancellationToken cancellationToken)
    {

        Result<IGrimoireClosingOwner> closing = _admissionGate.BeginOrResumeExclusive(owner);

        if (closing.IsFailure)
        {

            return Result<CovenantGrimoireClosure>.Failure(closing.Error);

        }

        IGrimoireClosingOwner closingOwner = closing.Value;

        Result drained = await _admissionGate
            .DrainRequestAndWorkAsync(closingOwner, cancellationToken)
            .ConfigureAwait(false);

        if (drained.IsFailure)
        {

            return await AbandonClosingAsync(closingOwner, drained.Error).ConfigureAwait(false);

        }

        Result<IGrimoireExclusiveClosedLease> closed = await _admissionGate
            .CloseConnectionAdmissionAsync(closingOwner, cancellationToken)
            .ConfigureAwait(false);

        if (closed.IsFailure)
        {

            // The owner is kept, not abandoned. Stage two commits the gate to Closed on a burned
            // generation before it waits on terminal callbacks and drains, so a failure here may
            // already be past the point of no return - and the gate's only route back to ordinary
            // admission from there is a closed lease that was never issued. What it does offer is a
            // retry by the exact same closing owner, which a later run reaches by resuming the
            // exclusive scope. Disposing it would take that away and hold admission shut for the life
            // of the process.
            return Result<CovenantGrimoireClosure>.Failure(closed.Error);

        }

        // Everything from here to the closure being handed back runs inside a catch. The lease is
        // issued and the gate is committed to closed, but nothing yet holds the lease on the caller's
        // behalf - the closure that would release it does not exist until the last statement below.
        // An exception in that window, and the maintenance lane honours the run's cancellation token
        // in three places, would leave the gate closed with a live lease nobody can reach: worse than
        // any refusal, because even the retry the gate offers is then refused.
        try
        {

            Result<IGrimoireMaintenanceIoLane> lane = await closed.Value
                .AcquireMaintenanceIoLaneAsync(
                    (laneOwner, _, token) => RevalidateAsync(lease, laneOwner, owner, token),
                    cancellationToken)
                .ConfigureAwait(false);

            if (lane.IsFailure)
            {

                return await AbandonClosedAsync(closed.Value, closingOwner, lane.Error).ConfigureAwait(false);

            }

            // The durable ledger is promoted and physically opened for the whole closed period, not
            // only around the terminal write. Ordinary admission is shut, so every statement this
            // erasure makes against its own database - the artifact and managed-file kernels'
            // transactions as much as the terminal compare-exchange - would otherwise be refused; and
            // the store issues its statements directly whenever the connection is already open, which
            // is the one way past an interceptor that is correctly saying no.
            if (_ledgerConnection.Connection is not SqliteConnection ledgerConnection)
            {

                await lane.Value.DisposeAsync().ConfigureAwait(false);

                return await AbandonClosedAsync(
                    closed.Value,
                    closingOwner,
                    MaintenanceFailure()).ConfigureAwait(false);

            }

            Result<IGrimoireScopedConnectionPermit> ledger =
                closed.Value.AcquireScopedConnectionPermit(ledgerConnection);

            if (ledger.IsFailure)
            {

                await lane.Value.DisposeAsync().ConfigureAwait(false);

                return await AbandonClosedAsync(
                    closed.Value,
                    closingOwner,
                    ledger.Error).ConfigureAwait(false);

            }

            return Result<CovenantGrimoireClosure>.Success(
                new CovenantGrimoireClosure(
                    closingOwner,
                    closed.Value,
                    lane.Value,
                    ledger.Value,
                    _ledgerConnection,
                    _drain));

        }
        catch (Exception failed)
        {

            // Reopened and rethrown. The caller's own unwinding is entitled to see the exception it
            // threw, and the gate is entitled not to be left closed over it - and only one of those
            // is the exception's own business. The disposition runs on an uncancellable token because
            // a cleanup unwinding under an ambient shutdown is exactly the caller that must still be
            // allowed to reopen.
            _ = await closed.Value.CompleteAsync(
                CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                CancellationToken.None).ConfigureAwait(false);

            await closed.Value.DisposeAsync().ConfigureAwait(false);

            await closingOwner.DisposeAsync().ConfigureAwait(false);

            _logger.LogWarning(
                failed,
                "A Covenant erasure could not finish taking its Grimoire closure; ordinary admission "
                + "was reopened before the failure was allowed to propagate.");

            throw;

        }

    }

    /// <summary>
    /// Runs one durable-ledger window of a closed period on the connection the gate admitted.
    /// </summary>
    /// <remarks>
    /// Ordinary admission is shut, so the operation store's usual route through the connection
    /// interceptor is refused - correctly, because that route is what the closed period exists to
    /// stop. What the gate still admits is one exact connection object, and the store issues its
    /// statements on that object directly whenever it is already physically open. So a window opens
    /// it, does the work, and closes it again.
    ///
    /// <para>A window rather than the whole closed period, and that is the load-bearing part. The
    /// canonical transaction takes an exclusive maintenance lock on the same file, and a live ledger
    /// handle would contend with it - so the erasure would retry its own lock against itself until
    /// the backoff gave up. Every window therefore ends before the next exclusive open begins.</para>
    ///
    /// <para>The pool is cleared after the close rather than trusted to empty itself. The ledger's
    /// connection string leaves provider pooling on, unlike every maintenance connection, so a closed
    /// handle would otherwise return to a pool rather than release the file - and a pooled handle
    /// over a database this erasure is about to replace is exactly the handle the sidecar proof finds.</para>
    ///
    /// <para>A run with no closure has nothing to promote, which is the shape a unit-level harness
    /// exercises: it never entered a closed period, so the ordinary route works.</para>
    /// </remarks>
    private static async Task<T> WithLedgerAsync<T>(
        CovenantGrimoireClosure? closure,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {

        if (closure is null)
        {

            return await work(cancellationToken).ConfigureAwait(false);

        }

        Result<IGrimoireTrackedMaintenanceHandle> admitted = closure.Permit.AcquireOpen(
            closure.LedgerConnection,
            closure.Closed.Owner,
            closure.Closed.Generation,
            closure.Lane);

        if (admitted.IsFailure)
        {

            return await work(cancellationToken).ConfigureAwait(false);

        }

        IGrimoireTrackedMaintenanceHandle handle = admitted.Value;

        if (handle.ReportOpenStarted().IsFailure)
        {

            _ = handle.ReportNotOpened();

            return await work(cancellationToken).ConfigureAwait(false);

        }

        try
        {

            // Through the ledger rather than on the connection object. A bare open here would run
            // none of this installation's connection policy, and the two pragmas it would silently
            // drop - secure_delete and foreign_keys - are the difference between an erasure that
            // removes the bytes and cascades, and one that leaves both behind while reporting success.
            await closure.Ledger.OpenAsync(cancellationToken).ConfigureAwait(false);

            return await work(cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            await closure.LedgerConnection.CloseAsync().ConfigureAwait(false);

            closure.Drain.ClearExactPoolAfterClose(closure.LedgerConnection);

            _ = handle.ReportPhysicallyClosed();

        }

    }

    /// <summary>
    /// How the Grimoire's closure ends, given how the Covenant scope ended.
    /// </summary>
    /// <remarks>
    /// It always reopens, and that is not the same decision as the Covenant disposition beside it.
    /// The Grimoire closure is the mechanism this erasure used to get exclusive access to a file; the
    /// Covenant scope is the durable statement about whether the installation may be used. Leaving
    /// ordinary connection admission shut after a parked erasure would not keep anything safe that
    /// the Covenant scope is not already keeping safe — it would make the database unopenable, so an
    /// operator could not even read the operation row that says what to do next, and no later process
    /// could reach the journal's own terminal reconciliation.
    ///
    /// <para>Which reopening disposition still matters, because the gate records it: a commit and a
    /// rollback leave the same open gate but a different account of why.</para>
    /// </remarks>
    private static CovenantExclusiveLeaseDisposition GrimoireDispositionFor(
        CovenantExclusiveLeaseDisposition covenant) =>
        covenant is CovenantExclusiveLeaseDisposition.CommitAndReopen
            ? CovenantExclusiveLeaseDisposition.CommitAndReopen
            : CovenantExclusiveLeaseDisposition.RollbackAndReopen;

    /// <summary>Answers the lane's revalidation from the lease that closed the scope, and nothing else.</summary>
    private static async ValueTask<bool> RevalidateAsync(
        CovenantExclusiveLease lease,
        CovenantExclusiveRecoveryOwner laneOwner,
        CovenantExclusiveRecoveryOwner expected,
        CancellationToken cancellationToken) =>
        laneOwner == expected
        && (await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false)).IsSuccess;

    /// <summary>Lets go of a half-taken closure so ordinary admission is not left shut behind it.</summary>
    private static async Task<Result<CovenantGrimoireClosure>> AbandonClosingAsync(
        IGrimoireClosingOwner closingOwner,
        Error error)
    {

        await closingOwner.DisposeAsync().ConfigureAwait(false);

        return Result<CovenantGrimoireClosure>.Failure(error);

    }

    /// <summary>
    /// Gives up a closure that got as far as a closed lease, leaving ordinary admission open.
    /// </summary>
    /// <remarks>
    /// Completed rather than disposed. Completing the lease is the gate's only edge from closed back
    /// to ordinary; disposing it releases the lease and leaves the gate closed, which for a closure
    /// that never ran a phase means an installation nobody can open over a setup step that failed
    /// before it touched anything.
    ///
    /// <para>The disposition is a rollback because nothing durable happened: the lane, the ledger
    /// connection or the permit could not be taken, and no phase has begun. A commit would record a
    /// reason for the reopen that did not happen.</para>
    /// </remarks>
    private static async Task<Result<CovenantGrimoireClosure>> AbandonClosedAsync(
        IGrimoireExclusiveClosedLease closed,
        IGrimoireClosingOwner closingOwner,
        Error error)
    {

        _ = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None).ConfigureAwait(false);

        await closed.DisposeAsync().ConfigureAwait(false);

        return await AbandonClosingAsync(closingOwner, error).ConfigureAwait(false);

    }

    /// <summary>
    /// Runs, or resumes, one erasure and reports how it left admission.
    /// </summary>
    /// <remarks>
    /// The checkpoint must already record <c>InventoryPrepared</c> — <c>CovenantResetCheckpointInitiator</c>
    /// is the only thing that can commit it, and its <c>GateAdmission</c> cannot be constructed without
    /// that commit winning. This method therefore never opens a closure that nothing durable describes.
    /// </remarks>
    internal async Task<Result<CovenantErasureCompletion>> RunAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId,
        CancellationToken cancellationToken) =>
        await RunAsync(
            operation,
            checkpoint,
            ownerId,
            factoryContinuation: null,
            CovenantExclusiveOperation.CovenantReset,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Runs, or resumes, a healthy-catalog factory erasure with its required ordinary cleanup.
    /// </summary>
    /// <remarks>
    /// The continuation is restart-idempotent and deliberately is not a new phase. It runs after
    /// <c>ManagedArtifactsProcessed</c> is durable and before <c>HandlesClosed</c>; therefore recovery
    /// repeats it while the former is the durable boundary and skips it once the latter is durable.
    /// </remarks>
    internal async Task<Result<CovenantErasureCompletion>> RunAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId,
        Func<CancellationToken, Task<Result>> factoryContinuation,
        CancellationToken cancellationToken) =>
        await RunAsync(
            operation,
            checkpoint,
            ownerId,
            factoryContinuation,
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            cancellationToken).ConfigureAwait(false);

    private async Task<Result<CovenantErasureCompletion>> RunAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId,
        Func<CancellationToken, Task<Result>>? factoryContinuation,
        CovenantExclusiveOperation requiredOperation,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        ArgumentNullException.ThrowIfNull(checkpoint);

        if (checkpoint.Operation != requiredOperation
            || requiredOperation == CovenantExclusiveOperation.HealthyCatalogFactoryErasure
                && factoryContinuation is null)
        {

            return Result<CovenantErasureCompletion>.Failure(
                new Error(
                    ErrorCodes.Covenant.InvalidScope,
                    requiredOperation == CovenantExclusiveOperation.CovenantReset
                        ? "The reset erasure entry point accepts only a Covenant reset."
                        : "A healthy-catalog factory erasure requires its ordinary cleanup continuation."));

        }

        Result admissible = RequireResumableCheckpoint(operation, checkpoint);

        if (admissible.IsFailure)
        {

            return Result<CovenantErasureCompletion>.Failure(admissible.Error);

        }

        CovenantExclusiveRecoveryOwner owner = new(
            checkpoint.OperationId,
            checkpoint.Operation,
            checkpoint.EffectDigest);

        // InventoryPrepared may be entering for the first time or resuming a drained gate.
        // ReopenedVerified may likewise follow a successful reopen whose post-disposition journal
        // finalizer lost its CAS. Every destructive intermediate phase is resume-only: acquiring an
        // open scope there would repeat effects whose checkpoint says admission must still be closed.
        Result<CovenantExclusiveLease> acquired = checkpoint.Phase is CovenantResetPhase.InventoryPrepared
            or CovenantResetPhase.ReopenedVerified
            ? await _gate.ResumeOrAcquireExclusiveAsync(owner, cancellationToken).ConfigureAwait(false)
            : await _gate.ResumeExclusiveAsync(owner, cancellationToken).ConfigureAwait(false);

        if (acquired.IsFailure)
        {

            return Result<CovenantErasureCompletion>.Failure(acquired.Error);

        }

        await using CovenantExclusiveLease lease = acquired.Value;

        Guid? datasetGeneration = lease.Snapshot.DatasetGeneration;

        if (datasetGeneration is null || datasetGeneration == Guid.Empty)
        {

            return Result<CovenantErasureCompletion>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The exclusive Covenant lease did not capture a dataset generation."));

        }

        Result<CovenantArtifactErasureAuthority> authority = CovenantArtifactErasureAuthority.ForExclusive(
            lease,
            checkpoint.Operation);

        if (authority.IsFailure)
        {

            return Result<CovenantErasureCompletion>.Failure(authority.Error);

        }

        // Claimed for the length of the run because the durable lease stops being renewed once the
        // journal opens. Nothing else would then stop the background reconciliation pass finding an
        // apparently abandoned row and starting a second recovery beside this one.
        if (!ownership.TryClaim(operation.Id, out Guid claim))
        {

            return Result<CovenantErasureCompletion>.Failure(
                new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This process is already running the operation this erasure was asked to run."));

        }

        try
        {

            return await RunUnderLeaseAsync(
                operation,
                checkpoint,
                datasetGeneration.Value,
                ownerId,
                lease,
                authority.Value,
                factoryContinuation,
                cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _ = ownership.Release(operation.Id, claim);

        }

    }

    /// <summary>
    /// Runs the closed period, and guarantees the Grimoire closure is let go of however it ends.
    /// </summary>
    /// <remarks>
    /// A wrapper rather than a <c>finally</c> inside the run, because the run has a great many
    /// endings and every one of them would otherwise have to remember. What it releases is a closure
    /// no disposition was ever reached for, so it keeps the installation closed: reopening on the way
    /// out of a failure would be announcing an answer nothing durable carries.
    /// </remarks>
    private async Task<Result<CovenantErasureCompletion>> RunUnderLeaseAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        Guid datasetGeneration,
        string ownerId,
        CovenantExclusiveLease lease,
        CovenantArtifactErasureAuthority authority,
        Func<CancellationToken, Task<Result>>? factoryContinuation,
        CancellationToken cancellationToken)
    {

        GrimoireClosureSlot stranded = new();

        try
        {

            return await RunClosedAsync(
                operation,
                checkpoint,
                datasetGeneration,
                ownerId,
                lease,
                authority,
                factoryContinuation,
                stranded,
                cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            if (stranded.Closure is { } unspent)
            {

                _ = await unspent
                    .ReleaseAsync(
                        CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                        CancellationToken.None)
                    .ConfigureAwait(false);

            }

        }

    }

    private async Task<Result<CovenantErasureCompletion>> RunClosedAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        Guid datasetGeneration,
        string ownerId,
        CovenantExclusiveLease lease,
        CovenantArtifactErasureAuthority authority,
        Func<CancellationToken, Task<Result>>? factoryContinuation,
        GrimoireClosureSlot stranded,
        CancellationToken cancellationToken)
    {

        CovenantErasureCheckpointState state = checkpoint;

        // Released in the terminal suffix beside the Covenant lease, and in the unwind below on every
        // path that never reaches one. A closure left behind holds ordinary admission shut for the
        // life of the process, which is strictly worse than the failure that stranded it.
        CovenantGrimoireClosure? closure = null;

        CovenantClosedPeriodAuthority? maintenance = null;

        // Opened after the quiesce rather than before it. A quiesce that fails has closed nothing and
        // touched nothing, and the lifecycle graph offers a published journal no way back out that
        // does not claim admission was closed - so a transition that never closed anything would have
        // to park an installation it had done nothing to. Nothing durable happens before this point.
        GrimoireOfflineTransitionPhaseSession? phases = null;

        // Resolved from the journal once it is open, and never from the checkpoint. The journal is
        // what says which kind this transition is; the checkpoint only says which operation somebody
        // claimed it was, and the whole point of the effect table is to be the one place those two are
        // made to agree.
        IGrimoireOfflineTransitionEffectHandler? effect = null;

        CovenantErasureProgress progress = new(state.Phase);

        CovenantVerifiedCandidateState candidate;

        bool resumedAtCanonical = false;

        try
        {

            // Quiescing is not a phase. It is idempotent, it writes nothing, and it has to happen
            // before the first artifact is touched: the disclosure writer is the one component still
            // able to append after admission closed, and a receipt appended over a row this erasure is
            // deleting would outlive the thing it describes.
            Result quiesced = await _disclosureWriter.QuiesceAsync(cancellationToken).ConfigureAwait(false);

            // The journal records what the quiesce just achieved. Closing is entered first and proved
            // second, because the two are separate facts a crash can fall between: entering says this
            // transition intends to stop ordinary access, and the proof says nothing can still race it.
            //
            // It stops at Closing rather than going on to Applying. A rollback proved to have touched
            // no storage is only legal from here, so a transition that entered the phase ladder before
            // it had a phase to run would have given that edge up for nothing.
            if (quiesced.IsSuccess)
            {

                // Authority order, and the order matters: the held installation lock, then the
                // validated launch binding, then the exact Covenant lease, then the journal. A
                // journal opened before the lease would be durable authority for an operation that
                // had not yet proved it owns the thing it is about to erase.
                Result<GrimoireOfflineTransitionPhaseSession> opened = await phaseAuthority
                    .OpenOrResumeAsync(operation, cancellationToken)
                    .ConfigureAwait(false);

                if (opened.IsSuccess)
                {

                    phases = opened.Value;

                    Result<IGrimoireOfflineTransitionEffectHandler> resolved = _effects.Resolve(
                        phases.Binding.Kind,
                        phases.Binding.PayloadVersion);

                    if (resolved.IsFailure)
                    {

                        throw new CovenantErasureStepFailedException(resolved.Error);

                    }

                    effect = resolved.Value;

                    // The operation restriction, re-imposed where the journal is the authority. Both
                    // entry points already refuse a checkpoint whose operation is not theirs, but that
                    // is a caller checking a caller; this is the durable record naming a kind, the
                    // table saying which operation that kind is, and the claim being refused when they
                    // disagree.
                    if (effect.Operation != state.Operation
                        || effect.Operation != phases.Launch.Operation)
                    {

                        throw new CovenantErasureStepFailedException(
                            new Error(
                                ErrorCodes.Covenant.InvalidScope,
                                "An authenticated offline transition names a kind whose effect is not "
                                + "the operation this run claims to be."));

                    }

                    // The journal says how far this transition got, not the row. The row records what
                    // was launched and stops there, so resuming from it would restart a transition
                    // that had already replaced a family.
                    state = state with { Phase = phases.LastCompletedPhase };

                    progress = new CovenantErasureProgress(state.Phase);

                    // Read from the journal rather than the row. The row records the launch and stops
                    // there, so it always says the first phase - and a flag derived from it would be
                    // false on exactly the runs it exists to be true on.
                    resumedAtCanonical = state.Phase == CovenantResetPhase.CanonicalApplied;

                    quiesced = await RecordClosedAsync(phases, cancellationToken)
                        .ConfigureAwait(false);

                    if (quiesced.IsSuccess)
                    {

                        // The Grimoire's own closure is the last item of the authority order, and it
                        // is held for the whole closed period: it is what every database open below
                        // is performed under, and a second closure part-way through would invalidate
                        // the authority the phases before it already ran with.
                        Result<CovenantGrimoireClosure> grimoire = await CloseGrimoireAsync(
                            checkpoint.Owner,
                            lease,
                            cancellationToken).ConfigureAwait(false);

                        if (grimoire.IsSuccess)
                        {

                            closure = grimoire.Value;

                            stranded.Closure = closure;

                            maintenance = new CovenantClosedPeriodAuthority(
                                checkpoint.Owner.OperationId,
                                closure.Closed,
                                closure.Lane,
                                _maintenanceConnections,
                                _maintenancePaths,
                                _passphrase);

                        }
                        else
                        {

                            quiesced = Result.Failure(grimoire.Error);

                        }

                    }

                }
                else
                {

                    quiesced = Result.Failure(opened.Error);

                }

            }

            if (quiesced.IsFailure)
            {

                return await AbortBeforeErasureAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    progress,
                    phases,
                    closure,
                    quiesced.Error).ConfigureAwait(false);

            }

            // The pre-canonical preflight asks a question about the family this transition is about
            // to replace. Once the replacement may have happened - which is exactly what an in-flight
            // canonical publication says - that family is gone, and asking anyway would refuse a
            // resumed run for having already done the thing it was resuming to finish. So a journal
            // that names the canonical phase in flight takes the resumed path instead, where the
            // exposure is read on its own and the replacement is replayed to converge.
            if (maintenance is null || phases is null || effect is null)
            {

                throw new CovenantErasureStepFailedException(MaintenanceFailure());

            }

            if (state.Phase == CovenantResetPhase.InventoryPrepared
                && phases?.InFlightPhase is not CovenantResetPhase.CanonicalApplied)
            {

                Result<CovenantErasureInventorySummary> inventory = await _inventory
                    .PreflightBeforeCanonicalAsync(
                        state.Operation,
                        datasetGeneration,
                        maintenance,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (inventory.IsFailure)
                {

                    return await AbortBeforeErasureAsync(
                        operation,
                        state,
                        ownerId,
                        lease,
                        progress,
                        phases,
                        closure,
                        inventory.Error).ConfigureAwait(false);

                }

                progress.Exposure = inventory.Value.Exposure;

            }
            else
            {

                Result<CovenantDisclosureExposure> exposure = await _inventory
                    .ReadDisclosureExposureAsync(maintenance, cancellationToken)
                    .ConfigureAwait(false);

                if (exposure.IsFailure)
                {

                    throw new CovenantErasureStepFailedException(exposure.Error);

                }

                progress.Exposure = exposure.Value;

                if (resumedAtCanonical || phases?.InFlightPhase is CovenantResetPhase.CanonicalApplied)
                {

                    Result managedPreflight = await _inventory
                        .PreflightRemainingManagedAsync(maintenance, cancellationToken)
                        .ConfigureAwait(false);

                    if (managedPreflight.IsFailure)
                    {

                        throw new CovenantErasureStepFailedException(managedPreflight.Error);

                    }

                }

            }

            // The journal and the Grimoire closure both exist from here on: the quiesce succeeded,
            // the opening publication is durable, and ordinary admission is shut for a generation
            // this run owns. Those are the preconditions every phase below is published and performed
            // against, and a run that reached this point without them has no authority to erase.
            if (phases is null || maintenance is null)
            {

                throw new CovenantErasureStepFailedException(MaintenanceFailure());

            }

            state = await AdvanceAsync(
                phases,
                state,
                CovenantResetPhase.CanonicalApplied,
                async (_, token) =>
                {

                    Result erased = await WithLedgerAsync(
                        closure,
                        ledgerToken => EraseDatabaseArtifactsAsync(
                            datasetGeneration,
                            maintenance,
                            authority,
                            progress,
                            ledgerToken),
                        token).ConfigureAwait(false);

                    if (erased.IsFailure)
                    {

                        return Result.Failure(erased.Error);

                    }

                    progress.EffectAttempted = true;

                    Result<Guid> applied = await _transition
                        .ApplyCanonicalErasureAsync(state.Operation, state.Dataset, maintenance, token)
                        .ConfigureAwait(false);

                    return applied.IsFailure ? Result.Failure(applied.Error) : Result.Success();

                },
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                phases,
                state,
                CovenantResetPhase.ManagedArtifactsProcessed,
                (_, token) => WithLedgerAsync(
                    closure,
                    ledgerToken => EraseManagedFilesAsync(
                        operation.Id,
                        maintenance,
                        authority,
                        progress,
                        ledgerToken),
                    token),
                progress,
                cancellationToken).ConfigureAwait(false);

            // What this kind owes the installation beyond the ladder is the effect handler's to say,
            // and whether it has already been paid is the journal's. Neither is inferred here: the
            // phase window cannot tell a run that completed the continuation from one that crashed
            // before starting it, because both sit at the same phase.
            Result continued = await effect.RunOrdinaryContinuationAsync(
                new GrimoireOfflineTransitionEffectContext(
                    phases,
                    factoryContinuation,
                    (work, token) => WithLedgerAsync(closure, work, token)),
                progress,
                cancellationToken).ConfigureAwait(false);

            if (continued.IsFailure)
            {

                throw new CovenantErasureStepFailedException(continued.Error);

            }

            state = await AdvanceAsync(
                phases,
                state,
                CovenantResetPhase.HandlesClosed,
                (_, token) => _transition.CloseHandlesAsync(maintenance, token),
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                phases,
                state,
                CovenantResetPhase.WalTruncated,
                (_, token) => _transition.TruncateWalAsync(maintenance, token),
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await CompactAsync(
                phases,
                state,
                maintenance,
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                phases,
                state,
                CovenantResetPhase.AcceleratorInitialized,
                (_, token) => _transition.InitializeAcceleratorAsync(maintenance, token),
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                phases,
                state,
                CovenantResetPhase.FinalWalTruncated,
                (_, token) => _transition.TruncateWalAsync(maintenance, token),
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                phases,
                state,
                CovenantResetPhase.SidecarsVerified,
                (_, token) => _transition.VerifySidecarAbsenceAsync(maintenance, token),
                progress,
                cancellationToken).ConfigureAwait(false);

            Result<CovenantVerifiedCandidateState> verified =
                await _transition.VerifyReopenAsync(maintenance, cancellationToken).ConfigureAwait(false);

            if (verified.IsFailure)
            {

                throw new CovenantErasureStepFailedException(verified.Error);

            }

            candidate = verified.Value;

        }
        catch (CovenantErasureStepFailedException failed)
        {

            // The refusing step's own sentence travels with the code. One code covers several steps —
            // MaintenanceFailed alone is emitted by the drain and by three points of the canonical
            // transaction — so a phase and a code together still leave the reader guessing which one
            // let go, and this warning is the only place either is written down.
            _logger.LogWarning(
                "A Covenant erasure stopped at phase {ResetPhase} for durable operation {OperationId} "
                + "with {ErrorCode}: {ErrorMessage}; admission stays closed.",
                state.Phase,
                operation.Id,
                failed.Error.Code,
                failed.Error.Message);

            return progress.EffectAttempted || progress.DurablyMutated
                ? await CloseAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    CovenantExclusiveLeaseDisposition.KeepClosed,
                    progress,
                    failed.Error.Code,
                        phases,
                        closure).ConfigureAwait(false)
                : await AbortBeforeErasureAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    progress,
                    phases,
                    closure,
                    failed.Error).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            Error interrupted = MaintenanceFailure();

            return progress.EffectAttempted || progress.DurablyMutated
                ? await CloseAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    CovenantExclusiveLeaseDisposition.KeepClosed,
                    progress,
                    interrupted.Code,
                        phases,
                        closure).ConfigureAwait(false)
                : await AbortBeforeErasureAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    progress,
                    phases,
                    closure,
                    interrupted).ConfigureAwait(false);

        }
        catch (DataRetentionLeaseLostException)
        {

            throw;

        }
        catch (Exception)
        {

            Error interrupted = MaintenanceFailure();

            return progress.EffectAttempted || progress.DurablyMutated
                ? await CloseAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    CovenantExclusiveLeaseDisposition.KeepClosed,
                    progress,
                    interrupted.Code,
                        phases,
                        closure).ConfigureAwait(false)
                : await AbortBeforeErasureAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    progress,
                    phases,
                    closure,
                    interrupted).ConfigureAwait(false);

        }

        // The caller no longer owns cancellation after the immutable proof succeeds. The checkpoint,
        // publication, and writer restart share one bounded lifecycle token because all three must
        // finish before the separately bounded disposition decides whether admission reopens.
        using CancellationTokenSource publicationAndWriter =
            new(PublicationAndWriterBound, _timeProvider);

        try
        {

            // ReopenedVerified records that a proof succeeded, not the proof object itself, and a
            // resumed pass has no value to recover from it either way — so it repeats the immutable
            // verification rather than trusting a recorded phase. The ordering is still checked,
            // because a run that reached this point out of order is a run whose earlier phases did
            // not happen.
            if (state.Phase < CovenantResetPhase.ReopenedVerified)
            {

                Result ordered = CovenantResetPhaseMachine.RequireAdvance(
                    state.Phase,
                    CovenantResetPhase.ReopenedVerified);

                if (ordered.IsFailure)
                {

                    throw new CovenantErasureStepFailedException(ordered.Error);

                }

                state = state with { Phase = CovenantResetPhase.ReopenedVerified };

            }

        }
        catch (CovenantErasureStepFailedException failed)
        {

            _logger.LogWarning(
                "A Covenant erasure stopped at phase {ResetPhase} for durable operation {OperationId} "
                + "with {ErrorCode}: {ErrorMessage}; admission stays closed.",
                state.Phase,
                operation.Id,
                failed.Error.Code,
                failed.Error.Message);

            return await CloseAsync(
                operation,
                state,
                ownerId,
                lease,
                CovenantExclusiveLeaseDisposition.KeepClosed,
                progress,
                failed.Error.Code,
                    phases,
                    closure).ConfigureAwait(false);

        }
        catch (Exception)
        {

            Error interrupted = MaintenanceFailure();

            return await CloseAsync(
                operation,
                state,
                ownerId,
                lease,
                CovenantExclusiveLeaseDisposition.KeepClosed,
                progress,
                interrupted.Code,
                    phases,
                    closure).ConfigureAwait(false);

        }

        // The erasure is proven. Everything below can still fail, and none of it can make the erasure
        // untrue — which is exactly why the storage fact and the publication fact are reported
        // separately rather than folded into one outcome.
        progress.LocalSecureErasureComplete = true;

        // Publication happens while the gate is still held. Reopening first would leave a window in
        // which new work could be authorized under keys this erasure just invalidated.

        Result published = await RunLifecycleAsync(
            token => _transition.PublishCommittedAsync(lease, candidate, token),
            publicationAndWriter.Token).ConfigureAwait(false);

        if (published.IsFailure)
        {

            _logger.LogWarning(
                "The Covenant authority transition for durable operation {OperationId} did not publish; "
                + "old contexts stay unusable and admission stays closed.",
                operation.Id);

            return await CloseAsync(
                operation,
                state,
                ownerId,
                lease,
                CovenantExclusiveLeaseDisposition.KeepClosed,
                progress,
                published.Error.Code,
                    phases,
                    closure).ConfigureAwait(false);

        }

        // Ordinary admission reopens here, before anything ordinary is asked to come back. The warm
        // writer opens an ordinary connection, and an ordinary connection is exactly what a closed
        // Grimoire refuses - so a writer restarted inside the closed period cannot succeed, and its
        // refusal would be reported instead of whatever the erasure actually did.
        //
        // Reopening now is safe and is not the same decision as the Covenant disposition below. The
        // storage proof has passed and the runtime authority is published, so there is nothing left
        // that a shut database is protecting; what a failed writer restart still costs is the
        // Covenant scope, which stays closed on its own terms.
        if (closure is not null)
        {

            Result reopened = await closure.ReleaseAsync(
                CovenantExclusiveLeaseDisposition.CommitAndReopen,
                publicationAndWriter.Token).ConfigureAwait(false);

            if (reopened.IsFailure)
            {

                return await CloseAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    CovenantExclusiveLeaseDisposition.KeepClosed,
                    progress,
                    reopened.Error.Code,
                    phases,
                    closure).ConfigureAwait(false);

            }

        }

        // The warm writer may only come back against the authority that was just published. A restart
        // failure lands here, before the one disposition, so it selects KeepClosed rather than
        // reversing an erasure the storage proof already earned.
        Result writer = await RunLifecycleAsync(
            token => _disclosureWriter.ReopenAsync(token).AsTask(),
            publicationAndWriter.Token).ConfigureAwait(false);

        if (writer.IsFailure)
        {

            return await CloseAsync(
                operation,
                state,
                ownerId,
                lease,
                CovenantExclusiveLeaseDisposition.KeepClosed,
                progress,
                writer.Error.Code,
                    phases,
                    closure).ConfigureAwait(false);

        }

        CovenantExclusiveLeaseDisposition disposition = CovenantExclusiveDisposition.Select(
            new CovenantExclusiveDispositionEvidence(
                StorageVerified: true,
                AuthorityVerified: true,
                DurablyMutated: progress.DurablyMutated,
                HealthPublished: true));

        return await CloseAsync(
            operation,
            state,
            ownerId,
            lease,
            disposition,
            progress,
            blockingErrorCode: null,
            phases,
            closure).ConfigureAwait(false);

    }

    /// <summary>
    /// The one shape of failure that may reopen: nothing was attempted, so nothing can be half done.
    /// </summary>
    /// <remarks>
    /// A quiesce or inventory failure happens before the first kernel call, so storage is provably
    /// untouched and <see cref="CovenantExclusiveDisposition.Select"/> answers
    /// <c>RollbackAndReopen</c> on its own. Everything from the first erased artifact onwards is a
    /// durable mutation, and the same function then answers <c>KeepClosed</c> — which is why the
    /// decision is made by the shared evidence function rather than picked by hand here.
    /// </remarks>
    private async Task<Result<CovenantErasureCompletion>> AbortBeforeErasureAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId,
        CovenantExclusiveLease lease,
        CovenantErasureProgress progress,
        GrimoireOfflineTransitionPhaseSession? phases,
        CovenantGrimoireClosure? closure,
        Error error)
    {

        _logger.LogWarning(
            "A Covenant erasure aborted before any artifact was touched with {ErrorCode}; admission reopens.",
            error.Code);

        using CancellationTokenSource restoration = new(WriterRestorationBound, _timeProvider);

        // Ordinary admission reopens before the writer is asked to come back, for the same reason it
        // does on the committed path: the writer opens an ordinary connection and a closed Grimoire
        // refuses one. Nothing here has been touched - this is the pre-effect abort - so there is
        // nothing a shut database would be protecting, and leaving it shut would replace the reason
        // this erasure stopped with the refusal that hid it.
        if (closure is not null)
        {

            _ = await closure.ReleaseAsync(
                CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                restoration.Token).ConfigureAwait(false);

        }

        Result restored;

        try
        {

            restored = await _disclosureWriter
                .ReopenAsync(restoration.Token)
                .ConfigureAwait(false);

        }
        catch (Exception)
        {

            restored = Result.Failure(MaintenanceFailure());

        }

        CovenantExclusiveLeaseDisposition disposition = restored.IsSuccess
            ? CovenantExclusiveDisposition.Select(
                new CovenantExclusiveDispositionEvidence(
                    StorageVerified: true,
                    AuthorityVerified: true,
                    DurablyMutated: progress.DurablyMutated,
                    HealthPublished: false))
            : CovenantExclusiveLeaseDisposition.KeepClosed;

        return await CloseAsync(
            operation,
            checkpoint,
            ownerId,
            lease,
            disposition,
            progress,
            restored.IsSuccess ? error.Code : ErrorCodes.Covenant.MaintenanceFailed,
            phases,
            closure).ConfigureAwait(false);

    }

    private async Task<Result> EraseDatabaseArtifactsAsync(
        Guid datasetGeneration,
        CovenantClosedPeriodAuthority maintenance,
        CovenantArtifactErasureAuthority authority,
        CovenantErasureProgress progress,
        CancellationToken cancellationToken)
    {

        Guid? cursor = null;

        while (true)
        {

            Result<CovenantDatabaseErasureBatch> batch = await _inventory
                .ReadNextDatabaseBatchAsync(datasetGeneration, cursor, maintenance, cancellationToken)
                .ConfigureAwait(false);

            if (batch.IsFailure)
            {

                return Result.Failure(batch.Error);

            }

            if (!batch.Value.IsComplete && batch.Value.NextCursor == cursor)
            {

                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "A bounded Covenant database inventory page did not advance its cursor."));

            }

            if (batch.Value.Page is { } page)
            {

                progress.EffectAttempted = true;

                Result<CovenantArtifactErasureProgress> erased = await _artifacts
                    .ErasePageAsync(page, authority, cancellationToken)
                    .ConfigureAwait(false);

                if (erased.IsFailure)
                {

                    return Result.Failure(erased.Error);

                }

                if (erased.Value.ErasedCount > 0)
                {

                    progress.DurablyMutated = true;

                }

                if (erased.Value.IsBlocked)
                {

                    return Result.Failure(
                        new Error(
                            ErrorCodes.Covenant.ManualArtifactErasureRequired,
                            "A protected artifact could not be erased, so the canonical erasure did not run."));

                }

            }

            if (batch.Value.IsComplete)
            {

                return Result.Success();

            }

            cursor = batch.Value.NextCursor;

        }

    }

    private async Task<Result> EraseManagedFilesAsync(
        Guid operationId,
        CovenantClosedPeriodAuthority maintenance,
        CovenantArtifactErasureAuthority authority,
        CovenantErasureProgress progress,
        CancellationToken cancellationToken)
    {

        Guid? cursor = null;

        while (true)
        {

            Result<CovenantManagedFileErasureBatch> batch = await _inventory
                .ReadNextManagedFileBatchAsync(operationId, cursor, maintenance, cancellationToken)
                .ConfigureAwait(false);

            if (batch.IsFailure)
            {

                return Result.Failure(batch.Error);

            }

            if (!batch.Value.IsComplete && batch.Value.NextCursor == cursor)
            {

                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "A bounded Covenant managed inventory page did not advance its cursor."));

            }

            foreach (CovenantManagedFileErasureRequest file in batch.Value.Requests)
            {

                progress.EffectAttempted = true;

                Result<CovenantArtifactErasureProgress> erased = await _managedFiles
                    .EraseAsync(file, authority, cancellationToken)
                    .ConfigureAwait(false);

                if (erased.IsFailure)
                {

                    return Result.Failure(erased.Error);

                }

                if (erased.Value.ErasedCount > 0)
                {

                    progress.DurablyMutated = true;

                }

                if (erased.Value.IsBlocked)
                {

                    return Result.Failure(
                        new Error(
                            ErrorCodes.Covenant.ManualArtifactErasureRequired,
                            "A managed workspace file could not be erased, so local erasure is incomplete."));

                }

            }

            if (batch.Value.IsComplete)
            {

                return Result.Success();

            }

            cursor = batch.Value.NextCursor;

        }

    }

    /// <summary>
    /// Performs one phase's step exactly once and records it, or returns the checkpoint untouched.
    /// </summary>
    /// <remarks>
    /// The guard is the whole resume contract: a phase already recorded is a step already committed,
    /// and re-running it would repeat an effect the filesystem cannot roll back. The ordering itself
    /// is checked by <see cref="CovenantResetPhaseMachine.RequireAdvance"/> rather than by a
    /// comparison written here, because "is this the next phase" has three silent wrong answers and
    /// the phase machine is the only reader that knows all of them.
    /// </remarks>
    private readonly GrimoireOfflineTransitionDatabaseReconciler _reconciler =
        reconciler ?? throw new ArgumentNullException(nameof(reconciler));

    /// <summary>
    /// What the reconciliation suffix reached, which decides whether anything may be retired.
    /// </summary>
    /// <remarks>
    /// A parked transition is an outcome rather than a fault, and it is the one the caller must not
    /// confuse with success: the journal is retained deliberately, so retiring it would discard the
    /// only durable statement of where the erasure stopped.
    /// </remarks>
    private enum ReconciliationSuffix
    {

        /// <summary>No journal was ever opened, so there is nothing to retain or retire.</summary>
        NoJournal = 1,

        /// <summary>The suffix is complete and the journal may retire once the disposition is spent.</summary>
        Retirable = 2,

        /// <summary>The journal is retained, so the gate must stay closed to agree with it.</summary>
        Parked = 3,

    }

    /// <summary>
    /// Publishes that ordinary access is stopped, in the two revisions that takes.
    /// </summary>
    /// <remarks>
    /// Two rather than one because entering and proving are separate facts a crash can fall between,
    /// and the graph refuses a payload that carries more than the edge it is on owns. A resumed run
    /// that is already past this skips both.
    ///
    /// <para>It stops at closing rather than going on to the phase ladder. A rollback proved to have
    /// touched no storage is only legal from there, so a transition that entered the ladder before it
    /// had a phase to run would have given that edge up for nothing.</para>
    /// </remarks>
    private static async Task<Result> RecordClosedAsync(
        GrimoireOfflineTransitionPhaseSession phases,
        CancellationToken cancellationToken)
    {

        // A park is where the last attempt stopped, not where this transition ends. Lifting it first
        // puts the journal back at the state it was parked from, which is the state the rest of this
        // run is written against; leaving it parked would make every step below refuse an edge that
        // is only illegal because nobody ever came back for it.
        if (phases.State is GrimoireOfflineTransitionState.KeepClosed)
        {

            Result resumed = await phases.ResumeFromParkAsync(cancellationToken).ConfigureAwait(false);

            if (resumed.IsFailure)
            {

                return resumed;

            }

        }

        // Closing is two publications, and a run can die between them. A journal left in Closing with
        // an incomplete proof has exactly one legal edge - the closing advance itself - because every
        // other edge out of Closing requires the proof to be complete. So a resumed run finishes the
        // proof rather than treating the state as evidence that it was already made; skipping it
        // would leave a transition that can never be advanced, rolled back, parked, or retired, on an
        // installation whose admission stays shut.
        if (phases.State is GrimoireOfflineTransitionState.Closing && !phases.ClosingProofIsComplete)
        {

            return await phases.RecordClosedAsync(cancellationToken).ConfigureAwait(false);

        }

        if (phases.State is not GrimoireOfflineTransitionState.Prepared)
        {

            return Result.Success();

        }

        Result entered = await phases.EnterClosingAsync(cancellationToken).ConfigureAwait(false);

        return entered.IsFailure
            ? entered
            : await phases.RecordClosedAsync(cancellationToken).ConfigureAwait(false);

    }

    private async Task<CovenantErasureCheckpointState> AdvanceAsync(
        GrimoireOfflineTransitionPhaseSession phases,
        CovenantErasureCheckpointState checkpoint,
        CovenantResetPhase phase,
        Func<CovenantErasureCheckpointState, CancellationToken, Task<Result>> step,
        CovenantErasureProgress progress,
        CancellationToken cancellationToken)
    {

        if (checkpoint.Phase >= phase)
        {

            return checkpoint;

        }

        Result ordered = CovenantResetPhaseMachine.RequireAdvance(checkpoint.Phase, phase);

        if (ordered.IsFailure)
        {

            throw new CovenantErasureStepFailedException(ordered.Error);

        }

        // The phase ladder is entered by the first phase that actually runs, not by the closing proof
        // that preceded it, because the rollback edge a pre-effect abort needs is only legal from
        // Closing.
        if (phases.State is GrimoireOfflineTransitionState.Closing)
        {

            Result applying = await phases.EnterApplyingAsync(cancellationToken).ConfigureAwait(false);

            if (applying.IsFailure)
            {

                throw new CovenantErasureStepFailedException(applying.Error);

            }

        }

        await FaultAsync(
            CovenantErasureFaultBoundary.BeforePhaseBegin,
            phase,
            cancellationToken).ConfigureAwait(false);

        // A journal that already names this phase in flight is a crash between the two publications.
        // The record it left is exactly the record this run was about to write, and writing it again
        // is an edge the graph refuses - rightly, since it would be claiming to have started
        // something that had already started. So the publication is skipped and the effect is run
        // again, which is safe for every phase by construction: each one is either idempotent in
        // itself, or - for the canonical replacement - matched on the exact source tuple it was
        // planned against, so a replay finds the row already moved and converges instead of moving it
        // twice. That is what the in-flight record is for: it says the effect may have happened, and
        // running it again is how a resumed run finds out without having to be told.
        if (phases.InFlightPhase is { } inFlight)
        {

            if (inFlight != phase)
            {

                throw new CovenantErasureStepFailedException(
                    new Error(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        "The offline transition journal names a different phase in flight."));

            }

        }
        else
        {

            // Published before the effect and again after it. A crash before the first means the
            // effect may not have begun; a crash after it never permits assuming it did, which is the
            // whole reason the pair cannot be one write.
            Result begun = await phases.BeginPhaseAsync(phase, cancellationToken)
                .ConfigureAwait(false);

            if (begun.IsFailure)
            {

                throw new CovenantErasureStepFailedException(begun.Error);

            }

        }

        await FaultAsync(
            CovenantErasureFaultBoundary.AfterPhaseBegin,
            phase,
            cancellationToken).ConfigureAwait(false);

        Result performed = await step(checkpoint, cancellationToken).ConfigureAwait(false);

        if (performed.IsFailure)
        {

            throw new CovenantErasureStepFailedException(performed.Error);

        }

        await FaultAsync(
            CovenantErasureFaultBoundary.AfterPhaseEffect,
            phase,
            cancellationToken).ConfigureAwait(false);

        if (phase == CovenantResetPhase.CanonicalApplied)
        {

            progress.CanonicalResetApplied = true;

            progress.DurablyMutated = true;

        }

        Result completed = await phases.CompletePhaseAsync(phase, cancellationToken)
            .ConfigureAwait(false);

        if (completed.IsFailure)
        {

            throw new CovenantErasureStepFailedException(completed.Error);

        }

        await FaultAsync(
            CovenantErasureFaultBoundary.AfterPhaseComplete,
            phase,
            cancellationToken).ConfigureAwait(false);

        return checkpoint with { Phase = phase };

    }

    /// <summary>
    /// Compacts, staging and publishing a replacement first when compaction alone cannot prove itself.
    /// </summary>
    /// <remarks>
    /// Everything a replacement is planned against is published before the phase begins, because the
    /// transition graph admits a replacement advance only from a completed write-ahead-log truncation
    /// with nothing in flight. That ordering is not a convenience: it is what makes the one
    /// irreversible act in this whole ladder bracketed by a phase whose before-state already names the
    /// file being replaced, the file replacing it, and what that file holds.
    ///
    /// <para>So the shape is stage, prove, publish, begin, install — and a resumed run re-enters at
    /// whichever of those it had reached, keyed on the journal rather than on the directory. A
    /// directory alone cannot distinguish a candidate this transition wrote from one it did not, an
    /// install that completed from one that never started, or a database that was compacted from one
    /// that was replaced; the journal can, and every arm below is an answer it gives.</para>
    /// </remarks>
    private async Task<CovenantErasureCheckpointState> CompactAsync(
        GrimoireOfflineTransitionPhaseSession phases,
        CovenantErasureCheckpointState checkpoint,
        CovenantClosedPeriodAuthority maintenance,
        CovenantErasureProgress progress,
        CancellationToken cancellationToken)
    {

        // Only while the phase is still ahead and has not begun. A checkpoint that already records
        // the phase has had this whole sequence run once, and staging again would export a second
        // candidate over a database that was already replaced; and once the phase is in flight the
        // graph refuses every publication below, so a run that tried them would turn a resumable stop
        // into an unresumable one.
        if (checkpoint.Phase < CovenantResetPhase.DatabaseCompacted && phases.InFlightPhase is null)
        {

            await StageReplacementAsync(phases, maintenance, cancellationToken).ConfigureAwait(false);

        }

        GrimoireOfflineTransitionReplacementEvidence? replacement = phases.ReplacementEvidence;

        return await AdvanceAsync(
            phases,
            checkpoint,
            CovenantResetPhase.DatabaseCompacted,
            (_, token) => InstallReplacementAsync(maintenance, replacement, token),
            progress,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Carries the replacement from wherever the journal left it to a published, proven candidate.
    /// </summary>
    /// <remarks>
    /// One step per call of the loop, and each step publishes before the next one reads: the journal
    /// is what the next arm is chosen by, so a step that acted twice before recording once would leave
    /// a resumed run choosing an arm for work that had already happened.
    ///
    /// <para>The identity the plan was made against is re-established at every arm. A canonical
    /// database that is no longer the file the plan named is not a database this plan says anything
    /// about, and continuing would install a candidate exported from one file over a different one.</para>
    /// </remarks>
    private async Task StageReplacementAsync(
        GrimoireOfflineTransitionPhaseSession phases,
        CovenantClosedPeriodAuthority maintenance,
        CancellationToken cancellationToken)
    {

        if (phases.ReplacementEvidence is null)
        {

            Result<bool> compacted = await _transition
                .CompactAsync(maintenance, cancellationToken)
                .ConfigureAwait(false);

            if (compacted.IsFailure)
            {

                throw new CovenantErasureStepFailedException(compacted.Error);

            }

            if (!compacted.Value)
            {

                return;

            }

            Result<CovenantDigest> canonical = await _transition
                .ReadCanonicalIdentityAsync(maintenance, cancellationToken)
                .ConfigureAwait(false);

            if (canonical.IsFailure)
            {

                throw new CovenantErasureStepFailedException(canonical.Error);

            }

            // The same identity three times over, and deliberately so. A compaction replaces a
            // database with a compaction of itself, so at planning time the file the candidate is
            // exported from, the file it will be installed over, and the original a recovery would
            // have to put back are one file. They are recorded separately because they stop being one
            // the instant the install runs — the destination becomes a new file and the original
            // survives only as whatever the atomic primitive kept — and because the same evidence
            // serves a restore, where the three are different files from the start.
            Require(
                await phases.RecordReplacementPlannedAsync(
                    maintenance.ExportStagingLeaf,
                    canonical.Value,
                    canonical.Value,
                    canonical.Value,
                    cancellationToken).ConfigureAwait(false));

        }

        GrimoireOfflineTransitionReplacementEvidence planned =
            await RequirePlannedAsync(phases, maintenance, cancellationToken).ConfigureAwait(false);

        if (planned.StagingPhysicalIdentityDigest is null)
        {

            Result<CovenantDigest> staged = await _transition
                .StageCandidateAsync(maintenance, cancellationToken)
                .ConfigureAwait(false);

            if (staged.IsFailure)
            {

                throw new CovenantErasureStepFailedException(staged.Error);

            }

            Require(
                await phases.RecordStagingIdentityAsync(staged.Value, cancellationToken)
                    .ConfigureAwait(false));

            planned = await RequirePlannedAsync(phases, maintenance, cancellationToken)
                .ConfigureAwait(false);

        }

        if (planned is not { StagingPhysicalIdentityDigest: { } stagingIdentity, StagedContentDigest: null })
        {

            return;

        }

        Result<CovenantDigest> proven = await _transition.ProveStagedCandidateAsync(
            maintenance,
            stagingIdentity,
            cancellationToken).ConfigureAwait(false);

        if (proven.IsFailure)
        {

            throw new CovenantErasureStepFailedException(proven.Error);

        }

        Require(
            await phases.RecordStagedContentAsync(proven.Value, cancellationToken)
                .ConfigureAwait(false));

    }

    /// <summary>
    /// Performs the compaction phase's own effect: install the proven candidate, or nothing at all.
    /// </summary>
    /// <remarks>
    /// Nothing at all is the ordinary outcome. A healthy engine's own accounting usually proves the
    /// freed pages gone, and in that case no replacement was ever planned and this phase brackets a
    /// measurement that has already happened.
    ///
    /// <para>Whether an install has already landed is not decided here. That question is about files
    /// — which one is at the staging path, and what the destination holds — and it is answered where
    /// every other question about files is, under the same closed-period authority. What is decided
    /// here is the one thing the journal knows and the storage layer does not: whether this transition
    /// ever committed to a replacement at all.</para>
    /// </remarks>
    private Task<Result> InstallReplacementAsync(
        CovenantClosedPeriodAuthority maintenance,
        GrimoireOfflineTransitionReplacementEvidence? replacement,
        CancellationToken cancellationToken) =>
        replacement switch
        {

            null => Task.FromResult(Result.Success()),

            {
                StagingPhysicalIdentityDigest: { } stagingIdentity,
                StagedContentDigest: { } stagedContent,
            } => _transition.InstallCompactionReplacementAsync(
                maintenance,
                stagingIdentity,
                stagedContent,
                replacement.DestinationPhysicalIdentityDigest,
                cancellationToken),

            // A replacement that reached the phase without its proof is one the publication sequence
            // stopped part-way through and something began the phase over the top of. Installing it
            // would install a candidate nobody proved; retrying the publications is refused by the
            // graph now the phase has begun. Neither is available, so neither is attempted.
            _ => Task.FromResult(
                Result.Failure(
                    Ambiguous("a compaction replacement reached its phase without the evidence to install it"))),

        };

    /// <summary>Re-reads the plan and refuses if the file it was made against is no longer that file.</summary>
    private async Task<GrimoireOfflineTransitionReplacementEvidence> RequirePlannedAsync(
        GrimoireOfflineTransitionPhaseSession phases,
        CovenantClosedPeriodAuthority maintenance,
        CancellationToken cancellationToken)
    {

        if (phases.ReplacementEvidence is not { } planned)
        {

            throw new CovenantErasureStepFailedException(
                Ambiguous("a compaction replacement was recorded and then read back as absent"));

        }

        if (!string.Equals(planned.StagingLeaf, maintenance.ExportStagingLeaf, StringComparison.Ordinal))
        {

            throw new CovenantErasureStepFailedException(
                Ambiguous("a compaction replacement names a candidate belonging to another operation"));

        }

        Result<CovenantDigest> canonical = await _transition
            .ReadCanonicalIdentityAsync(maintenance, cancellationToken)
            .ConfigureAwait(false);

        if (canonical.IsFailure)
        {

            throw new CovenantErasureStepFailedException(canonical.Error);

        }

        if (canonical.Value != planned.SourcePhysicalIdentityDigest)
        {

            throw new CovenantErasureStepFailedException(
                Ambiguous("the database a compaction was planned against is not the one in place"));

        }

        return planned;

    }

    /// <summary>Turns a refused publication into the step failure that leaves admission closed.</summary>
    private static void Require(Result published)
    {

        if (published.IsFailure)
        {

            throw new CovenantErasureStepFailedException(published.Error);

        }

    }

    /// <summary>
    /// The refusal every arm above shares: two readings of the same directory, and no way to choose.
    /// </summary>
    /// <remarks>
    /// Manual recovery rather than a retry, because every one of these says the world stopped matching
    /// what this transition recorded about it. Running the ladder again would act on that world under
    /// evidence that describes a different one, and the whole reason the evidence is in the journal is
    /// so that nobody does.
    /// </remarks>
    private static Error Ambiguous(string detail) =>
        new(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            $"A Covenant erasure cannot resume its compaction: {detail}. An operator has to establish "
            + "which database is in place before anything else runs.");

    /// <summary>
    /// Raises the injected fault for one boundary, as the step failure a real crash would look like.
    /// </summary>
    /// <remarks>
    /// Thrown rather than returned, so an injected stop takes exactly the path a genuine step failure
    /// takes and cannot be handled by a branch that only exists because the fault was injected. In
    /// production the delegate is a no-op and this is one already-completed task per boundary.
    /// </remarks>
    private async Task FaultAsync(
        CovenantErasureFaultBoundary boundary,
        CovenantResetPhase phase,
        CancellationToken cancellationToken)
    {

        Result injected = await _faultSeam(boundary, phase, cancellationToken).ConfigureAwait(false);

        if (injected.IsFailure)
        {

            throw new CovenantErasureStepFailedException(injected.Error);

        }

    }

    /// <summary>
    /// Refuses a checkpoint this build cannot resume, before any scope is closed.
    /// </summary>
    /// <remarks>
    /// The owner identity check is the one that matters: a checkpoint naming a different operation
    /// would mint an owner for a closed scope this operation has no right to adopt. It is checked
    /// before the gate rather than after, because by then the scope is already closed.
    /// </remarks>
    private static Result RequireResumableCheckpoint(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint)
    {

        if (checkpoint.Operation is not CovenantExclusiveOperation.CovenantReset
            and not CovenantExclusiveOperation.HealthyCatalogFactoryErasure)
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "Only a Covenant reset or a healthy-catalog factory erasure enters the erasure coordinator.");

        }

        if (checkpoint.OperationId == Guid.Empty || checkpoint.OperationId != operation.Id)
        {

            return new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "This Covenant erasure checkpoint names a different durable operation.");

        }

        return CovenantResetPhaseMachine.RequireDeclared(checkpoint.Phase);

    }

    /// <summary>
    /// Takes the one reopening decision, on a token this coordinator owns.
    /// </summary>
    /// <remarks>
    /// A failed disposition is reported as itself and never followed by a second one. The gate is
    /// already closed at that point, so a retry with <c>KeepClosed</c> would change nothing an
    /// operator can observe while replacing the real failure with the lease's own
    /// <c>LifecycleConflict</c> — hiding the reason the reset could not reopen.
    /// </remarks>
    private async Task<Result<CovenantErasureCompletion>> CloseAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId,
        CovenantExclusiveLease lease,
        CovenantExclusiveLeaseDisposition disposition,
        CovenantErasureProgress progress,
        string? blockingErrorCode,
        GrimoireOfflineTransitionPhaseSession? phases,
        CovenantGrimoireClosure? closure)
    {

        using CancellationTokenSource lifecycle = new(DispositionBound, _timeProvider);

        // A parked transition keeps its journal. It is the only durable statement of where this
        // erasure stopped, and retiring it to tidy up would discard the evidence the next process
        // needs to decide whether the effects behind it happened.
        // A null session means the transition never opened a journal, because nothing durable had
        // happened yet when it stopped. There is no progress to record and nothing to retire.
        Result<ReconciliationSuffix> suffix = phases is null
            ? Result<ReconciliationSuffix>.Success(ReconciliationSuffix.NoJournal)
            : disposition is CovenantExclusiveLeaseDisposition.KeepClosed
                ? Parked(await phases.ParkAsync(lifecycle.Token).ConfigureAwait(false))
                : await ReconcileBeforeDispositionAsync(phases, disposition, closure, lifecycle.Token)
                    .ConfigureAwait(false);

        if (suffix.IsFailure)
        {

            _logger.LogError(
                "A Covenant erasure could not publish its {Disposition} disposition to the offline "
                + "transition journal ({ErrorCode}); admission stays closed.",
                disposition,
                suffix.Error.Code);

            return Result<CovenantErasureCompletion>.Failure(MaintenanceFailure());

        }

        // A parked journal and a reopened gate would disagree about whether this transition is over,
        // and the journal is the one that survives the process. So a park takes the gate with it: the
        // retained journal is what the next start reads, and it says the erasure is unfinished.
        if (suffix.Value is ReconciliationSuffix.Parked)
        {

            disposition = CovenantExclusiveLeaseDisposition.KeepClosed;

        }

        // The Grimoire's closure is spent first, and on the same answer. The two gates are separate
        // closures over one installation, and a run that reopened either while keeping the other shut
        // would leave an operator with an installation that is neither open nor closed. This one goes
        // first because it is the outer of the two: ordinary admission has to be reopenable before
        // the Covenant scope that closed it lets go.
        if (closure is not null)
        {

            Result reopened = await closure
                .ReleaseAsync(GrimoireDispositionFor(disposition), lifecycle.Token)
                .ConfigureAwait(false);

            if (reopened.IsFailure)
            {

                _logger.LogError(
                    "A Covenant erasure could not spend its {Disposition} disposition on the Grimoire "
                    + "closure ({ErrorCode}); admission stays closed.",
                    disposition,
                    reopened.Error.Code);

                return Result<CovenantErasureCompletion>.Failure(MaintenanceFailure());

            }

        }

        Result closed;

        try
        {

            closed = await lease.CompleteAsync(disposition, lifecycle.Token).ConfigureAwait(false);

        }
        catch (Exception)
        {

            closed = Result.Failure(MaintenanceFailure());

        }

        if (closed.IsFailure)
        {

            Error normalized = MaintenanceFailure();

            // No database status is written to describe this. The journal already says the disposition
            // was in flight and has not been verified, which is both more precise than a status column
            // and readable by a process that cannot open the database at all. Writing one here would
            // also mean reaching into the Grimoire during the closed period, which is the circularity
            // the whole offline-transition arrangement exists to remove.
            _logger.LogError(
                "A Covenant erasure could not record its {Disposition} disposition ({ErrorCode}); "
                + "admission stays closed and the retained journal keeps the operation adoptable.",
                disposition,
                normalized.Code);

            return Result<CovenantErasureCompletion>.Failure(normalized);

        }

        // The disposition is spent, so the journal may say so and then retire. Retirement is last on
        // purpose: everything before it is recoverable from the journal, and nothing after it is.
        if (phases is not null && suffix.Value is ReconciliationSuffix.Retirable)
        {

            Result retired = await RetireAsync(phases, lifecycle.Token).ConfigureAwait(false);

            if (retired.IsFailure)
            {

                _logger.LogError(
                    "A Covenant erasure spent its {Disposition} disposition but could not retire the "
                    + "offline transition journal ({ErrorCode}); the journal stays adoptable.",
                    disposition,
                    retired.Error.Code);

                return Result<CovenantErasureCompletion>.Failure(MaintenanceFailure());

            }

        }

        return Result<CovenantErasureCompletion>.Success(
            new CovenantErasureCompletion(
                disposition,
                progress.CanonicalResetApplied,
                progress.LocalSecureErasureComplete,
                progress.Exposure,
                blockingErrorCode));

    }

    /// <summary>
    /// Publishes the reconciliation suffix up to the point the one disposition may be spent.
    /// </summary>
    /// <remarks>
    /// The database row is terminalized here rather than after the coordinator returns, because the
    /// journal may not retire until it can name the exact terminal winner - and a row terminalized
    /// afterwards would leave a window where the journal has been discarded and nothing says which
    /// answer the row was supposed to carry.
    ///
    /// <para>An outcome that does not permit retirement parks instead. A missing row, a row belonging
    /// to another launch, a row that moved, and a row somebody else terminalized are four different
    /// answers and none of them is one this transition may overwrite.</para>
    /// </remarks>
    private async Task<Result<ReconciliationSuffix>> ReconcileBeforeDispositionAsync(
        GrimoireOfflineTransitionPhaseSession phases,
        CovenantExclusiveLeaseDisposition disposition,
        CovenantGrimoireClosure? closure,
        CancellationToken cancellationToken)
    {

        // The suffix is resumable, so every step below is entered only if the journal does not already
        // record it. A park lifted at the start of this run puts the transition back exactly where it
        // stopped, which is routinely inside this sequence - and replaying a step from there would ask
        // the graph for an edge it has already spent.
        if (phases.State is GrimoireOfflineTransitionState.RetirementPending)
        {

            return Result<ReconciliationSuffix>.Success(ReconciliationSuffix.Retirable);

        }

        if (phases.State is not GrimoireOfflineTransitionState.DatabaseReconciliationPending)
        {

            Result prepared = await PrepareForReconciliationAsync(phases, disposition, cancellationToken)
                .ConfigureAwait(false);

            if (prepared.IsFailure)
            {

                // A transition that cannot reach a terminal intent has neither answer available, which
                // is the correct outcome for a run that stopped in the middle rather than a failure of
                // this step. It parks, and the caller must not then retire what it just parked.
                return Parked(await phases.ParkAsync(cancellationToken).ConfigureAwait(false));

            }

            Result opened = await phases.BeginReconciliationAsync(cancellationToken)
                .ConfigureAwait(false);

            if (opened.IsFailure)
            {

                return Result<ReconciliationSuffix>.Failure(opened.Error);

            }

        }

        GrimoireOfflineTransitionReconciliationStep step = phases.ReconciliationStep
            ?? GrimoireOfflineTransitionReconciliationStep.CandidateVerified;

        if (step < GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner)
        {

            GrimoireOfflineTransitionDatabaseReconciliation reconciled = await WithLedgerAsync(
                closure,
                token => _reconciler.ReconcileAsync(
                    phases.Current.Payload,
                    disposition is CovenantExclusiveLeaseDisposition.CommitAndReopen
                        ? GrimoireOfflineTransitionTerminalDisposition.Completed
                        : GrimoireOfflineTransitionTerminalDisposition.FailedBeforeEffect,
                    token),
                cancellationToken).ConfigureAwait(false);

            if (reconciled.TerminalWinnerDigest is not { IsValid: true } winner)
            {

                // A reconciliation that cannot name the exact terminal winner has no answer this
                // transition may act on. The row is missing, or belongs to another launch, or moved,
                // or somebody else terminalized it - four different facts, none to overwrite.
                return Parked(await phases.ParkAsync(cancellationToken).ConfigureAwait(false));

            }

            Result recorded = await phases.RecordTerminalWinnerAsync(winner, cancellationToken)
                .ConfigureAwait(false);

            if (recorded.IsFailure)
            {

                return Result<ReconciliationSuffix>.Failure(recorded.Error);

            }

        }

        if (step < GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied)
        {

            CovenantDigest? proved = null;

            if (phases.ParentReceipt is { } sink)
            {

                // Published after the exact terminal winner is journaled and before the journal may
                // record the step, so the two records cross only once and always in that order. The
                // winner the outer record is told about is the one this transition already proved,
                // not a value recomputed here from the same database it just closed.
                if (phases.Current.Payload.Lifecycle.ReconciliationEvidence
                        ?.DatabaseTerminalWinnerDigest is not { IsValid: true } winner)
                {

                    return Parked(await phases.ParkAsync(cancellationToken).ConfigureAwait(false));

                }

                Result<CovenantDigest> published = await sink
                    .PublishAndRereadAsync(winner, cancellationToken)
                    .ConfigureAwait(false);

                if (published.IsFailure)
                {

                    // The database is already terminal and no effect may repeat. A receipt that could
                    // not be published, or that read back as something else, means the two records
                    // disagree about what happened - which is a state to stay closed in, not one to
                    // resolve by preferring either of them.
                    return Parked(await phases.ParkAsync(cancellationToken).ConfigureAwait(false));

                }

                proved = published.Value;

            }

            Result receipt = await phases.RecordParentReceiptAsync(proved, cancellationToken)
                .ConfigureAwait(false);

            if (receipt.IsFailure)
            {

                return Result<ReconciliationSuffix>.Failure(receipt.Error);

            }

        }

        if (step < GrimoireOfflineTransitionReconciliationStep.LaneClosed)
        {

            Result lane = await phases.RecordLaneClosedAsync(cancellationToken).ConfigureAwait(false);

            if (lane.IsFailure)
            {

                return Result<ReconciliationSuffix>.Failure(lane.Error);

            }

        }

        if (step >= GrimoireOfflineTransitionReconciliationStep.CovenantDispositionInFlight)
        {

            return Result<ReconciliationSuffix>.Success(ReconciliationSuffix.Retirable);

        }

        Result inFlight = await phases.BeginCovenantDispositionAsync(cancellationToken)
            .ConfigureAwait(false);

        return inFlight.IsFailure
            ? Result<ReconciliationSuffix>.Failure(inFlight.Error)
            : Result<ReconciliationSuffix>.Success(ReconciliationSuffix.Retirable);

    }

    /// <summary>
    /// Selects the one terminal intent and publishes the verification that precedes reconciliation.
    /// </summary>
    /// <remarks>
    /// A commit may only be selected from a complete phase ladder and a rollback only from a closing
    /// proof with nothing applied behind it, which is the graph enforcing what the two words mean. A
    /// transition anywhere else has neither answer available and parks instead — that is not a
    /// failure of this step, it is the correct outcome for a run that stopped in the middle.
    /// </remarks>
    private static async Task<Result> PrepareForReconciliationAsync(
        GrimoireOfflineTransitionPhaseSession phases,
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken)
    {

        if (phases.State is GrimoireOfflineTransitionState.Closing
            or GrimoireOfflineTransitionState.Applying)
        {

            Result reopening = await phases.PrepareReopenAsync(
                disposition is CovenantExclusiveLeaseDisposition.CommitAndReopen
                    ? GrimoireOfflineTransitionTerminalIntent.CommitAndReopen
                    : GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen,
                cancellationToken).ConfigureAwait(false);

            if (reopening.IsFailure)
            {

                return reopening;

            }

        }

        if (phases.State is GrimoireOfflineTransitionState.ReopenPrepared)
        {

            Result verifying = await phases.EnterVerifyingAsync(cancellationToken)
                .ConfigureAwait(false);

            if (verifying.IsFailure)
            {

                return verifying;

            }

        }

        // Published only when it would say something new. The graph admits a verification advance
        // only when the evidence changes, so a resumed run that had already published the complete
        // one would be refused - and this method's caller answers a refusal by parking, which puts
        // the journal straight back into the state it was resuming from. That is a loop no number of
        // resumes gets out of, over a transition that had in fact already done the work.
        return phases.State is GrimoireOfflineTransitionState.Verifying && !phases.VerificationIsComplete
            ? await phases.RecordVerificationAsync(true, true, true, cancellationToken)
                .ConfigureAwait(false)
            : Result.Success();

    }

    private static Result<ReconciliationSuffix> Parked(Result parked) =>
        parked.IsFailure
            ? Result<ReconciliationSuffix>.Failure(parked.Error)
            : Result<ReconciliationSuffix>.Success(ReconciliationSuffix.Parked);

    private static async Task<Result> RetireAsync(
        GrimoireOfflineTransitionPhaseSession phases,
        CancellationToken cancellationToken)
    {

        // Both publications are skipped when the journal already carries them, for the same reason the
        // suffix above is: a run resuming from a crash between them would otherwise fail on an edge it
        // had already taken, and retirement is exactly the point where that would strand the journal.
        if (phases.ReconciliationStep
            < GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified)
        {

            Result verified = await phases.CompleteCovenantDispositionAsync(cancellationToken)
                .ConfigureAwait(false);

            if (verified.IsFailure)
            {

                return verified;

            }

        }

        if (phases.State is not GrimoireOfflineTransitionState.RetirementPending)
        {

            Result pending = await phases.PrepareRetirementAsync(cancellationToken)
                .ConfigureAwait(false);

            if (pending.IsFailure)
            {

                return pending;

            }

        }

        return await phases.RetireAsync(cancellationToken).ConfigureAwait(false);

    }

    private static bool IsRecoverableAttention(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint) =>
        operation.State == LongRunningOperationState.ReconciliationRequired
        && operation.TerminalErrorCode is ErrorCodes.Covenant.MaintenanceFailed
            or ErrorCodes.Data.ReconciliationFailed
        && HasExactCheckpoint(operation, checkpoint);

    private static bool IsActiveCheckpoint(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId)
    {

        if (operation.State is not LongRunningOperationState.Running
            and not LongRunningOperationState.Waiting
            and not LongRunningOperationState.Cancelling
            || !string.Equals(operation.LeaseOwner, ownerId, StringComparison.Ordinal))
        {

            return false;

        }

        return HasExactCheckpoint(operation, checkpoint);

    }

    private static bool HasExactCheckpoint(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint)
    {

        if (operation.Id != checkpoint.OperationId
            || !string.Equals(
                operation.CheckpointReference,
                CovenantResetCheckpointInitiator.CheckpointReference(operation.Kind, operation.Id),
                StringComparison.Ordinal)
            || operation.CheckpointPayload is not { Length: > 0 } payload)
        {

            return false;

        }

        Result<CovenantErasureCheckpointState> durable = operation.Kind switch
        {

            LongRunningOperationKinds.DataRetentionMutation
                when operation.RecoveryPolicy == LongRunningOperationRecoveryPolicy.ReconcileAndComplete
                    && operation.CheckpointVersion == CovenantOfflineTransitionLaunchV4.CurrentVersion =>
                CovenantErasureCheckpointState.FromMutationCheckpoint(
                    operation.Id,
                    operation.CheckpointVersion,
                    payload,
                    out _),

            LongRunningOperationKinds.DataRetentionFactoryReset
                when operation.RecoveryPolicy == LongRunningOperationRecoveryPolicy.RestartIdempotently
                    && operation.CheckpointVersion == DataRetentionFactoryTransitionLaunchV2.CurrentVersion =>
                CovenantErasureCheckpointState.FromFactoryResetCheckpoint(
                    operation.Id,
                    operation.CheckpointVersion,
                    payload),

            _ => Result<CovenantErasureCheckpointState>.Failure(MaintenanceFailure()),

        };

        // The phase is deliberately excluded. A launch row records what was committed to and nothing
        // about how far the run got, so a decoded launch always projects the first phase while the
        // caller is holding whatever phase this attempt reached. Comparing the two would ask the row
        // a question it stopped being able to answer the moment offline phases stopped rewriting it.
        return durable.IsSuccess
            && durable.Value == checkpoint with { Phase = CovenantResetPhaseMachine.First };

    }

    private static async Task<Result> RunLifecycleAsync(
        Func<CancellationToken, Task<Result>> action,
        CancellationToken cancellationToken)
    {

        try
        {

            return await action(cancellationToken).ConfigureAwait(false);

        }
        catch (Exception)
        {

            return Result.Failure(MaintenanceFailure());

        }

    }

    private static Error MaintenanceFailure() =>
        new(
            ErrorCodes.Covenant.MaintenanceFailed,
            "The Covenant erasure lifecycle could not be completed safely.");

    /// <summary>
    /// The mutable facts one run accumulates, kept out of the durable checkpoint on purpose.
    /// </summary>
    /// <remarks>
    /// None of these belong in a checkpoint: two are derived from the phase already recorded there,
    /// and the third is a dataset identity the database itself owns. A checkpoint that carried them
    /// would be a second source for facts that already have one.
    /// </remarks>
}

/// <summary>
/// The internal signal that one erasure step did not complete.
/// </summary>
/// <remarks>
/// An exception rather than a returned result, so a step failure cannot be accidentally ignored by a
/// caller that forgot to check. It never escapes the coordinator: every catch selects
/// <c>KeepClosed</c>, which is the only safe answer when a durable step's outcome is unknown.
/// </remarks>
internal sealed class CovenantErasureStepFailedException(Error error)
    : Exception(error.Message)
{

    internal Error Error { get; } = error;

}
