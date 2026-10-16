using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>What the repair found, or did, to the profile's installation-identity credential item.</summary>
internal enum InstallationIdentityRepairState : byte
{
    /// <summary>The profile carries no such item. There is nothing to repair.</summary>
    Absent = 1,

    /// <summary>The item is present and is not one canonical identity. A repair would remove it.</summary>
    Damaged = 2,

    /// <summary>The damaged item was removed and its absence was read back.</summary>
    Removed = 3,
}

/// <summary>
/// The one credential-store item the repair concerns, named without its value.
/// </summary>
/// <param name="CredentialService">The credential service the item is stored under.</param>
/// <param name="CredentialAccount">
/// The account: a fixed prefix and this profile root's namespace digest. Neither it nor the service
/// is a secret; the stored value is never read into a report.
/// </param>
internal sealed record InstallationIdentityRepairReport(
    InstallationIdentityRepairState State,
    string CredentialService,
    string CredentialAccount);

/// <summary>
/// The guarded repair of a profile's damaged pre-database installation identity.
/// </summary>
/// <remarks>
/// The identity item outlives every Grimoire (§10.19.13), and a Grimoire is never created over a value
/// that is present and not one canonical identity: minting over it would silently replace what it
/// exists to pin. That refusal has exactly one way out, which is to remove the item, and this is the
/// only code that does so.
///
/// <para>It removes that one item (the identity account of this profile's namespace, under the
/// Arcanum credential service) and nothing else, and only when every one of these holds, each proved
/// under the exact installation lock this call takes for itself: the stored value is present and
/// is not one canonical identity (a valid identity is never removed, and an unreadable store is not a
/// damaged value); no installation reset record is active, which the startup probe proves by the
/// record and by the key and anchor credentials that authenticate it; none of the restore or
/// offline-transition key and anchor credentials of this profile exists, so no evidence is bound to
/// the identity; and the Grimoire has no database file or no authority row, so no installation exists
/// that the item could be named for. Any of those that cannot be proved is a refusal, never a guess.
/// The lock is what proves no host is running: a running host, a restore and a reset all hold it.</para>
///
/// <para>This type names no account. Each credential is asked about by its own owner (the identity
/// provider, the two restore-journal stores, the two offline-transition stores) and the one removal is
/// the identity provider's, so the account names keep the single owners the call-site pins hold them
/// to.</para>
///
/// <para>Planning and applying are separate calls with the same checks, so the operator confirms
/// against the state the apply then finds again. An apply that finds the item already gone reports
/// that, and removes nothing.</para>
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
    /// Proves every precondition again and removes the damaged item.
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

        Result<InstallationIdentityRepairReport> evidence = await RequireNoBoundEvidenceAsync(
            held,
            profile.Value,
            account,
            cancellationToken).ConfigureAwait(false);

        if (evidence.IsFailure)
        {
            return evidence;
        }

        if (!apply)
        {
            return Report(InstallationIdentityRepairState.Damaged, account);
        }

        Result<bool> removed = _identities.RemoveDamaged(held, _guardedRoot, profile.Value);

        if (removed.IsFailure)
        {
            return Result<InstallationIdentityRepairReport>.Failure(removed.Error);
        }

        return Report(
            removed.Value
                ? InstallationIdentityRepairState.Removed
                : InstallationIdentityRepairState.Absent,
            account);
    }

    private async Task<Result<InstallationIdentityRepairReport>> RequireNoBoundEvidenceAsync(
        ArcanumMaintenanceLock held,
        BackupRestoreProfileNamespace profile,
        string account,
        CancellationToken cancellationToken)
    {
        Result<ActiveInstallationReset?> reset = await _startupProbe
            .ReadActiveResetAsync(cancellationToken)
            .ConfigureAwait(false);

        if (reset.IsFailure)
        {
            return Refuse(
                ErrorCodes.Data.ControlPathUnavailable,
                "Whether an installation reset is active could not be proved, so nothing was changed. "
                + reset.Error.Message);
        }

        if (reset.Value is not null)
        {
            return Refuse(
                ErrorCodes.Data.ResetInProgress,
                "An installation reset is active. Resume it with 'arcanum data factory-reset' and the "
                + "scope it names, and run the repair again afterwards. Nothing was changed.");
        }

        // Each credential is asked about by the type that owns its account. The reset record's own key
        // and anchor are not listed: the startup probe above proves them with the record, and refuses
        // a key or an anchor it cannot pair with one.
        (string Name, OsCredentialStoreStatus Status)[] evidence =
        [
            ("restore journal key", new BackupRestoreJournalKeyProvider(_credentials).ProbePresence(profile)),
            (
                "restore journal anchor",
                new BackupRestoreJournalAnchorStore(
                    _credentials,
                    new BackupRestoreJournalKeyProvider(_credentials),
                    _identities).ProbeAnchorPresence(profile)),
            (
                "offline transition journal key",
                new GrimoireOfflineTransitionJournalKeyProvider(_credentials).ProbePresence(profile)),
            (
                "offline transition journal anchor",
                new GrimoireOfflineTransitionJournalAnchorStore(_credentials).ProbeAnchorPresence(profile)),
        ];

        foreach ((string name, OsCredentialStoreStatus status) in evidence)
        {

            if (status is OsCredentialStoreStatus.NotFound)
            {
                continue;
            }

            return status is OsCredentialStoreStatus.Ok
                ? Refuse(
                    ErrorCodes.Data.Blocked,
                    $"This profile carries a {name} credential, so evidence is bound to the installation "
                    + "identity. Finish or close the restore, offline transition, or installation reset "
                    + "that owns it; the repair removes only an identity nothing is bound to. Nothing was "
                    + "changed.")
                : Refuse(
                    ErrorCodes.Covenant.Unavailable,
                    $"Whether this profile carries a {name} credential could not be proved, so nothing "
                    + "was changed. Unlock or repair the operating-system credential store and run the "
                    + "repair again.");
        }

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
                return Refuse(
                    ErrorCodes.Data.InventoryUnavailable,
                    "Whether the Grimoire holds its authority row could not be proved, so nothing was "
                    + "changed. The repair removes the identity only for a Grimoire that has no database "
                    + "or no authority row.");
            }

            if (row.Value)
            {
                return Refuse(
                    ErrorCodes.Data.Blocked,
                    "The Grimoire holds its authority row, so an installation exists that this identity "
                    + "is the external binding of. The repair applies only before a Grimoire exists, so "
                    + "nothing was changed.");
            }
        }

        return Report(InstallationIdentityRepairState.Damaged, account);
    }

    private static Result<InstallationIdentityRepairReport> Report(
        InstallationIdentityRepairState state,
        string account) =>
        new InstallationIdentityRepairReport(state, ArcanumCredentialIdentity.Service, account);

    private static Result<InstallationIdentityRepairReport> Refuse(string code, string message) =>
        Result<InstallationIdentityRepairReport>.Failure(new Error(code, message));
}
