using Microsoft.Win32.SafeHandles;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// Where one operation's journal lives, as identity rather than as a path.
/// </summary>
/// <remarks>
/// The staging root's own physical identity is what the anchor commits to. A canonically named
/// directory carries no authority — the name is a bare random suffix anyone can create — so location
/// has to be proved from the handle the journal was written through.
/// </remarks>
internal sealed record BackupRestoreJournalLocation(
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    Guid OperationId,
    string StagingRoot,
    CovenantDigest StagingRootPhysicalIdentityDigest,
    CovenantDigest JournalLocationDigest);

/// <summary>
/// One authenticated journal revision and the anchor that currently commits to it.
/// </summary>
internal sealed record BackupRestoreJournalPublication(
    BackupRestoreJournalLocation Location,
    BackupRestoreJournalEnvelopeV2 Envelope,
    CovenantDigest EnvelopeDigest,
    BackupRestoreJournalPayloadV2 Payload,
    BackupRestoreJournalAnchorV1 Anchor);

/// <summary>What a recovery pass proved about one profile's restore evidence.</summary>
/// <remarks>
/// Two outcomes only. Everything else — a rollback, a replay, a lookalike, a missing key beside live
/// evidence — is a typed failure, because absence is the one answer that lets startup continue, and it
/// must never be reachable by tampering.
/// </remarks>
internal enum BackupRestoreJournalRecoveryOutcome : byte
{

    NoActiveJournal = 1,

    Authenticated = 2,

}

internal sealed record BackupRestoreJournalRecoveryState(
    BackupRestoreJournalRecoveryOutcome Outcome,
    BackupRestoreJournalPublication? Publication,
    Guid? OperationId = null);

