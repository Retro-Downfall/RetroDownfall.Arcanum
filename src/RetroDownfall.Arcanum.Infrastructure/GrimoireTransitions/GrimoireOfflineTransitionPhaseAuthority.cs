using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

/// <summary>
/// Opens the durable phase authority for one offline transition, or resumes the one already open.
/// </summary>
/// <remarks>
/// A seam rather than a direct dependency because the four things it takes care of are four things
/// an erasure coordinator has no business knowing: which directory is guarded, what this
/// installation's identity is, how the host's maintenance lock is borrowed, and how a durable launch
/// row becomes a journal binding. A coordinator that assembled those itself would be reaching past
/// the boundary that keeps the journal's security properties in one place.
/// </remarks>
internal interface IGrimoireOfflineTransitionPhaseAuthority
{

    /// <summary>
    /// Resumes the active journal for this operation, or opens one from its committed launch.
    /// </summary>
    /// <remarks>
    /// One entry point rather than separate open and resume calls, because the caller cannot tell
    /// which it needs. A crash between the launch commit and the first publication leaves a row with
    /// no journal, and a crash after leaves a journal that is already ahead of anything the caller
    /// knows; deciding from the durable evidence is the only way to get it right, and asking the
    /// caller to decide would put that reasoning in every call site.
    /// </remarks>
    Task<Result<GrimoireOfflineTransitionPhaseSession>> OpenOrResumeAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken);

}

