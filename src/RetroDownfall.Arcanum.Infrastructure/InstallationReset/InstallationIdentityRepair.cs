using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>What the repair found, or did, to the profile's installation-identity credential item.</summary>
internal enum InstallationIdentityRepairState : byte
{
    /// <summary>The profile carries no such item. There is nothing to repair.</summary>
    Absent = 1,

    /// <summary>The item is present and is not one canonical identity. A repair would put it right.</summary>
    Damaged = 2,

    /// <summary>The damaged item was removed and its absence was read back.</summary>
    Removed = 3,

    /// <summary>The damaged item was replaced by the identity a closed anchor names, and read back.</summary>
    Restored = 4,

    /// <summary>The damaged item was replaced by the identity the Grimoire's authority row carries, and read back.</summary>
    Reseeded = 5,
}

/// <summary>How the repair puts a damaged identity right.</summary>
internal enum InstallationIdentityRepairAction : byte
{
    /// <summary>There is nothing to put right.</summary>
    None = 0,

    /// <summary>The Grimoire has no authority row and no anchor names an installation, so the item is removed.</summary>
    Remove = 1,

    /// <summary>The Grimoire has no authority row and a closed anchor names the installation, so it is written back from that anchor.</summary>
    Restore = 2,

    /// <summary>The Grimoire holds its authority row, so the item is seeded again from that row.</summary>
    Reseed = 3,
}

/// <summary>
/// The one credential-store item the repair concerns, named without its value.
/// </summary>
/// <param name="State">What the repair found, or did.</param>
/// <param name="CredentialService">The credential service the item is stored under.</param>
/// <param name="CredentialAccount">
/// The account: a fixed prefix and this profile root's namespace digest. Neither it nor the service
/// is a secret; the stored value is never read into a report.
/// </param>
/// <param name="Action">How a damaged item is, or was, put right.</param>
internal sealed record InstallationIdentityRepairReport(
    InstallationIdentityRepairState State,
    string CredentialService,
    string CredentialAccount,
    InstallationIdentityRepairAction Action = InstallationIdentityRepairAction.None);

