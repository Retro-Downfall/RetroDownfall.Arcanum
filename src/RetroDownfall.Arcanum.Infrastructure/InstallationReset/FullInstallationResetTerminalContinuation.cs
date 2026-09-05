using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// The last authorized step of an attested full installation reset.
/// </summary>
/// <remarks>
/// It runs after the Grimoire is gone and before anything may report the installation deleted. Two
/// things happen here that cannot happen anywhere else: a profile's restore history is proven over,
/// and the three credentials that could otherwise have finished an interrupted restore are removed.
/// </remarks>
internal interface IFullInstallationResetTerminalContinuation
{

    Task<Result<FullInstallationResetTerminalOutcome>> CompleteAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication publication,
        CancellationToken cancellationToken);

}

/// <summary>
/// What the terminal step reached, and the publication it left current.
/// </summary>
/// <remarks>
/// The publication is handed back rather than left for the caller to rediscover. Every phase this
/// step publishes advances the authenticated envelope revision, so a caller still holding the one it
/// started from would conflict on its very next write — and the next write is the one that says the
/// reset is verified.
/// </remarks>
internal sealed record FullInstallationResetTerminalOutcome(
    InstallationResetRestoreCredentialCleanupPhase Phase,
    InstallationResetActivePublication Publication);

/// <summary>
/// The production terminal continuation.
/// </summary>
/// <remarks>
/// Its whole job is ordering, and the order is not a preference. The three restore credentials may
/// only be removed once the database that could still have needed them is provably gone, and the
/// database may only be deleted once every managed file it recorded has been accounted for. So this
/// type reproves both facts from durable state before it touches anything: the authenticated record
/// has to carry a managed-file checkpoint at <c>TerminalInventoryVerified</c>, and the Grimoire
/// database file has to be absent from the guarded root by direct observation rather than by
/// inference from a cleanup result somebody else reported.
///
/// <para>What authenticates it after the database is gone is the reason the active record was never
/// stored inside the database. The record is a file beside the maintenance lock in the guarded root's
/// parent, its key and anti-rollback anchor live in the OS credential store, and all three survive
/// deletion of everything this operation just removed — so the last steps of the reset are authorized
/// by exactly the same authenticated publication the first ones were.</para>
///
/// <para>Identity rotation is not a separate effect here, and deliberately so. Every one of the four
/// identity families is destroyed by a step that has already run: the path-identity registry, the
/// authority state with its authority and recovery-envelope epochs, and the installation identity the
/// database carried all go with the Grimoire; the Campaign root-identity key goes with the ordinary
/// accepted credential inventory; and the pre-database installation identity is one of the three
/// removed here. Rotation is what the next start does with nothing left to inherit, so what this type
/// owes is proof that nothing was left, which is what <c>VerifiedAbsent</c> is.</para>
/// </remarks>
internal sealed class FullInstallationResetTerminalContinuation(
    IInstallationResetActiveStore activeStore,
    BackupRestoreJournalAnchorStore anchors,
    InstallationResetRestoreCredentialCleanup credentials,
    IOsCredentialStore credentialStore,
    GrimoireOfflineTransitionJournalAnchorStore transitionAnchors,
    string grimoireDatabaseFile)
    : IFullInstallationResetTerminalContinuation
{

    private readonly GrimoireOfflineTransitionJournalAnchorStore _transitionAnchors =
        transitionAnchors ?? throw new ArgumentNullException(nameof(transitionAnchors));

    private readonly IInstallationResetActiveStore _activeStore =
        activeStore ?? throw new ArgumentNullException(nameof(activeStore));

    private readonly BackupRestoreJournalAnchorStore _anchors =
        anchors ?? throw new ArgumentNullException(nameof(anchors));

    private readonly InstallationResetRestoreCredentialCleanup _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    private readonly IOsCredentialStore _credentialStore =
        credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));

    private readonly string _grimoireDatabaseFile =
        string.IsNullOrWhiteSpace(grimoireDatabaseFile)
            ? throw new ArgumentException(
                "A terminal continuation needs the database path it must prove absent.",
                nameof(grimoireDatabaseFile))
            : grimoireDatabaseFile;

    public async Task<Result<FullInstallationResetTerminalOutcome>> CompleteAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication publication,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(publication);

        // Asserted, never acquired and never disposed. The caller owns this lock for the whole
        // operation and the rest of the reset still depends on it.
        heldInstallationLock.AssertHeldFor(_activeStore.GuardedRoot);

        Result<AuthenticatedTerminalState> state = await RevalidateAsync(
            heldInstallationLock,
            publication,
            cancellationToken).ConfigureAwait(false);

        if (state.IsFailure)
        {

            return Result<FullInstallationResetTerminalOutcome>.Failure(state.Error);

        }

        AuthenticatedTerminalState current = state.Value;

        if (current.Marker.RestoreCredentialCleanup
            is InstallationResetRestoreCredentialCleanupPhase.TransitionCredentialsVerifiedAbsent)
        {

            // Already finished. A resumed operation reads this rather than removing anything again.
            // The guard is the terminal phase of the whole cleanup rather than of its first half: a
            // record resting at the restore trio's own verification still owes the transition pair.
            return Result<FullInstallationResetTerminalOutcome>.Success(
                new FullInstallationResetTerminalOutcome(
                    InstallationResetRestoreCredentialCleanupPhase
                        .TransitionCredentialsVerifiedAbsent,
                    current.Publication));

        }

        Result<ProvenTerminalState> proven = await ProveOrAdoptAsync(
            heldInstallationLock,
            current,
            cancellationToken).ConfigureAwait(false);

        if (proven.IsFailure)
        {

            return Result<FullInstallationResetTerminalOutcome>.Failure(proven.Error);

        }

        // The proof may itself have published, so the state travels back with it rather than being
        // reread from a publication this method still remembers.
        BackupRestoreFullResetTerminalProjectionV1 terminal = proven.Value.Terminal;

        current = proven.Value.State;

        // Checked before the first removal rather than after the last. Every identity family this
        // reset must rotate is destroyed by a step that has already run — the path-identity registry,
        // the authority state and its epochs, and the database's installation identity went with the
        // Grimoire, and the Campaign root-identity key went with the accepted credential inventory. If
        // one of them is still there, something earlier did not do what it reported, and the honest
        // response is to stop before taking anything else rather than to finish and then refuse.
        Result rotated = VerifyIdentitiesRotated();

        if (rotated.IsFailure)
        {

            return Result<FullInstallationResetTerminalOutcome>.Failure(rotated.Error);

        }

        Result<ImmutableArray<InstallationResetRestoreCredentialStep>> steps =
            InstallationResetRestoreCredentialCleanup.OrderedSteps(terminal);

        if (steps.IsFailure)
        {

            return Result<FullInstallationResetTerminalOutcome>.Failure(steps.Error);

        }

        foreach (InstallationResetRestoreCredentialStep step in steps.Value)
        {

            // Steps already recorded as done are skipped rather than re-issued. The removal itself is
            // idempotent, but re-issuing one would publish a phase the record has already passed.
            if (current.Marker.RestoreCredentialCleanup is { } reached
                && reached >= step.CompletedPhase)
            {

                continue;

            }

            Result removed = _credentials.RemoveStep(step);

            if (removed.IsFailure)
            {

                return Result<FullInstallationResetTerminalOutcome>.Failure(removed.Error);

            }

            Result<AuthenticatedTerminalState> advanced = await PublishAsync(
                heldInstallationLock,
                current,
                terminal,
                step.CompletedPhase,
                cancellationToken).ConfigureAwait(false);

            if (advanced.IsFailure)
            {

                return Result<FullInstallationResetTerminalOutcome>.Failure(advanced.Error);

            }

            current = advanced.Value;

        }

        Result verified = _credentials.VerifyAllAbsent(terminal);

        if (verified.IsFailure)
        {

            return Result<FullInstallationResetTerminalOutcome>.Failure(verified.Error);

        }

        if (current.Marker.RestoreCredentialCleanup
            is not InstallationResetRestoreCredentialCleanupPhase.RestoreCredentialsVerifiedAbsent
                and not InstallationResetRestoreCredentialCleanupPhase.TransitionAnchorRemoved
                and not InstallationResetRestoreCredentialCleanupPhase.TransitionKeyRemoved)
        {

            Result<AuthenticatedTerminalState> restoreVerified = await PublishAsync(
                heldInstallationLock,
                current,
                terminal,
                InstallationResetRestoreCredentialCleanupPhase.RestoreCredentialsVerifiedAbsent,
                cancellationToken).ConfigureAwait(false);

            if (restoreVerified.IsFailure)
            {

                return Result<FullInstallationResetTerminalOutcome>.Failure(restoreVerified.Error);

            }

            current = restoreVerified.Value;

        }

        return await CompleteTransitionPairAsync(
            heldInstallationLock,
            current,
            terminal,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Removes the offline-transition slot's two accounts, after proving the slot is over.
    /// </summary>
    /// <remarks>
    /// The last thing this reset takes, and the only path in the product that may take it. Every other
    /// cleanup — ordinary credential deletion, a Covenant reset, a family reinitialize, an unattested
    /// installation reset — retains both accounts byte for byte, because they are the only evidence
    /// that could ever finish an interrupted database transition.
    ///
    /// <para>The nested receipt is checked here rather than at the proof, because it is a statement
    /// about this reset rather than about the slot: a reset holding a claim it never saw completed has
    /// not finished the transition it started, and the credentials that could finish it are exactly
    /// what is about to go.</para>
    /// </remarks>
    private async Task<Result<FullInstallationResetTerminalOutcome>> CompleteTransitionPairAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        AuthenticatedTerminalState current,
        BackupRestoreFullResetTerminalProjectionV1 terminal,
        CancellationToken cancellationToken)
    {

        if (current.Publication.Payload.NestedTransitionReceipt is
            { Phase: not InstallationResetNestedTransitionPhase.Completed })
        {

            return Result<FullInstallationResetTerminalOutcome>.Failure(
                new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "A nested database transition this reset claimed has not reported its completion."));

        }

        Result<GrimoireOfflineTransitionJournalLocation> location =
            new GrimoireOfflineTransitionJournalFileStore().ResolveLocation(
                _activeStore.GuardedRoot);

        if (location.IsFailure)
        {

            return Result<FullInstallationResetTerminalOutcome>.Failure(location.Error);

        }

        // Adopted rather than reproved when one is already persisted. Once the first account is gone
        // the slot no longer has the shape the proof was made from, so a resumed pass compares each
        // survivor against the digest projected while both were still there.
        GrimoireOfflineTransitionFullResetTerminalProjectionV1? adopted =
            current.Marker.TransitionTerminal;

        if (adopted is null)
        {

            Result<GrimoireOfflineTransitionFullResetTerminalProjectionV1> proven =
                _transitionAnchors.ProveFullResetTerminal(
                    heldInstallationLock,
                    location.Value,
                    current.Publication.Envelope.InstallationId);

            if (proven.IsFailure)
            {

                return Result<FullInstallationResetTerminalOutcome>.Failure(proven.Error);

            }

            adopted = proven.Value;

            // Persisted only when there is something to compare it against later. A slot that was
            // never opened has no account to remove and therefore nothing a resumed pass would need
            // the projection for, and every publication costs an envelope revision that stales the
            // authorities bound to the one it replaced.
            if (adopted.Arm is GrimoireOfflineTransitionFullResetTerminalArm.ClosedAnchor)
            {

                Result<AuthenticatedTerminalState> persisted = await PublishAsync(
                    heldInstallationLock,
                    current,
                    terminal,
                    phase: null,
                    cancellationToken,
                    adopted).ConfigureAwait(false);

                if (persisted.IsFailure)
                {

                    return Result<FullInstallationResetTerminalOutcome>.Failure(persisted.Error);

                }

                current = persisted.Value;

            }

        }

        Result<ImmutableArray<InstallationResetRestoreCredentialStep>> steps =
            InstallationResetRestoreCredentialCleanup.OrderedTransitionSteps(
                adopted,
                GrimoireOfflineTransitionJournalAnchorStore
                    .TerminalAccounts(location.Value.ProfileNamespace).AnchorAccount,
                GrimoireOfflineTransitionJournalAnchorStore
                    .TerminalAccounts(location.Value.ProfileNamespace).KeyAccount);

        if (steps.IsFailure)
        {

            return Result<FullInstallationResetTerminalOutcome>.Failure(steps.Error);

        }

        foreach (InstallationResetRestoreCredentialStep step in
                 adopted.Arm is GrimoireOfflineTransitionFullResetTerminalArm.ClosedAnchor
                     ? steps.Value
                     : [])
        {

            if (current.Marker.RestoreCredentialCleanup is { } reached
                && reached >= step.CompletedPhase)
            {

                continue;

            }

            Result removed = step.CompletedPhase
                is InstallationResetRestoreCredentialCleanupPhase.TransitionAnchorRemoved
                ? _transitionAnchors.RemoveAnchorForFullReset(
                    heldInstallationLock,
                    location.Value,
                    step.ProjectedValueDigest ?? default)
                : _transitionAnchors.RemoveJournalKeyForFullReset(
                    heldInstallationLock,
                    location.Value,
                    step.ProjectedValueDigest ?? default);

            if (removed.IsFailure)
            {

                return Result<FullInstallationResetTerminalOutcome>.Failure(removed.Error);

            }

            Result<AuthenticatedTerminalState> advanced = await PublishAsync(
                heldInstallationLock,
                current,
                terminal,
                step.CompletedPhase,
                cancellationToken,
                adopted).ConfigureAwait(false);

            if (advanced.IsFailure)
            {

                return Result<FullInstallationResetTerminalOutcome>.Failure(advanced.Error);

            }

            current = advanced.Value;

        }

        Result absent = _transitionAnchors.VerifyTerminalPairAbsent(
            heldInstallationLock,
            location.Value);

        if (absent.IsFailure)
        {

            return Result<FullInstallationResetTerminalOutcome>.Failure(absent.Error);

        }

        Result<AuthenticatedTerminalState> published = await PublishAsync(
            heldInstallationLock,
            current,
            terminal,
            InstallationResetRestoreCredentialCleanupPhase.TransitionCredentialsVerifiedAbsent,
            cancellationToken,
            adopted).ConfigureAwait(false);

        return published.IsFailure
            ? Result<FullInstallationResetTerminalOutcome>.Failure(published.Error)
            : Result<FullInstallationResetTerminalOutcome>.Success(
                new FullInstallationResetTerminalOutcome(
                    InstallationResetRestoreCredentialCleanupPhase
                        .TransitionCredentialsVerifiedAbsent,
                    published.Value.Publication));

    }

    /// <summary>
    /// Proves the restore history terminal, or adopts the proof a previous attempt already made.
    /// </summary>
    /// <remarks>
    /// A removal that has already started cannot prove itself again — the credential set it is midway
    /// through taking no longer has the shape the proof was made from, and re-deriving would report
    /// exactly the "partially cleaned" refusal that the durable record already explains. So the first
    /// attempt persists the projection with the operation, and every later one compares each surviving
    /// account against the digest projected for it while all three were still there.
    ///
    /// <para>The first proof is also the only place the database is required absent. That check
    /// belongs to the decision to start, not to each step of it: once the anchor is gone the
    /// installation is committed either way, and refusing a resume because somebody recreated a file
    /// at the database's path would strand the operation rather than protect anything.</para>
    /// </remarks>
    private async Task<Result<ProvenTerminalState>> ProveOrAdoptAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        AuthenticatedTerminalState state,
        CancellationToken cancellationToken)
    {

        if (state.Marker.RestoreTerminal is { } adopted)
        {

            return Result<ProvenTerminalState>.Success(new ProvenTerminalState(adopted, state));

        }

        // Observed, not inferred. A cleanup result saying the database was deleted is a claim by
        // whatever ran it; the credentials that could still finish an interrupted restore are removed
        // on the strength of the file genuinely not being there.
        if (File.Exists(_grimoireDatabaseFile))
        {

            return Inert<ProvenTerminalState>();

        }

        Result<BackupRestoreProfileNamespace> profile =
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_activeStore.GuardedRoot);

        if (profile.IsFailure)
        {

            return Result<ProvenTerminalState>.Failure(profile.Error);

        }

        Result<BackupRestoreFullResetTerminalProjectionV1> proven =
            _anchors.ProveFullResetTerminal(
                heldInstallationLock,
                _activeStore.GuardedRoot,
                profile.Value,
                state.InstallationId,
                CandidateStagingRoots());

        if (proven.IsFailure)
        {

            return Result<ProvenTerminalState>.Failure(proven.Error);

        }

        // Persisted before the first removal, so the operation can still finish from any point after
        // it. This publication is the commitment; everything after it is idempotent replay.
        Result<AuthenticatedTerminalState> published = await PublishAsync(
            heldInstallationLock,
            state,
            proven.Value,
            phase: null,
            cancellationToken).ConfigureAwait(false);

        return published.IsFailure
            ? Result<ProvenTerminalState>.Failure(published.Error)
            : Result<ProvenTerminalState>.Success(
                new ProvenTerminalState(proven.Value, published.Value));

    }

    private sealed record ProvenTerminalState(
        BackupRestoreFullResetTerminalProjectionV1 Terminal,
        AuthenticatedTerminalState State);

    /// <summary>
    /// Publishes the projection and the phase reached, and reauthenticates against the result.
    /// </summary>
    private async Task<Result<AuthenticatedTerminalState>> PublishAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        AuthenticatedTerminalState state,
        BackupRestoreFullResetTerminalProjectionV1 terminal,
        InstallationResetRestoreCredentialCleanupPhase? phase,
        CancellationToken cancellationToken,
        GrimoireOfflineTransitionFullResetTerminalProjectionV1? transitionTerminal = null)
    {

        Result<InstallationResetActivePublication> published = await _activeStore.AdvanceAsync(
            heldInstallationLock,
            state.Publication,
            state.Publication.Payload.ToRecord() with
            {
                HostToolsMarkerPairReset = state.Marker with
                {
                    RestoreTerminal = terminal,
                    RestoreCredentialCleanup = phase ?? state.Marker.RestoreCredentialCleanup,
                    TransitionTerminal = transitionTerminal ?? state.Marker.TransitionTerminal,
                },
            },
            cancellationToken).ConfigureAwait(false);

        return published.IsFailure
            ? Result<AuthenticatedTerminalState>.Failure(published.Error)
            : await RevalidateAsync(
                heldInstallationLock,
                published.Value,
                cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Rereads the durable record and requires everything this step stands on to still be true.
    /// </summary>
    private async Task<Result<AuthenticatedTerminalState>> RevalidateAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication expected,
        CancellationToken cancellationToken)
    {

        Result<InstallationResetActiveRecoveryState> recovered = await _activeStore
            .RecoverAsync(heldInstallationLock, cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure
            || recovered.Value.Outcome is not InstallationResetActiveRecoveryOutcome.AuthenticatedV2
            || recovered.Value.Publication is not { } current
            || current.EnvelopeDigest != expected.EnvelopeDigest
            || current.Payload.Scope is not InstallationResetScope.All
            || current.Payload.FullInstallationResetRemediationClaim is not { } claim
            || current.Payload.HostToolsMarkerPairReset is not { } marker
            || marker.ManagedFile is not
            {
                Phase: FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified,
            })
        {

            return Inert<AuthenticatedTerminalState>();

        }

        return Result<AuthenticatedTerminalState>.Success(
            new AuthenticatedTerminalState(current, marker, claim.InstallationId));

    }

    private sealed record AuthenticatedTerminalState(
        InstallationResetActivePublication Publication,
        HostToolsMarkerPairResetCheckpointV1 Marker,
        Guid InstallationId);

    /// <summary>
    /// Proves that nothing survives for a new installation to inherit an identity from.
    /// </summary>
    /// <remarks>
    /// Three of the four families are database state and are covered by the absence of the database
    /// file itself, which the caller has already observed: the Campaign path-identity registry, the
    /// authority state carrying the authority and recovery-envelope epochs, and the installation
    /// identity the authority row named. The fourth, the Campaign root-identity key, is an OS
    /// credential whose documented lifetime is "regenerated only by a full installation reset" — so a
    /// reset that left it in place would hand the next installation the key that turns a physical
    /// directory into an opaque Campaign root identity, and every root registered afterwards would
    /// derive the same identity the erased installation used.
    /// </remarks>
    private Result VerifyIdentitiesRotated()
    {

        try
        {

            OsCredentialStoreResult campaignRootIdentity = _credentialStore.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.CampaignRootIdentityKeyAccount);

            return campaignRootIdentity.Status is OsCredentialStoreStatus.NotFound
                ? Result.Success()
                : Result.Failure(new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "An identity a full installation reset must rotate is still present."));

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return Result.Failure(new Error(
                ErrorCodes.Covenant.Unavailable,
                "An identity a full installation reset must rotate could not be read."));

        }

    }

    /// <summary>
    /// The staging roots a restore could have left beside the guarded root.
    /// </summary>
    /// <remarks>
    /// Enumerated from the guarded root's parent because that is where a restore stages, and a
    /// terminal proof that skipped the enumeration would report "never restored" for an installation
    /// with a live journal sitting next to it. An unreadable parent yields an empty list, which makes
    /// the proof strictly harder rather than easier: an anchor with no locatable journal refuses.
    /// </remarks>
    private IReadOnlyList<string> CandidateStagingRoots()
    {

        try
        {

            string? parent = Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(_activeStore.GuardedRoot)));

            return parent is null || !Directory.Exists(parent)
                ? []
                : [.. Directory.EnumerateDirectories(
                    parent,
                    BackupRestoreJournal.StagingPrefix + "*",
                    SearchOption.TopDirectoryOnly)];

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {

            return [];

        }

    }

    /// <summary>
    /// One content-free refusal for every shape that is not entitled to finish.
    /// </summary>
    private static Result<T> Inert<T>() =>
        Result<T>.Failure(
            new Error(
                ErrorCodes.Data.RecoveryRequired,
                "The full installation reset requires recovery."));

}