/// <summary>
/// The OS-secret anti-rollback anchor and the durable journal transitions it serializes.
/// </summary>
/// <remarks>
/// The journal file alone cannot resist rollback: it sits inside the staging root, and anyone who can
/// write there can present any authentic earlier revision. The anchor lives in the OS credential store
/// under a profile-namespaced account and is advanced only under the caller-held installation lock,
/// through one read-compare-write-readback per revision. That leaves exactly one crash window — the
/// envelope reached disk and the anchor did not — and recovery closes that window through the same
/// checked write before any effect (§10.19.6).
///
/// <para>The lock is borrowed, never acquired: this type asserts the caller's identity through
/// <see cref="ArcanumMaintenanceLock.AssertHeldFor(string)"/> and never calls <c>TryAcquire</c>,
/// <c>IsHeld</c>, or <c>Dispose</c>.</para>
/// </remarks>
internal sealed class BackupRestoreJournalAnchorStore(
    IOsCredentialStore credentials,
    BackupRestoreJournalKeyProvider keys,
    BackupRestoreJournalInstallationIdentityProvider identities)
{

    /// <summary>
    /// The canonical journal child name.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>restore-journal.json</c>. A V2 envelope and the pre-Covenant plain journal
    /// must never be mistaken for one another: one is authenticated evidence and the other is a
    /// document anyone can write.
    /// </remarks>
    internal const string JournalFileName = "restore-journal.v2.json";

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    private readonly BackupRestoreJournalKeyProvider _keys =
        keys ?? throw new ArgumentNullException(nameof(keys));

    private readonly BackupRestoreJournalInstallationIdentityProvider _identities =
        identities ?? throw new ArgumentNullException(nameof(identities));

    /// <summary>
    /// Derives the journal location for one staging root from the handle it is opened through.
    /// </summary>
    internal Result<BackupRestoreJournalLocation> ResolveLocation(
        BackupRestoreProfileNamespace profileNamespace,
        Guid installationId,
        Guid operationId,
        string stagingRoot)
    {

        ArgumentNullException.ThrowIfNull(profileNamespace);

        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        Result<CovenantDigest> parent = StagingRootIdentity(stagingRoot, out string full);

        if (parent.IsFailure)
        {

            return Result<BackupRestoreJournalLocation>.Failure(parent.Error);

        }

        Result<CovenantDigest> location = BackupRestoreJournalAuthenticator.JournalLocation(
            profileNamespace.Digest,
            installationId,
            operationId,
            parent.Value,
            JournalFileName);

        return location.IsFailure
            ? Result<BackupRestoreJournalLocation>.Failure(location.Error)
            : new BackupRestoreJournalLocation(
                profileNamespace.Digest,
                installationId,
                operationId,
                full,
                parent.Value,
                location.Value);

    }

    /// <summary>
    /// Opens a new operation: an <c>Active</c> revision-zero anchor, then envelope revision one.
    /// </summary>
    /// <remarks>
    /// The anchor is written before the envelope so that a crash between them leaves an anchor naming
    /// a journal that does not exist yet — a blocker rather than an orphaned envelope nothing commits
    /// to. It refuses to open over another operation's live anchor.
    /// </remarks>
    internal Result<BackupRestoreJournalPublication> Begin(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace,
        BackupRestoreJournalLocation location,
        BackupRestoreJournalPayloadV2 payload)
    {

        Result asserted = Assert(heldInstallationLock, guardedDirectory, profileNamespace, location, payload);

        if (asserted.IsFailure)
        {

            return Result<BackupRestoreJournalPublication>.Failure(asserted.Error);

        }

        if (location.OperationId != payload.OwnerOperationId)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "A restore journal location must name the operation that owns its payload.");

        }

        Result<BackupRestoreJournalAnchorV1?> existing = TryReadAnchor(profileNamespace);

        if (existing.IsFailure)
        {

            return Result<BackupRestoreJournalPublication>.Failure(existing.Error);

        }

        if (existing.Value is { } live)
        {

            if (live.State is BackupRestoreJournalAnchorState.Active)
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This profile already has an active restore journal anchor.");

            }

            if (live.OperationId == location.OperationId)
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This restore operation has already been closed and cannot be reopened.");

            }

            Result absent = RequireJournalAbsent(location);

            if (absent.IsFailure)
            {

                return Result<BackupRestoreJournalPublication>.Failure(absent.Error);

            }

        }

        BackupRestoreJournalAnchorV1 opening = new(
            BackupRestoreJournalAuthenticator.AnchorVersion,
            profileNamespace.Digest,
            location.InstallationId,
            location.OperationId,
            0,
            BackupRestoreJournalAuthenticator.ZeroDigest,
            location.JournalLocationDigest,
            BackupRestoreJournalAnchorState.Active);

        Result opened = WriteAnchorAndVerify(profileNamespace, opening);

        if (opened.IsFailure)
        {

            return Result<BackupRestoreJournalPublication>.Failure(opened.Error);

        }

        Result<BackupRestoreJournalPublication> published = Publish(
            profileNamespace,
            location,
            opening,
            payload,
            BackupRestoreJournalAuthenticator.ZeroDigest);

        if (published.IsFailure)
        {

            RetireUnpublishedOpening(profileNamespace, location, opening);

        }

        return published;

    }

    /// <summary>
    /// Retires an opening anchor whose first revision never reached disk.
    /// </summary>
    /// <remarks>
    /// The anchor is written before the envelope so a crash between them is *detectable*; the cost is
    /// that an active anchor naming no journal is a blocker. When the failure is not a crash — the
    /// process is alive, still holds the lock, and knows nothing was published — leaving that blocker
    /// behind would let a transient disk-full or an absent journal key wedge the profile permanently,
    /// because <see cref="Begin"/> then refuses on the live anchor and <see cref="Close"/> is
    /// unreachable with no publication to close.
    ///
    /// <para>It writes the <c>Closed</c> tombstone rather than deleting the account: the three
    /// restore-journal credentials are retained by every path in this build, and a tombstone is
    /// exactly what an operation that reached no revision leaves behind. Every precondition is
    /// re-proved first — the anchor must still be byte-for-byte the one just written, at revision
    /// zero, and no journal may exist at the location — so this can never retire an anchor that
    /// governs real evidence. A failure here is deliberately swallowed: the caller is already
    /// returning the original failure, and the surviving active anchor is the fail-closed outcome.
    /// </para>
    /// </remarks>
    private void RetireUnpublishedOpening(
        BackupRestoreProfileNamespace profileNamespace,
        BackupRestoreJournalLocation location,
        BackupRestoreJournalAnchorV1 opening)
    {

        if (opening.Revision != 0
            || opening.State is not BackupRestoreJournalAnchorState.Active
            || RequireAnchorMatches(profileNamespace, opening).IsFailure
            || RequireJournalAbsent(location).IsFailure)
        {

            return;

        }

        _ = WriteAnchorAndVerify(
            profileNamespace,
            opening with { State = BackupRestoreJournalAnchorState.Closed });

    }

    /// <summary>
    /// Publishes the next revision and advances the anchor to match it.
    /// </summary>
    internal Result<BackupRestoreJournalPublication> Advance(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace,
        BackupRestoreJournalPublication current,
        BackupRestoreJournalPayloadV2 payload)
    {

        ArgumentNullException.ThrowIfNull(current);

        Result asserted = Assert(
            heldInstallationLock,
            guardedDirectory,
            profileNamespace,
            current.Location,
            payload);

        if (asserted.IsFailure)
        {

            return Result<BackupRestoreJournalPublication>.Failure(asserted.Error);

        }

        if (payload.OwnerOperationId != current.Location.OperationId)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "A restore journal revision cannot change the operation that owns it.");

        }

        Result compared = RequireAnchorMatches(profileNamespace, current.Anchor);

        return compared.IsFailure
            ? Result<BackupRestoreJournalPublication>.Failure(compared.Error)
            : Publish(
                profileNamespace,
                current.Location,
                current.Anchor,
                payload,
                current.EnvelopeDigest);

    }

    /// <summary>
    /// Proves what this profile's restore evidence is, and closes the one permitted crash window.
    /// </summary>
    /// <remarks>
    /// <paramref name="candidateStagingRoots"/> is the complete set the caller swept — the canonical
    /// staging roots beside the guarded tree plus every staging-index entry. Proving absence over a set
    /// the caller assembled is deliberate: this type owns what the evidence means, and the pre-database
    /// bootstrap owns where to look for it.
    /// </remarks>
    internal Result<BackupRestoreJournalRecoveryState> Recover(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace,
        IReadOnlyList<string> candidateStagingRoots)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentNullException.ThrowIfNull(profileNamespace);

        ArgumentNullException.ThrowIfNull(candidateStagingRoots);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        return Classify(
            profileNamespace,
            candidateStagingRoots,
            advanceOneAhead: true);

    }

    /// <summary>
    /// Classifies the same authenticated evidence as <see cref="Recover"/> without advancing the
    /// anchor in the one-ahead crash window.
    /// </summary>
    internal Result<Guid?> InspectActiveOperationId(
        BackupRestoreProfileNamespace profileNamespace,
        IReadOnlyList<string> candidateStagingRoots)
    {

        ArgumentNullException.ThrowIfNull(profileNamespace);

        ArgumentNullException.ThrowIfNull(candidateStagingRoots);

        Result<BackupRestoreJournalRecoveryState> classified = Classify(
            profileNamespace,
            candidateStagingRoots,
            advanceOneAhead: false);

        return classified.IsFailure
            ? Result<Guid?>.Failure(classified.Error)
            : classified.Value.Outcome
                is BackupRestoreJournalRecoveryOutcome.Authenticated
                ? classified.Value.OperationId
                : null;

    }

    private Result<BackupRestoreJournalRecoveryState> Classify(
        BackupRestoreProfileNamespace profileNamespace,
        IReadOnlyList<string> candidateStagingRoots,
        bool advanceOneAhead)
    {

        Result<BackupRestoreJournalAnchorV1?> anchorRead = TryReadAnchor(profileNamespace);

        if (anchorRead.IsFailure)
        {

            return Result<BackupRestoreJournalRecoveryState>.Failure(anchorRead.Error);

        }

        List<string> withJournal = [];

        foreach (string candidate in candidateStagingRoots)
        {

            if (string.IsNullOrWhiteSpace(candidate))
            {

                continue;

            }

            Result<bool> present = HasJournalFile(candidate);

            if (present.IsFailure)
            {

                return Result<BackupRestoreJournalRecoveryState>.Failure(present.Error);

            }

            if (present.Value)
            {

                withJournal.Add(candidate);

            }

        }

        if (anchorRead.Value is not { } anchor)
        {

            // Proven absence: no anchor, and nothing that even looks like a journal. An unanchored
            // journal is a lookalike, and a lookalike is never "nothing happened".
            return withJournal.Count == 0
                ? new BackupRestoreJournalRecoveryState(
                    BackupRestoreJournalRecoveryOutcome.NoActiveJournal,
                    null)
                : new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "A canonical restore journal exists with no anchor committing to it.");

        }

        if (anchor.ProfileNamespaceDigest != profileNamespace.Digest)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This restore anchor was written under a different profile namespace.");

        }

        if (anchor.State is BackupRestoreJournalAnchorState.Closed)
        {

            return withJournal.Count == 0
                ? new BackupRestoreJournalRecoveryState(
                    BackupRestoreJournalRecoveryOutcome.NoActiveJournal,
                    null)
                : new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "A restore journal exists beneath a closed anchor tombstone.");

        }

        Result<BackupRestoreJournalIdentityProbe> identity = _identities.Probe(profileNamespace);

        if (identity.IsFailure)
        {

            return Result<BackupRestoreJournalRecoveryState>.Failure(identity.Error);

        }

        if (identity.Value.Presence is not BackupRestoreJournalIdentityPresence.Present)
        {

            return new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "An active restore anchor exists without this profile's external installation identity.");

        }

        if (identity.Value.InstallationId != anchor.InstallationId)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This restore anchor names a different installation than the profile now carries.");

        }

        Result<BackupRestoreJournalLocation> located = LocateExactly(
            profileNamespace,
            anchor,
            candidateStagingRoots);

        if (located.IsFailure)
        {

            return Result<BackupRestoreJournalRecoveryState>.Failure(located.Error);

        }

        Result<BackupRestoreJournalEnvelopeV2> read = ReadJournal(located.Value);

        if (read.IsFailure)
        {

            return Result<BackupRestoreJournalRecoveryState>.Failure(read.Error);

        }

        Result<BackupRestoreJournalPayloadV2> payload = OpenEnvelope(
            profileNamespace,
            anchor.InstallationId,
            read.Value);

        if (payload.IsFailure)
        {

            return Result<BackupRestoreJournalRecoveryState>.Failure(payload.Error);

        }

        Result<CovenantDigest> digest = BackupRestoreJournalAuthenticator.EnvelopeDigest(read.Value);

        if (digest.IsFailure)
        {

            return Result<BackupRestoreJournalRecoveryState>.Failure(digest.Error);

        }

        if (read.Value.OperationId != anchor.OperationId)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This restore journal belongs to a different operation than its anchor.");

        }

        // Exact match: the anchor already commits to the envelope on disk.
        if (read.Value.Revision == anchor.Revision && digest.Value == anchor.EnvelopeDigest)
        {

            return new BackupRestoreJournalRecoveryState(
                BackupRestoreJournalRecoveryOutcome.Authenticated,
                new BackupRestoreJournalPublication(
                    located.Value,
                    read.Value,
                    digest.Value,
                    payload.Value,
                    anchor),
                located.Value.OperationId);

        }

        // One revision ahead with a matching chain: the single crash window between the journal fsync
        // and the anchor advance. Closing it through the same checked write is what makes the window
        // survivable rather than merely detectable.
        if (read.Value.Revision == checked(anchor.Revision + 1)
            && read.Value.PreviousEnvelopeDigest == anchor.EnvelopeDigest)
        {

            if (!advanceOneAhead)
            {

                return new BackupRestoreJournalRecoveryState(
                    BackupRestoreJournalRecoveryOutcome.Authenticated,
                    Publication: null,
                    located.Value.OperationId);

            }

            BackupRestoreJournalAnchorV1 closed = anchor with
            {
                Revision = read.Value.Revision,

                EnvelopeDigest = digest.Value,
            };

            // The anchor was read before the identity probe, the sweep, the journal read, and the
            // AES-GCM open. Comparing it again here is what keeps this a read-compare-write rather
            // than a blind overwrite: without it a concurrent terminal Close could land in that
            // window and be silently resurrected as a live anchor by this write.
            Result unchanged = RequireAnchorMatches(profileNamespace, anchor);

            if (unchanged.IsFailure)
            {

                return Result<BackupRestoreJournalRecoveryState>.Failure(unchanged.Error);

            }

            Result advanced = WriteAnchorAndVerify(profileNamespace, closed);

            return advanced.IsFailure
                ? Result<BackupRestoreJournalRecoveryState>.Failure(advanced.Error)
                : new BackupRestoreJournalRecoveryState(
                    BackupRestoreJournalRecoveryOutcome.Authenticated,
                    new BackupRestoreJournalPublication(
                        located.Value,
                        read.Value,
                        digest.Value,
                        payload.Value,
                        closed),
                    located.Value.OperationId);

        }

        return new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "This restore journal is an authenticated rollback or replay of the revision its anchor "
            + "commits to.");

    }

    /// <summary>
    /// Terminalizes one operation: the anchor goes <c>Closed</c> first, then the journal is deleted.
    /// </summary>
    /// <remarks>
    /// That order is the whole point. Deleting the journal first would leave an active anchor naming a
    /// file that no longer exists, which is indistinguishable from a journal an attacker removed.
    ///
    /// <para>It is therefore idempotent across its own halves. A delete that fails — a read-only
    /// staging directory, a scanner holding the file, a death between the two steps — leaves the
    /// tombstone written and the journal present, and a retry has to be able to finish. Requiring the
    /// anchor to still be exactly <c>Active</c> would fail that retry with a concurrency error that
    /// says "somebody else moved it" when the truth is "you already closed it", and nothing else on
    /// this type could reach the surviving journal.</para>
    /// </remarks>
    internal Result Close(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace,
        BackupRestoreJournalPublication terminal)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentNullException.ThrowIfNull(profileNamespace);

        ArgumentNullException.ThrowIfNull(terminal);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        Result<BackupRestoreJournalAnchorV1?> stored = TryReadAnchor(profileNamespace);

        if (stored.IsFailure)
        {

            return stored.Error;

        }

        BackupRestoreJournalAnchorV1 tombstone = terminal.Anchor with
        {
            State = BackupRestoreJournalAnchorState.Closed,
        };

        bool alreadyClosed = stored.Value == tombstone;

        if (!alreadyClosed && stored.Value != terminal.Anchor)
        {

            return new Error(
                ErrorCodes.Covenant.RevisionConflict,
                "This profile's restore journal anchor is not the revision this transition read.");

        }

        if (alreadyClosed)
        {

            // The tombstone already landed; only the journal removal is outstanding.
            return DeleteJournal(terminal.Location);

        }

        Result<BackupRestoreJournalEnvelopeV2> read = ReadJournal(terminal.Location);

        if (read.IsFailure)
        {

            return read.Error;

        }

        Result<CovenantDigest> digest = BackupRestoreJournalAuthenticator.EnvelopeDigest(read.Value);

        if (digest.IsFailure)
        {

            return digest.Error;

        }

        if (digest.Value != terminal.EnvelopeDigest || digest.Value != terminal.Anchor.EnvelopeDigest)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The journal on disk is not the terminal revision this anchor commits to.");

        }

        Result<BackupRestoreJournalPayloadV2> payload = OpenEnvelope(
            profileNamespace,
            terminal.Anchor.InstallationId,
            read.Value);

        if (payload.IsFailure)
        {

            return payload.Error;

        }

        Result closed = WriteAnchorAndVerify(profileNamespace, tombstone);

        if (closed.IsFailure)
        {

            return closed;

        }

        return DeleteJournal(terminal.Location);

    }

    /// <summary>
    /// The current anchor, proven absence, or a typed failure — never a repaired value.
    /// </summary>
    internal Result<BackupRestoreJournalAnchorV1?> TryReadAnchor(
        BackupRestoreProfileNamespace profileNamespace)
    {

        ArgumentNullException.ThrowIfNull(profileNamespace);

        OsCredentialStoreResult result;

        try
        {

            result = _credentials.TryGet(
                ArcanumCredentialIdentity.Service,
                AnchorAccount(profileNamespace));

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's restore journal anchor could not be read.");

        }

        if (result.Status is OsCredentialStoreStatus.NotFound)
        {

            return Result<BackupRestoreJournalAnchorV1?>.Success(null);

        }

        if (result.Status is not OsCredentialStoreStatus.Ok)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's restore journal anchor could not be read.");

        }

        Result<BackupRestoreJournalAnchorV1> decoded =
            BackupRestoreJournalAuthenticator.DecodeAnchor(result.Value);

        return decoded.IsFailure
            ? Result<BackupRestoreJournalAnchorV1?>.Failure(decoded.Error)
            : decoded.Value;

    }

    // ------------------------------------------------------------------ internals

    private Result<BackupRestoreJournalPublication> Publish(
        BackupRestoreProfileNamespace profileNamespace,
        BackupRestoreJournalLocation location,
        BackupRestoreJournalAnchorV1 anchor,
        BackupRestoreJournalPayloadV2 payload,
        CovenantDigest previousEnvelopeDigest)
    {

        Result<BackupRestoreJournalKeyLease> sealing = _keys.OpenExisting(profileNamespace);

        if (sealing.IsFailure)
        {

            return Result<BackupRestoreJournalPublication>.Failure(sealing.Error);

        }

        ulong revision = checked(anchor.Revision + 1);

        Result<BackupRestoreJournalEnvelopeV2> sealed_;

        using (sealing.Value)
        {

            sealed_ = BackupRestoreJournalAuthenticator.Seal(
                sealing.Value,
                profileNamespace.Digest,
                location.InstallationId,
                revision,
                previousEnvelopeDigest,
                payload);

        }

        if (sealed_.IsFailure)
        {

            return Result<BackupRestoreJournalPublication>.Failure(sealed_.Error);

        }

        Result<CovenantDigest> digest = BackupRestoreJournalAuthenticator.EnvelopeDigest(sealed_.Value);

        if (digest.IsFailure)
        {

            return Result<BackupRestoreJournalPublication>.Failure(digest.Error);

        }

        Result written = WriteJournalDurably(location, sealed_.Value);

        if (written.IsFailure)
        {

            return Result<BackupRestoreJournalPublication>.Failure(written.Error);

        }

        // Reread and reauthenticate what actually landed before the anchor is allowed to commit to it.
        Result<BackupRestoreJournalEnvelopeV2> reread = ReadJournal(location);

        if (reread.IsFailure)
        {

            return Result<BackupRestoreJournalPublication>.Failure(reread.Error);

        }

        Result<CovenantDigest> rereadDigest =
            BackupRestoreJournalAuthenticator.EnvelopeDigest(reread.Value);

        if (rereadDigest.IsFailure)
        {

            return Result<BackupRestoreJournalPublication>.Failure(rereadDigest.Error);

        }

        if (rereadDigest.Value != digest.Value)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This restore journal revision did not read back as written.");

        }

        Result<BackupRestoreJournalPayloadV2> opened = OpenEnvelope(
            profileNamespace,
            location.InstallationId,
            reread.Value);

        if (opened.IsFailure)
        {

            return Result<BackupRestoreJournalPublication>.Failure(opened.Error);

        }

        // The anchor is reread and compared once more before it advances, so a concurrent writer that
        // moved it between the first compare and here loses rather than silently wins.
        Result compared = RequireAnchorMatches(profileNamespace, anchor);

        if (compared.IsFailure)
        {

            return Result<BackupRestoreJournalPublication>.Failure(compared.Error);

        }

        BackupRestoreJournalAnchorV1 advanced = anchor with
        {
            Revision = revision,

            EnvelopeDigest = digest.Value,
        };

        Result stored = WriteAnchorAndVerify(profileNamespace, advanced);

        return stored.IsFailure
            ? Result<BackupRestoreJournalPublication>.Failure(stored.Error)
            : new BackupRestoreJournalPublication(
                location,
                reread.Value,
                digest.Value,
                opened.Value,
                advanced);

    }

    private Result<BackupRestoreJournalPayloadV2> OpenEnvelope(
        BackupRestoreProfileNamespace profileNamespace,
        Guid installationId,
        BackupRestoreJournalEnvelopeV2 envelope)
    {

        // The profile namespace and installation are compared before the key account is consulted, so
        // a cross-profile envelope fails without this profile's key ever being read.
        if (!envelope.ProfileNamespaceDigest.IsValid
            || envelope.ProfileNamespaceDigest != profileNamespace.Digest)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This restore journal envelope was written under a different profile namespace.");

        }

        Result<BackupRestoreJournalKeyLease> key = _keys.OpenExisting(profileNamespace);

        if (key.IsFailure)
        {

            // Only a proven absence becomes the manual blocker. A backend that could not be reached
            // keeps its own Unavailable code: the remedy for a locked keychain is to unlock it and
            // retry, and reporting it as a missing journal key would send the operator to restore
            // recovery for a credential store that is merely closed.
            return key.Error.Code == ErrorCodes.Covenant.NotFound
                ? new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "An active restore journal exists without this profile's journal key.")
                : Result<BackupRestoreJournalPayloadV2>.Failure(key.Error);

        }

        using (key.Value)
        {

            return BackupRestoreJournalAuthenticator.Open(
                key.Value,
                profileNamespace.Digest,
                installationId,
                envelope);

        }

    }

    /// <summary>
    /// The exactly-one staging root whose own identity produces the anchor's journal-location digest.
    /// </summary>
    private static Result<BackupRestoreJournalLocation> LocateExactly(
        BackupRestoreProfileNamespace profileNamespace,
        BackupRestoreJournalAnchorV1 anchor,
        IReadOnlyList<string> candidateStagingRoots)
    {

        // Keyed by physical identity, not by list position. The caller's set is the canonical sweep
        // unioned with the staging index, and for a ReplaceInstallation restore those two name the
        // same directory — counting list elements would make the ordinary case look like two rival
        // staging roots and refuse a single authentic journal. Two spellings of one directory are one
        // directory, and the identity the loop already computes is what says so.
        Dictionary<CovenantDigest, BackupRestoreJournalLocation> matches = [];

        foreach (string candidate in candidateStagingRoots)
        {

            if (string.IsNullOrWhiteSpace(candidate))
            {

                continue;

            }

            Result<CovenantDigest> identity = StagingRootIdentity(candidate, out string full);

            if (identity.IsFailure)
            {

                continue;

            }

            Result<CovenantDigest> location = BackupRestoreJournalAuthenticator.JournalLocation(
                anchor.ProfileNamespaceDigest,
                anchor.InstallationId,
                anchor.OperationId,
                identity.Value,
                JournalFileName);

            if (location.IsFailure || location.Value != anchor.JournalLocationDigest)
            {

                continue;

            }

            matches[identity.Value] = new BackupRestoreJournalLocation(
                profileNamespace.Digest,
                anchor.InstallationId,
                anchor.OperationId,
                full,
                identity.Value,
                location.Value);

        }

        return matches.Count switch
        {
            1 => matches.Values.First(),

            0 => new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "No swept staging root has the physical identity this restore anchor commits to."),

            _ => new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "More than one swept staging root claims the location this restore anchor commits to."),
        };

    }

    private static Result<CovenantDigest> StagingRootIdentity(string stagingRoot, out string full)
    {

        full = string.Empty;

        try
        {

            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));

        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore staging root must be a resolvable path.");

        }

        return FileHandleIdentityInterop.TryGetPathMetadataNoFollow(full, out FileHandleMetadata metadata)
            && metadata.Kind is FileSystemObjectKind.Directory
                ? BackupRestoreJournalAuthenticator.PhysicalIdentity(
                    metadata.Identity.VolumeId,
                    metadata.Identity.FileId)
                : new Error(
                    ErrorCodes.Covenant.Unavailable,
                    "A restore staging root could not be opened as a directory without following links.");

    }

    /// <summary>
    /// Whether a canonical journal is present, or a typed refusal when that cannot be established.
    /// </summary>
    /// <remarks>
    /// An unreadable candidate is not an absent one. <see cref="File.Exists(string)"/> answers
    /// <see langword="false"/> for a permission error just as it does for a missing file, so a
    /// same-user attacker could hide an unanchored journal behind a mode-000 staging directory and
    /// have proven absence reported over it. Absence has to be proved, and an inaccessible path
    /// proves nothing.
    /// </remarks>
    private static Result<bool> HasJournalFile(string stagingRoot)
    {

        try
        {

            string root = Path.GetFullPath(stagingRoot);

            if (File.Exists(Path.Combine(root, JournalFileName)))
            {

                return true;

            }

            // The directory has to be readable for its emptiness to mean anything. A canonical
            // staging root that cannot be enumerated is indeterminate, not empty.
            return Directory.Exists(root) && !CanEnumerate(root)
                ? new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "A canonical restore staging root could not be read, so its journal is neither "
                    + "present nor proven absent.")
                : false;

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
        {

            return new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "A canonical restore staging root could not be read, so its journal is neither "
                + "present nor proven absent.");

        }

    }

    private static bool CanEnumerate(string directory)
    {

        try
        {

            using IEnumerator<string> entries =
                Directory.EnumerateFileSystemEntries(directory).GetEnumerator();

            _ = entries.MoveNext();

            return true;

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return false;

        }

    }

    private static Result RequireJournalAbsent(BackupRestoreJournalLocation location)
    {

        Result<bool> present = HasJournalFile(location.StagingRoot);

        if (present.IsFailure)
        {

            return present.Error;

        }

        return present.Value
            ? new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "A restore journal already exists at the location this operation would open.")
            : Result.Success();

    }

    /// <summary>
    /// Same-directory owner-only temporary file, file fsync, atomic replace, then parent fsync.
    /// </summary>
    /// <remarks>
    /// The parent flush is the step the pre-Covenant journal writer omits. Without it the rename can
    /// outrun the directory entry that publishes it, and recovery would find an anchor naming a file
    /// the filesystem has not yet admitted exists.
    /// </remarks>
    private static Result WriteJournalDurably(
        BackupRestoreJournalLocation location,
        BackupRestoreJournalEnvelopeV2 envelope)
    {

        Result<byte[]> encoded = BackupRestoreJournalAuthenticator.EncodeEnvelope(envelope);

        if (encoded.IsFailure)
        {

            return encoded.Error;

        }

        string path = Path.Combine(location.StagingRoot, JournalFileName);

        string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {

            using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(temporaryPath))
            {

                stream.Write(encoded.Value);

                stream.Flush(flushToDisk: true);

            }

            File.Move(temporaryPath, path, overwrite: true);

            SecureFilePermissions.ApplyOwnerOnlyFile(path);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            TryDeleteTemporary(temporaryPath);

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This restore journal revision could not be written durably.");

        }

        return FlushParent(location.StagingRoot);

    }

    private static Result DeleteJournal(BackupRestoreJournalLocation location)
    {

        try
        {

            File.Delete(Path.Combine(location.StagingRoot, JournalFileName));

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This restore journal could not be removed after its anchor closed.");

        }

        return FlushParent(location.StagingRoot);

    }

    private static Result FlushParent(string directory)
    {

        if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                directory,
                out SafeFileHandle handle,
                out _))
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The restore journal's directory could not be opened to prove its durability.");

        }

        using (handle)
        {

            return BackupRestoreJournalNativeMethods.TryFlushDirectory(handle)
                ? Result.Success()
                : new Error(
                    ErrorCodes.Covenant.Unavailable,
                    "The restore journal's directory entry could not be flushed.");

        }

    }

    private static void TryDeleteTemporary(string temporaryPath)
    {

        try
        {

            File.Delete(temporaryPath);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

        }

    }

    private static Result<BackupRestoreJournalEnvelopeV2> ReadJournal(
        BackupRestoreJournalLocation location)
    {

        string path = Path.Combine(location.StagingRoot, JournalFileName);

        byte[] bytes;

        try
        {

            if (!File.Exists(path))
            {

                return new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "An active restore anchor names a journal that is not present.");

            }

            FileInfo info = new(path);

            if (info.Length > BackupRestoreJournalAuthenticator.MaxJournalFileBytes)
            {

                return new Error(
                    ErrorCodes.Covenant.CapacityExceeded,
                    "This restore journal exceeds the bound one guarded-root file may carry.");

            }

            bytes = File.ReadAllBytes(path);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This restore journal could not be read.");

        }

        return BackupRestoreJournalAuthenticator.DecodeEnvelope(bytes);

    }

    private Result RequireAnchorMatches(
        BackupRestoreProfileNamespace profileNamespace,
        BackupRestoreJournalAnchorV1 expected)
    {

        Result<BackupRestoreJournalAnchorV1?> current = TryReadAnchor(profileNamespace);

        if (current.IsFailure)
        {

            return current.Error;

        }

        return current.Value == expected
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.RevisionConflict,
                "This profile's restore journal anchor is not the revision this transition read.");

    }

    private Result WriteAnchorAndVerify(
        BackupRestoreProfileNamespace profileNamespace,
        BackupRestoreJournalAnchorV1 anchor)
    {

        Result<string> encoded = BackupRestoreJournalAuthenticator.EncodeAnchor(anchor);

        if (encoded.IsFailure)
        {

            return encoded.Error;

        }

        OsCredentialStoreResult written;

        try
        {

            written = _credentials.Set(
                ArcanumCredentialIdentity.Service,
                AnchorAccount(profileNamespace),
                encoded.Value);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's restore journal anchor could not be written.");

        }

        if (written.Status is not OsCredentialStoreStatus.Ok)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's restore journal anchor could not be written.");

        }

        Result<BackupRestoreJournalAnchorV1?> readback = TryReadAnchor(profileNamespace);

        if (readback.IsFailure)
        {

            return readback.Error;

        }

        return readback.Value == anchor
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This profile's restore journal anchor did not read back as written.");

    }

    private static Result Assert(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace,
        BackupRestoreJournalLocation location,
        BackupRestoreJournalPayloadV2 payload)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentNullException.ThrowIfNull(profileNamespace);

        ArgumentNullException.ThrowIfNull(location);

        ArgumentNullException.ThrowIfNull(payload);

        // Identity, not liveness, and deliberately no acquisition.
        heldInstallationLock.AssertHeldFor(guardedDirectory);

        if (location.ProfileNamespaceDigest != profileNamespace.Digest)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This restore journal location belongs to a different profile namespace.");

        }

        return BackupRestoreJournalAuthenticator.ValidatePayload(payload);

    }

    private static string AnchorAccount(BackupRestoreProfileNamespace profileNamespace) =>
        ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(profileNamespace.AccountSuffix);

}

/// <summary>
/// The one native call the restore journal's durability barrier needs.
/// </summary>
/// <remarks>
/// Kept beside its only caller rather than added to the shared identity interop, for the same reason
/// the Campaign marker protocol keeps its own: a shared surface invites a caller who wants "an fsync"
/// to reach for one on a handle nobody proved. Windows exposes no directory-handle flush and journals
/// directory metadata itself, so the barrier is satisfied there rather than demonstrated — stated
/// rather than papered over, because a call that silently succeeded where it does nothing would let
/// the anchor claim a durability guarantee it never obtained (§10.17).
/// </remarks>
internal static partial class BackupRestoreJournalNativeMethods
{

    internal static bool TryFlushDirectory(SafeFileHandle directory)
    {

        if (OperatingSystem.IsWindows())
        {

            return true;

        }

        if (directory is null || directory.IsInvalid || directory.IsClosed)
        {

            return false;

        }

        int descriptor = directory.DangerousGetHandle().ToInt32();

        return descriptor >= 0 && Fsync(descriptor) == 0;

    }

    [System.Runtime.InteropServices.LibraryImport(
        "libc",
        EntryPoint = "fsync",
        SetLastError = true)]
    private static partial int Fsync(int fileDescriptor);

}