/// <summary>
/// The production phase authority, over the host's own maintenance lock and credential store.
/// </summary>
internal sealed class GrimoireOfflineTransitionPhaseAuthority(
    GrimoireOfflineTransitionLifecycleStore lifecycle,
    IInstallationResetMaintenanceLockAccessor maintenanceLocks,
    IInstallationResetDatabaseIdentityReader installationIdentities,
    ILongRunningOperationStore operations,
    IOsCredentialStore credentials,
    string guardedDirectory) : IGrimoireOfflineTransitionPhaseAuthority
{

    private readonly GrimoireOfflineTransitionLifecycleStore _lifecycle =
        lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

    private readonly IInstallationResetMaintenanceLockAccessor _maintenanceLocks =
        maintenanceLocks ?? throw new ArgumentNullException(nameof(maintenanceLocks));

    private readonly IInstallationResetDatabaseIdentityReader _installationIdentities =
        installationIdentities ?? throw new ArgumentNullException(nameof(installationIdentities));

    private readonly ILongRunningOperationStore _operations =
        operations ?? throw new ArgumentNullException(nameof(operations));

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    private readonly string _guardedDirectory = string.IsNullOrWhiteSpace(guardedDirectory)
        ? throw new ArgumentException("A guarded directory is required.", nameof(guardedDirectory))
        : guardedDirectory;

    public async Task<Result<GrimoireOfflineTransitionPhaseSession>> OpenOrResumeAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        // Borrowed, never acquired. The running host already holds the exact installation lock for
        // the length of its own lifetime, and a transition that took a second one would be waiting
        // on itself.
        Result<ArcanumMaintenanceLock> borrowed = _maintenanceLocks.BorrowHeldLock(_guardedDirectory);

        if (borrowed.IsFailure)
        {

            return Result<GrimoireOfflineTransitionPhaseSession>.Failure(borrowed.Error);

        }

        Result<GrimoireOfflineTransitionLaunchBinding> launch = LaunchOf(operation);

        if (launch.IsFailure)
        {

            return Result<GrimoireOfflineTransitionPhaseSession>.Failure(launch.Error);

        }

        Result<GrimoireOfflineTransitionTypedRecoveryState> recovered = await _lifecycle
            .RecoverAsync(borrowed.Value, _guardedDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure)
        {

            return Result<GrimoireOfflineTransitionPhaseSession>.Failure(recovered.Error);

        }

        return recovered.Value.Outcome is GrimoireOfflineTransitionTypedRecoveryOutcome.NoActiveJournal
            ? await BeginJournalAsync(borrowed.Value, operation, launch.Value, cancellationToken)
                .ConfigureAwait(false)
            : Resume(borrowed.Value, launch.Value, recovered.Value);

    }

    /// <summary>
    /// Adopts an already-published journal, but only one bound to this exact launch.
    /// </summary>
    /// <remarks>
    /// The digest comparison is the whole check. A journal is authority over destructive effects, and
    /// one describing a different launch would authorize this operation to continue somebody else's
    /// plan - which is the single thing the launch binding exists to make impossible.
    /// </remarks>
    private Result<GrimoireOfflineTransitionPhaseSession> Resume(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionLaunchBinding launch,
        GrimoireOfflineTransitionTypedRecoveryState recovered) =>
        recovered.Publication is not { } publication
            ? Unresumable()
            : Admit(heldInstallationLock, launch, publication);

    /// <summary>
    /// Publishes the opening journal revision for a launch that has none yet.
    /// </summary>
    /// <remarks>
    /// Deliberately not called <c>OpenAsync</c>. Every acquisition of a database handle in this
    /// repository is catalogued by a syntax-only scanner that treats that name as a provider open, and
    /// a journal publication borrowing it would either be catalogued as a connection it is not, or be
    /// exempted by name — and an exemption by name is how the next real opener slips through.
    /// </remarks>
    private async Task<Result<GrimoireOfflineTransitionPhaseSession>> BeginJournalAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        LongRunningOperation operation,
        GrimoireOfflineTransitionLaunchBinding launch,
        CancellationToken cancellationToken)
    {

        Result<Guid> installation = await _installationIdentities
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (installation.IsFailure)
        {

            return Result<GrimoireOfflineTransitionPhaseSession>.Failure(installation.Error);

        }

        // The journal's envelope binds this profile's external installation identity, and the identity
        // has to exist before the first publication can commit to it. Seeding is idempotent and
        // refuses rather than overwrites: an identity already present and matching is returned
        // unchanged, and one naming a different installation is a refusal rather than a correction.
        Result<Guid> identity = SeedIdentity(heldInstallationLock, installation.Value);

        if (identity.IsFailure)
        {

            return Result<GrimoireOfflineTransitionPhaseSession>.Failure(identity.Error);

        }

        // Read back rather than taken from the instance the caller is holding. The revision a launch
        // commit produces is not the one the launch recorded, and whatever the caller last read may
        // already be behind - a lease acquisition or a single-flight start moves it. Binding a stale
        // value would make the terminal compare-exchange that has to happen before retirement refuse
        // its own row, which is the one refusal nothing downstream can act on.
        LongRunningOperation? current = await _operations
            .GetAsync(launch.OperationId, cancellationToken)
            .ConfigureAwait(false);

        if (current is null)
        {

            return Unresumable();

        }

        Result<GrimoireOfflineTransitionTypedPublication> opened = await _lifecycle
            .BeginBoundAsync(
                heldInstallationLock,
                _guardedDirectory,
                installation.Value,
                launch.OperationId,
                launch.Kind,
                PayloadVersion,
                slotEpoch => OpeningPayload(launch, current.Revision, slotEpoch),
                cancellationToken)
            .ConfigureAwait(false);

        return opened.IsFailure
            ? Result<GrimoireOfflineTransitionPhaseSession>.Failure(opened.Error)
            : Admit(heldInstallationLock, launch, opened.Value);

    }

    /// <summary>
    /// Builds the one session, through the token that will not exist without both halves.
    /// </summary>
    /// <remarks>
    /// Fresh entry and resumption go through the same admission on purpose. A journal this build has
    /// just published and one it has just recovered are the same kind of authority over the same
    /// destructive effects, and the case where the two paths differ is exactly the case where a
    /// recovered journal belongs to somebody else's launch.
    /// </remarks>
    private Result<GrimoireOfflineTransitionPhaseSession> Admit(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionLaunchBinding launch,
        GrimoireOfflineTransitionTypedPublication publication)
    {

        Result<GrimoireOfflineTransitionPhaseSession.ClosingOwner> admitted =
            GrimoireOfflineTransitionPhaseSession.ClosingOwner.ForVerifiedPublication(
                launch,
                publication);

        return admitted.IsFailure
            ? Result<GrimoireOfflineTransitionPhaseSession>.Failure(admitted.Error)
            : Result<GrimoireOfflineTransitionPhaseSession>.Success(
                new GrimoireOfflineTransitionPhaseSession(
                    _lifecycle,
                    heldInstallationLock,
                    admitted.Value));

    }

    private Result<Guid> SeedIdentity(ArcanumMaintenanceLock heldInstallationLock, Guid installation)
    {

        Result<GrimoireOfflineTransitionJournalLocation> location =
            new GrimoireOfflineTransitionJournalFileStore().ResolveLocation(_guardedDirectory);

        return location.IsFailure
            ? Result<Guid>.Failure(location.Error)
            : new BackupRestoreJournalInstallationIdentityProvider(_credentials).SeedFromDatabase(
                heldInstallationLock,
                _guardedDirectory,
                location.Value.ProfileNamespace,
                installation);

    }

    /// <summary>
    /// The opening publication, bound to the revision the launch row carried after its commit.
    /// </summary>
    /// <remarks>
    /// The revision is read from the row rather than derived from the launch's own recorded one,
    /// because a launch cannot name the value its own commit produces. The journal is refused if that
    /// revision is not past what the launch recorded, which is what makes a journal published against
    /// a stale read impossible rather than merely unlikely.
    /// </remarks>
    private static Result<IGrimoireOfflineTransitionPayload> OpeningPayload(
        GrimoireOfflineTransitionLaunchBinding launch,
        long observedRevision,
        ulong slotEpoch)
    {

        Result<GrimoireOfflineTransitionBinding> binding = GrimoireOfflineTransitionLaunch.JournalBinding(
            launch,
            slotEpoch,
            PayloadVersion,
            observedRevision,
            parentReceiptBindingDigest: null);

        if (binding.IsFailure)
        {

            return Result<IGrimoireOfflineTransitionPayload>.Failure(binding.Error);

        }

        GrimoireOfflineTransitionLifecycle lifecycle = new(
            GrimoireOfflineTransitionState.Prepared,
            GrimoireOfflineTransitionTerminalIntent.Undecided,
            new GrimoireOfflineTransitionClosingEvidence(false, false, false, false, false, null),
            new GrimoireOfflineTransitionVerificationEvidence(false, false, false),
            ReconciliationEvidence: null,
            Blocker: null);

        return Result<IGrimoireOfflineTransitionPayload>.Success(
            launch.Kind is GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure
                ? new HealthyCatalogFactoryErasureOfflineTransitionPayloadV1(
                    binding.Value,
                    lifecycle,
                    CovenantResetPhaseMachine.First,
                    InFlightPhase: null,
                    InFlightBeforeState: null,
                    ReplacementEvidence: null,
                    OrdinaryFactoryContinuationCompleted: false)
                : new CovenantResetOfflineTransitionPayloadV1(
                    binding.Value,
                    lifecycle,
                    CovenantResetPhaseMachine.First,
                    InFlightPhase: null,
                    InFlightBeforeState: null,
                    ReplacementEvidence: null));

    }

    /// <summary>Projects the row's committed launch through the one reader that decides what a launch is.</summary>
    private static Result<GrimoireOfflineTransitionLaunchBinding> LaunchOf(LongRunningOperation operation) =>
        GrimoireOfflineTransitionLaunch.FromCommittedCheckpoint(
            operation.CheckpointVersion,
            operation.CheckpointPayload ?? []);

    private const byte PayloadVersion = 1;

    private static Result<T> Unresumable<T>() => Result<T>.Failure(
        new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The authenticated offline transition payload cannot be recovered by this build."));

    private static Result<GrimoireOfflineTransitionPhaseSession> Unresumable() =>
        Unresumable<GrimoireOfflineTransitionPhaseSession>();

}