/// <summary>
/// The guarded repair of a profile's damaged pre-database installation identity.
/// </summary>
/// <remarks>
/// The identity item outlives every Grimoire (§10.19.13), and a Grimoire is never created over a value
/// that is present and not one canonical identity: minting over it would silently replace what it
/// exists to pin, and a start of one that already exists is refused beside it whenever a closed anchor
/// is compared with it. That refusal has one way out, which is to put the item right, and this is the
/// only code that does so. How depends on what else the profile still names.
///
/// <para>A Grimoire that holds its authority row carries the installation the item exists to pin, so
/// the item is seeded again from that row: the same identity the restores, resets and offline
/// transitions seed an absent one with, never one invented here. Every closed anchor that remains has
/// to name that installation; one that names another is a conflict between two records of the
/// installation, which no automatic remedy settles, and the repair refuses.</para>
///
/// <para>With no Grimoire row the closed anchors are the only record. The restore journal and the
/// offline-transition journal each keep their anchor after the work closes, as a tombstone that still
/// names its installation (§10.19.6), and every later transition and reset compares that name with the
/// identity. So an identity that a closed anchor names is written back from the anchor, never removed:
/// removing it would let the next start mint another, and the transition journal would then refuse
/// every later opening beside the anchor that names the old one, for good. An identity that no anchor
/// names is removed, and the next Grimoire takes a fresh one.</para>
///
/// <para>It changes that one item (the identity account of this profile's namespace, under the Arcanum
/// credential service) and nothing else, and only when every one of these holds, each proved under the
/// exact installation lock this call takes for itself: the stored value is present and is not one
/// canonical identity (a valid identity is never changed, and an unreadable store is not a damaged
/// value); no installation reset record is active, which the startup probe proves by the record and by
/// the key and anchor credentials that authenticate it; no restore or offline transition is
/// unfinished, which is to say that no anchor of either is active and no restore journal is in flight;
/// the closed anchors that remain agree on one installation, and on the Grimoire's when its row
/// exists; and the Grimoire's row, when there is a database file, can be read. Any of those that
/// cannot be proved is a refusal, never a guess. The lock is what proves no host is running: a running
/// host, a restore and a reset all hold it.</para>
///
/// <para>This type names no account. Each credential is asked about by its own owner (the identity
/// provider, the restore-journal anchor store, the offline-transition anchor store) and the one change
/// is the identity provider's, so the account names keep the single owners the call-site pins hold
/// them to.</para>
///
/// <para>Planning and applying are separate calls with the same checks, so the operator confirms
/// against the state the apply then finds again. An apply that finds the item already gone reports
/// that, and changes nothing.</para>
/// </remarks>
internal sealed class InstallationIdentityRepair(
    IOsCredentialStore credentials,
    IInstallationStartupProbe startupProbe,
    IInstallationResetStoppedHostDataService stoppedHostData,
    string guardedRoot,
    string canonicalDatabasePath)
{
    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    private readonly BackupRestoreJournalInstallationIdentityProvider _identities =
        new(credentials);

    private readonly IInstallationStartupProbe _startupProbe =
        startupProbe ?? throw new ArgumentNullException(nameof(startupProbe));

    private readonly IInstallationResetStoppedHostDataService _stoppedHostData =
        stoppedHostData ?? throw new ArgumentNullException(nameof(stoppedHostData));

    private readonly string _guardedRoot = Path.GetFullPath(
        string.IsNullOrWhiteSpace(guardedRoot)
            ? throw new ArgumentException("A guarded root is required.", nameof(guardedRoot))
            : guardedRoot);

    private readonly string _canonicalDatabasePath = Path.GetFullPath(
        string.IsNullOrWhiteSpace(canonicalDatabasePath)
            ? throw new ArgumentException("A database path is required.", nameof(canonicalDatabasePath))
            : canonicalDatabasePath);

    /// <summary>
    /// Proves every precondition and reports what an apply would do. Changes nothing.
    /// </summary>
    public Task<Result<InstallationIdentityRepairReport>> PlanAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(apply: false, cancellationToken);

    /// <summary>
    /// Proves every precondition again and puts the damaged item right.
    /// </summary>
    public Task<Result<InstallationIdentityRepairReport>> ApplyAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(apply: true, cancellationToken);

    private async Task<Result<InstallationIdentityRepairReport>> RunAsync(
        bool apply,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ArcanumMaintenanceLockAcquisitionResult acquisition;

        try
        {
            acquisition = ArcanumMaintenanceLock.AcquireDetailed(_guardedRoot);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {
            return Refuse(
                ErrorCodes.Data.ControlPathUnavailable,
                "The installation maintenance lock could not be taken safely, so nothing was changed.");
        }

        if (acquisition.Disposition is ArcanumMaintenanceLockAcquisitionDisposition.Unsafe)
        {
            return Refuse(
                ErrorCodes.Data.ControlPathUnavailable,
                "The installation maintenance lock could not be taken safely because its topology, "
                + "identity, or owner-only permissions could not be validated, so nothing was changed.");
        }

        if (acquisition.Disposition is ArcanumMaintenanceLockAcquisitionDisposition.Contended)
        {
            return Refuse(
                ErrorCodes.Data.FileLocked,
                "A running host, a backup restore, or an installation reset holds the installation "
                + "maintenance lock. Stop the host with 'arcanum serve quit', let any restore or reset "
                + "finish, and run the repair again. Nothing was changed.");
        }

        using ArcanumMaintenanceLock held = acquisition.BorrowAcquiredLock();

        try
        {
            GrimoireGuardedRootTopology.EnsureOwnedRootIsSafe(held, _guardedRoot);
        }
        catch (InvalidOperationException)
        {
            return Refuse(
                ErrorCodes.Data.ControlPathUnavailable,
                "The profile root is not a safe ordinary directory, so nothing was changed.");
        }

        Result<BackupRestoreProfileNamespace> profile =
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_guardedRoot);

        if (profile.IsFailure)
        {
            return Refuse(
                ErrorCodes.Data.ControlPathUnavailable,
                "This profile's root could not be resolved safely, so its installation identity cannot "
                + "be located and nothing was changed.");
        }

        string account = _identities.AccountFor(profile.Value);

        Result<BackupRestoreJournalIdentityProbe> identity = _identities.Probe(profile.Value);

        if (identity.IsSuccess)
        {
            return identity.Value.Presence is BackupRestoreJournalIdentityPresence.Absent
                ? Report(InstallationIdentityRepairState.Absent, account)
                : Refuse(
                    ErrorCodes.Data.Conflict,
                    "This profile's installation identity is a valid canonical identity. A valid "
                    + "identity is never removed, so nothing was changed.");
        }

        if (identity.Error.Code != ErrorCodes.Covenant.IntegrityFailure)
        {
            return Refuse(
                ErrorCodes.Covenant.Unavailable,
                "This profile's installation identity could not be read from the operating-system "
                + "credential store. A store that does not answer is not a damaged value, so nothing was "
                + "changed. Unlock or repair the store and run the repair again.");
        }

        Result<PutRight> chosen = await ProveNothingIsActiveAsync(
            held,
            profile.Value,
            cancellationToken).ConfigureAwait(false);

        if (chosen.IsFailure)
        {
            return Result<InstallationIdentityRepairReport>.Failure(chosen.Error);
        }

        InstallationIdentityRepairAction action = chosen.Value.Action;

        if (!apply)
        {
            return Report(InstallationIdentityRepairState.Damaged, account, action);
        }

        Result<bool> changed = action switch
        {
            InstallationIdentityRepairAction.Reseed => _identities.ReseedDamagedFromDatabase(
                held,
                _guardedRoot,
                profile.Value,
                chosen.Value.Installation),
            InstallationIdentityRepairAction.Restore => _identities.ReplaceDamagedFromClosedEvidence(
                held,
                _guardedRoot,
                profile.Value,
                chosen.Value.Installation),
            _ => _identities.RemoveDamaged(held, _guardedRoot, profile.Value),
        };

        if (changed.IsFailure)
        {
            return Result<InstallationIdentityRepairReport>.Failure(changed.Error);
        }

        return Report(
            changed.Value
                ? action switch
                {
                    InstallationIdentityRepairAction.Reseed => InstallationIdentityRepairState.Reseeded,
                    InstallationIdentityRepairAction.Restore => InstallationIdentityRepairState.Restored,
                    _ => InstallationIdentityRepairState.Removed,
                }
                : InstallationIdentityRepairState.Absent,
            account,
            action);
    }

    /// <summary>
    /// Proves that nothing is active that the identity is bound to, and chooses how the damaged item is
    /// put right.
    /// </summary>
    /// <remarks>
    /// Active evidence is unfinished work and blocks the repair: an installation reset record, a
    /// restore, an offline transition. Closed evidence is the kept tombstone of work that finished, and
    /// it does not block: it names the installation the identity has to go on naming. A key with no
    /// anchor names nothing and is not evidence of anything in flight (§10.19.6).
    ///
    /// <para>What the item is put right to depends on the Grimoire. One that holds its authority row
    /// carries the installation the binding exists to pin, so the item is seeded again from that row,
    /// and a closed anchor that names another installation is a conflict between two records of the
    /// installation that the repair does not settle. One that does not (no file, or a file that never
    /// finished its first install) leaves the closed anchors as the only record of the installation:
    /// the item is written back from the one they name, or removed where there is none.</para>
    /// </remarks>
    private async Task<Result<PutRight>> ProveNothingIsActiveAsync(
        ArcanumMaintenanceLock held,
        BackupRestoreProfileNamespace profile,
        CancellationToken cancellationToken)
    {
        Result<ActiveInstallationReset?> reset = await _startupProbe
            .ReadActiveResetAsync(cancellationToken)
            .ConfigureAwait(false);

        if (reset.IsFailure)
        {
            return RefuseChoice(
                ErrorCodes.Data.ControlPathUnavailable,
                "Whether an installation reset is active could not be proved, so nothing was changed. "
                + reset.Error.Message);
        }

        if (reset.Value is not null)
        {
            return RefuseChoice(
                ErrorCodes.Data.ResetInProgress,
                "An installation reset is active. Resume it with 'arcanum data factory-reset' and the "
                + "scope it names, and run the repair again afterwards. Nothing was changed.");
        }

        Result<Guid?> restore = await ReadRestoreSlotAsync(profile, cancellationToken).ConfigureAwait(false);

        if (restore.IsFailure)
        {
            return Result<PutRight>.Failure(restore.Error);
        }

        Result<Guid?> transition = ReadTransitionSlot();

        if (transition.IsFailure)
        {
            return Result<PutRight>.Failure(transition.Error);
        }

        if (restore.Value is { } fromRestore
            && transition.Value is { } fromTransition
            && fromRestore != fromTransition)
        {
            return RefuseChoice(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "The closed restore anchor and the closed offline-transition anchor of this profile name "
                + "different installations, so there is no one identity to put back. Nothing was changed.");
        }

        Guid? named = restore.Value ?? transition.Value;

        if (!File.Exists(_canonicalDatabasePath))
        {
            return PutRightFromAnchors(named);
        }

        StoppedHostGrimoireAuthorityIssuer issuer = new(held, _guardedRoot, _canonicalDatabasePath);

        Result<bool> row = await _stoppedHostData
            .ReadAuthorityRowPresenceUnderStoppedHostAuthorityAsync(issuer, cancellationToken)
            .ConfigureAwait(false);

        if (row.IsFailure)
        {
            return RefuseChoice(
                ErrorCodes.Data.InventoryUnavailable,
                "Whether the Grimoire holds its authority row could not be proved, so nothing was "
                + "changed. What the repair puts the item right to depends on that row, and a position "
                + "that cannot be proved is never guessed.");
        }

        if (!row.Value)
        {
            return PutRightFromAnchors(named);
        }

        Result<Guid> authority = await _stoppedHostData
            .ReadIdentityUnderStoppedHostAuthorityAsync(issuer, cancellationToken)
            .ConfigureAwait(false);

        if (authority.IsFailure || authority.Value == Guid.Empty)
        {
            return RefuseChoice(
                ErrorCodes.Data.InventoryUnavailable,
                "The installation identity of the Grimoire's authority row could not be read as one "
                + "canonical identity, so there is nothing to seed the item again from and nothing was "
                + "changed. " + (authority.IsFailure ? authority.Error.Message : string.Empty));
        }

        if (named is { } anchored && anchored != authority.Value)
        {
            // Both anchors, when both exist, name one installation (checked above), so they differ from
            // the row together.
            string subject = restore.Value is not null && transition.Value is not null
                ? "The closed restore anchor and the closed offline-transition anchor of this profile name"
                : restore.Value is not null
                    ? "The closed restore anchor of this profile names"
                    : "The closed offline-transition anchor of this profile names";

            return RefuseChoice(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                subject
                + " a different installation than the Grimoire's authority row carries, so there is no one "
                + "identity to put back, and the repair does not choose between the anchor and the "
                + "database. Nothing was changed.");
        }

        return Result<PutRight>.Success(new PutRight(InstallationIdentityRepairAction.Reseed, authority.Value));
    }

    /// <summary>
    /// The choice for a profile whose Grimoire has no authority row: the installation a closed anchor
    /// names is written back, and with none the item is removed.
    /// </summary>
    private static Result<PutRight> PutRightFromAnchors(Guid? named) =>
        Result<PutRight>.Success(
            named is { } installation
                ? new PutRight(InstallationIdentityRepairAction.Restore, installation)
                : new PutRight(InstallationIdentityRepairAction.Remove, Guid.Empty));

    /// <summary>
    /// The installation a closed restore anchor names, or none when no restore left an anchor.
    /// </summary>
    private async Task<Result<Guid?>> ReadRestoreSlotAsync(
        BackupRestoreProfileNamespace profile,
        CancellationToken cancellationToken)
    {
        BackupRestoreJournalAnchorStore anchors = new(
            _credentials,
            new BackupRestoreJournalKeyProvider(_credentials),
            _identities);

        Result<BackupRestoreJournalAnchorV1?> anchor = anchors.TryReadAnchor(profile);

        if (anchor.IsFailure)
        {
            return RefuseNaming(
                anchor.Error.Code == ErrorCodes.Covenant.Unavailable
                    ? ErrorCodes.Covenant.Unavailable
                    : ErrorCodes.Covenant.ManualRecoveryRequired,
                "Whether this profile's restore journal anchor is closed could not be proved, so nothing "
                + "was changed. " + anchor.Error.Message);
        }

        if (anchor.Value is { State: BackupRestoreJournalAnchorState.Active })
        {
            return RefuseNaming(ErrorCodes.Data.Blocked, UnfinishedRestoreMessage);
        }

        // The anchor says no restore is active; the sweep says there is no journal either, including
        // the plain journal of a build that predates the authenticated one.
        Result<ActiveReplacementRestore?> active = await new BackupRestoreActiveEvidenceInspector(
                _guardedRoot,
                _credentials)
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);

        if (active.IsFailure)
        {
            return RefuseNaming(
                active.Error.Code,
                "Whether a replacement restore is in flight for this profile could not be proved, so "
                + "nothing was changed. " + active.Error.Message);
        }

        if (active.Value is not null)
        {
            return RefuseNaming(ErrorCodes.Data.Blocked, UnfinishedRestoreMessage);
        }

        return Result<Guid?>.Success(anchor.Value?.InstallationId);
    }

    /// <summary>
    /// The installation a closed offline-transition anchor names, or none when no transition left one.
    /// </summary>
    private Result<Guid?> ReadTransitionSlot()
    {
        GrimoireOfflineTransitionJournalFileStore files = new();

        Result<GrimoireOfflineTransitionJournalLocation> location = files.ResolveLocation(_guardedRoot);

        if (location.IsFailure)
        {
            return RefuseNaming(
                ErrorCodes.Data.ControlPathUnavailable,
                "This profile's offline-transition journal could not be located safely, so nothing was "
                + "changed.");
        }

        Result<GrimoireOfflineTransitionAnchorV1?> anchor =
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(location.Value);

        if (anchor.IsFailure)
        {
            return RefuseNaming(
                anchor.Error.Code == ErrorCodes.Covenant.Unavailable
                    ? ErrorCodes.Covenant.Unavailable
                    : ErrorCodes.Covenant.ManualRecoveryRequired,
                "Whether this profile's offline-transition journal anchor is closed could not be proved, "
                + "so nothing was changed. " + anchor.Error.Message);
        }

        if (anchor.Value is { State: GrimoireOfflineTransitionAnchorState.Active })
        {
            return RefuseNaming(
                ErrorCodes.Data.Blocked,
                "An offline Grimoire transition is unfinished and its journal is bound to this "
                + "installation identity. The repair does not change an identity that unfinished work is "
                + "bound to. Nothing was changed.");
        }

        // A journal file with no anchor, or beside a closed one, is not nothing: it is evidence the
        // repair cannot classify without the recovery that needs the identity it is about to change.
        if (files.RequireNoEvidence(location.Value).IsFailure)
        {
            return RefuseNaming(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "This profile's offline-transition journal slot holds a file that no active anchor "
                + "accounts for, so whether the transition is finished could not be proved and nothing "
                + "was changed.");
        }

        return Result<Guid?>.Success(anchor.Value?.InstallationId);
    }

    private const string UnfinishedRestoreMessage =
        "A replacement restore is unfinished and its journal is bound to this installation identity. "
        + "The repair does not change an identity that unfinished work is bound to. Nothing was changed.";

    private static Result<InstallationIdentityRepairReport> Report(
        InstallationIdentityRepairState state,
        string account,
        InstallationIdentityRepairAction action = InstallationIdentityRepairAction.None) =>
        new InstallationIdentityRepairReport(state, ArcanumCredentialIdentity.Service, account, action);

    private static Result<Guid?> RefuseNaming(string code, string message) =>
        Result<Guid?>.Failure(new Error(code, message));

    private static Result<PutRight> RefuseChoice(string code, string message) =>
        Result<PutRight>.Failure(new Error(code, message));

    /// <summary>How a damaged item is put right, and the installation it is put right to.</summary>
    /// <param name="Action">The way the item is put right.</param>
    /// <param name="Installation">
    /// The installation the item is written to for <see cref="InstallationIdentityRepairAction.Restore"/>
    /// (a closed anchor's) and <see cref="InstallationIdentityRepairAction.Reseed"/> (the authority
    /// row's); empty for a removal.
    /// </param>
    private sealed record PutRight(InstallationIdentityRepairAction Action, Guid Installation);

    private static Result<InstallationIdentityRepairReport> Refuse(string code, string message) =>
        Result<InstallationIdentityRepairReport>.Failure(new Error(code, message));
}
