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
}

/// <summary>How the repair puts a damaged identity right.</summary>
internal enum InstallationIdentityRepairAction : byte
{
    /// <summary>There is nothing to put right.</summary>
    None = 0,

    /// <summary>No anchor names an installation, so the damaged item is removed.</summary>
    Remove = 1,

    /// <summary>A closed anchor names the installation, so it is written back from that anchor.</summary>
    Restore = 2,
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
/// exists to pin. That refusal has one way out, which is to put the item right, and this is the only
/// code that does so. How depends on what else the profile still names.
///
/// <para>The restore journal and the offline-transition journal each keep their anchor after the work
/// closes, as a tombstone that still names its installation (§10.19.6), and every later transition and
/// reset compares that name with the identity. So an identity that a closed anchor names is written
/// back from the anchor, never removed: removing it would let the next start mint another, and the
/// transition journal would then refuse every later opening beside the anchor that names the old one,
/// for good. An identity that no anchor names is removed, and the next Grimoire takes a fresh one.</para>
///
/// <para>It changes that one item (the identity account of this profile's namespace, under the Arcanum
/// credential service) and nothing else, and only when every one of these holds, each proved under the
/// exact installation lock this call takes for itself: the stored value is present and is not one
/// canonical identity (a valid identity is never changed, and an unreadable store is not a damaged
/// value); no installation reset record is active, which the startup probe proves by the record and by
/// the key and anchor credentials that authenticate it; no restore or offline transition is
/// unfinished, which is to say that no anchor of either is active and no restore journal is in flight;
/// the closed anchors that remain agree on one installation; and the Grimoire has no database file or
/// no authority row, so no installation exists that the item could be named for. Any of those that
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

        Result<Guid?> named = await ProveNothingIsActiveAsync(
            held,
            profile.Value,
            cancellationToken).ConfigureAwait(false);

        if (named.IsFailure)
        {
            return Result<InstallationIdentityRepairReport>.Failure(named.Error);
        }

        InstallationIdentityRepairAction action = named.Value is null
            ? InstallationIdentityRepairAction.Remove
            : InstallationIdentityRepairAction.Restore;

        if (!apply)
        {
            return Report(InstallationIdentityRepairState.Damaged, account, action);
        }

        if (named.Value is not { } installation)
        {
            Result<bool> removed = _identities.RemoveDamaged(held, _guardedRoot, profile.Value);

            if (removed.IsFailure)
            {
                return Result<InstallationIdentityRepairReport>.Failure(removed.Error);
            }

            return Report(
                removed.Value
                    ? InstallationIdentityRepairState.Removed
                    : InstallationIdentityRepairState.Absent,
                account,
                action);
        }

        Result<bool> restored = _identities.ReplaceDamagedFromClosedEvidence(
            held,
            _guardedRoot,
            profile.Value,
            installation);

        if (restored.IsFailure)
        {
            return Result<InstallationIdentityRepairReport>.Failure(restored.Error);
        }

        return Report(
            restored.Value
                ? InstallationIdentityRepairState.Restored
                : InstallationIdentityRepairState.Absent,
            account,
            action);
    }

    /// <summary>
    /// Proves that nothing is active that the identity is bound to, and reports the installation the
    /// closed evidence names, or none.
    /// </summary>
    /// <remarks>
    /// Active evidence is unfinished work and blocks the repair: an installation reset record, a
    /// restore, an offline transition. Closed evidence is the kept tombstone of work that finished, and
    /// it does not block: it names the installation the identity has to go on naming. A key with no
    /// anchor names nothing and is not evidence of anything in flight (§10.19.6).
    /// </remarks>
    private async Task<Result<Guid?>> ProveNothingIsActiveAsync(
        ArcanumMaintenanceLock held,
        BackupRestoreProfileNamespace profile,
        CancellationToken cancellationToken)
    {
        Result<ActiveInstallationReset?> reset = await _startupProbe
            .ReadActiveResetAsync(cancellationToken)
            .ConfigureAwait(false);

        if (reset.IsFailure)
        {
            return RefuseNaming(
                ErrorCodes.Data.ControlPathUnavailable,
                "Whether an installation reset is active could not be proved, so nothing was changed. "
                + reset.Error.Message);
        }

        if (reset.Value is not null)
        {
            return RefuseNaming(
                ErrorCodes.Data.ResetInProgress,
                "An installation reset is active. Resume it with 'arcanum data factory-reset' and the "
                + "scope it names, and run the repair again afterwards. Nothing was changed.");
        }

        Result<Guid?> restore = await ReadRestoreSlotAsync(profile, cancellationToken).ConfigureAwait(false);

        if (restore.IsFailure)
        {
            return restore;
        }

        Result<Guid?> transition = ReadTransitionSlot();

        if (transition.IsFailure)
        {
            return transition;
        }

        if (restore.Value is { } fromRestore
            && transition.Value is { } fromTransition
            && fromRestore != fromTransition)
        {
            return RefuseNaming(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "The closed restore anchor and the closed offline-transition anchor of this profile name "
                + "different installations, so there is no one identity to put back. Nothing was changed.");
        }

        Guid? named = restore.Value ?? transition.Value;

        if (File.Exists(_canonicalDatabasePath))
        {
            Result<bool> row = await _stoppedHostData
                .ReadAuthorityRowPresenceUnderStoppedHostAuthorityAsync(
                    new StoppedHostGrimoireAuthorityIssuer(
                        held,
                        _guardedRoot,
                        _canonicalDatabasePath),
                    cancellationToken)
                .ConfigureAwait(false);

            if (row.IsFailure)
            {
                return RefuseNaming(
                    ErrorCodes.Data.InventoryUnavailable,
                    "Whether the Grimoire holds its authority row could not be proved, so nothing was "
                    + "changed. The repair changes the identity only for a Grimoire that has no database "
                    + "or no authority row.");
            }

            if (row.Value)
            {
                return RefuseNaming(
                    ErrorCodes.Data.Blocked,
                    "The Grimoire holds its authority row, so an installation exists that this identity "
                    + "is the external binding of. The repair applies only before a Grimoire exists, so "
                    + "nothing was changed.");
            }
        }

        return Result<Guid?>.Success(named);
    }

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

    private static Result<InstallationIdentityRepairReport> Refuse(string code, string message) =>
        Result<InstallationIdentityRepairReport>.Failure(new Error(code, message));
}
