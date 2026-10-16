using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>Whether a profile's external installation identity exists.</summary>
internal enum BackupRestoreJournalIdentityPresence : byte
{
    Absent = 1,

    Present = 2,
}

/// <summary>
/// What one probe of the external installation identity found.
/// </summary>
/// <remarks>
/// Absence and a malformed value are different answers, and only absence is reported here — a value
/// that exists but is not one canonical identity is a typed failure, because "there is no identity"
/// is what lets an authenticated publication seed one.
/// </remarks>
internal sealed record BackupRestoreJournalIdentityProbe(
    BackupRestoreJournalIdentityPresence Presence,
    Guid InstallationId);

/// <summary>
/// The sole owner of one profile's namespaced external installation identity.
/// </summary>
/// <remarks>
/// This account is what gives each profile root an independent binding that exists before any database
/// opens. Physical recovery reads it before the live database is even classified, binds it into the
/// envelope and anchor, and the later core precheck compares it with the recovered row — so a journal,
/// key, anchor, or staged root copied in from another installation fails before topology mutation
/// rather than after it (§10.19.6).
///
/// <para>Ordinary restore, Covenant reset, family reinitialize, credential cleanup, and key rotation
/// all preserve it, and so does an ordinary installation reset, whose sweep deletes the database the
/// identity was seeded from: the Grimoire the next start creates takes the identity as its own
/// (<see cref="ReadForNewDatabase"/>). Only an attested full installation reset may remove a valid
/// identity, and only after proving no active restore owner remains. A value that is not one canonical
/// identity is the one other thing removed, by <see cref="RemoveDamaged"/>, for the installation-identity
/// repair that has proved under the held lock that nothing is bound to it.</para>
/// </remarks>
internal sealed class BackupRestoreJournalInstallationIdentityProvider(IOsCredentialStore credentials)
{
    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    /// <summary>
    /// Reads the identity, reports proven absence, or fails on anything else.
    /// </summary>
    internal Result<BackupRestoreJournalIdentityProbe> Probe(
        BackupRestoreProfileNamespace profileNamespace)
    {
        ArgumentNullException.ThrowIfNull(profileNamespace);

        OsCredentialStoreResult result;

        try
        {
            result = _credentials.TryGet(ArcanumCredentialIdentity.Service, Account(profileNamespace));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {
            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's external installation identity could not be read.");
        }

        if (result.Status is OsCredentialStoreStatus.NotFound)
        {
            return new BackupRestoreJournalIdentityProbe(
                BackupRestoreJournalIdentityPresence.Absent,
                Guid.Empty);
        }

        if (result.Status is not OsCredentialStoreStatus.Ok)
        {
            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's external installation identity could not be read.");
        }

        return TryParseCanonical(result.Value, out Guid installationId)
            ? new BackupRestoreJournalIdentityProbe(
                BackupRestoreJournalIdentityPresence.Present,
                installationId)
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This profile's external installation identity is not one canonical uppercase UUID.");
    }

    /// <summary>
    /// Seeds a missing identity from the database authority row, or requires the existing one to agree.
    /// </summary>
    /// <remarks>
    /// Called only by the three authenticated publications that may create the binding, each under the
    /// exact installation lock and after core authority is readable: a replacement restore, the
    /// installation-reset V2 record, and the offline transition's opening journal (a standalone Covenant
    /// reset included). It never runs on an ordinary bootstrap, which reads the binding and writes
    /// nothing (<see cref="ReadForNewDatabase"/>). Seeding a value that disagrees with the row would let
    /// the external binding drift from the installation it names, which is the one thing it exists to
    /// pin.
    /// </remarks>
    internal Result<Guid> SeedFromDatabase(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace,
        Guid databaseInstallationId)
    {
        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentNullException.ThrowIfNull(profileNamespace);

        // Identity, not liveness, and deliberately no acquisition.
        heldInstallationLock.AssertHeldFor(guardedDirectory);

        if (databaseInstallationId == Guid.Empty)
        {
            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A profile's external installation identity cannot be seeded from an empty row.");
        }

        Result<BackupRestoreJournalIdentityProbe> probe = Probe(profileNamespace);

        if (probe.IsFailure)
        {
            return Result<Guid>.Failure(probe.Error);
        }

        if (probe.Value.Presence is BackupRestoreJournalIdentityPresence.Present)
        {
            return probe.Value.InstallationId == databaseInstallationId
                ? probe.Value.InstallationId
                : new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "This profile's external installation identity names a different installation than "
                    + "the database row.");
        }

        return Write(profileNamespace, databaseInstallationId);
    }

    /// <summary>
    /// Creates one random destination identity for a new profile root.
    /// </summary>
    /// <remarks>
    /// A new profile root is a new installation by construction, so it mints its own identity before
    /// its first journal or staged effect. Reusing the source archive's identity would let a restore
    /// claim a lineage this machine never had.
    /// </remarks>
    internal Result<Guid> CreateForNewProfileRoot(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace)
    {
        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentNullException.ThrowIfNull(profileNamespace);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        Result<BackupRestoreJournalIdentityProbe> probe = Probe(profileNamespace);

        if (probe.IsFailure)
        {
            return Result<Guid>.Failure(probe.Error);
        }

        return probe.Value.Presence is BackupRestoreJournalIdentityPresence.Present
            ? new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "This profile root already carries an external installation identity.")
            : Write(profileNamespace, Guid.NewGuid());
    }

    /// <summary>
    /// Reads the identity a Grimoire that does not exist yet must be born under, or reports that this
    /// profile carries none.
    /// </summary>
    /// <remarks>
    /// The binding exists before any database does, and every cleanup short of an attested full reset
    /// retains it — including an ordinary installation reset, whose sweep deletes the database it was
    /// seeded from. A database created afterwards is therefore created into a profile that already names
    /// an installation, and taking that name is what keeps the row and the binding from disagreeing the
    /// first time anything compares them: every later restore, reset, and offline transition refuses a
    /// row that differs from this account, and none of them can change either side.
    ///
    /// <para>This reads and never writes. Absence stays absence, because seeding is something only an
    /// authenticated replacement restore, an authenticated reset publication, or an authenticated offline
    /// transition's opening journal may do (<see cref="SeedFromDatabase"/>), and a value that
    /// exists but is not one canonical identity is a typed failure rather than a reason to mint another:
    /// minting over it would silently replace what it was there to pin.</para>
    /// </remarks>
    internal Result<Guid?> ReadForNewDatabase(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace)
    {
        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentNullException.ThrowIfNull(profileNamespace);

        // Identity, not liveness, and deliberately no acquisition.
        heldInstallationLock.AssertHeldFor(guardedDirectory);

        Result<BackupRestoreJournalIdentityProbe> probe = Probe(profileNamespace);

        if (probe.IsFailure)
        {
            return Result<Guid?>.Failure(probe.Error);
        }

        return Result<Guid?>.Success(
            probe.Value.Presence is BackupRestoreJournalIdentityPresence.Present
                ? probe.Value.InstallationId
                : null);
    }

    /// <summary>
    /// Requires the external identity to be present and to equal the database authority row.
    /// </summary>
    internal Result RequireMatchesDatabase(
        BackupRestoreProfileNamespace profileNamespace,
        Guid databaseInstallationId)
    {
        Result<BackupRestoreJournalIdentityProbe> probe = Probe(profileNamespace);

        if (probe.IsFailure)
        {
            return probe.Error;
        }

        if (probe.Value.Presence is not BackupRestoreJournalIdentityPresence.Present)
        {
            return new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "This profile has no external installation identity to compare the database row with.");
        }

        return probe.Value.InstallationId == databaseInstallationId
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This profile's external installation identity names a different installation than the "
                + "database row.");
    }

    /// <summary>
    /// The account this profile's identity is stored under, for a caller that reports it.
    /// </summary>
    /// <remarks>
    /// The owner names the account so that no other file derives it: an account name has one owner, and
    /// a second opinion about which account holds the identity is how the wrong one gets deleted.
    /// </remarks>
    internal string AccountFor(BackupRestoreProfileNamespace profileNamespace)
    {
        ArgumentNullException.ThrowIfNull(profileNamespace);

        return Account(profileNamespace);
    }

    /// <summary>
    /// Removes a stored value that is present and is not one canonical identity, reads its absence
    /// back, and removes nothing else.
    /// </summary>
    /// <remarks>
    /// The one path that removes this account outside the terminal cleanup of an attested full reset,
    /// called only by the installation-identity repair after it has proved every other condition under
    /// the held installation lock. It does not trust that proof: a valid identity is never removed, and a
    /// store that does not answer is not a damaged value, so each is refused here too. The result is
    /// true when this call removed the item and false when it was already gone.
    /// </remarks>
    internal Result<bool> RemoveDamaged(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace)
    {
        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentNullException.ThrowIfNull(profileNamespace);

        // Identity, not liveness, and deliberately no acquisition.
        heldInstallationLock.AssertHeldFor(guardedDirectory);

        Result<BackupRestoreJournalIdentityProbe> probe = Probe(profileNamespace);

        if (probe.IsSuccess)
        {
            return probe.Value.Presence is BackupRestoreJournalIdentityPresence.Absent
                ? Result<bool>.Success(false)
                : new Error(
                    ErrorCodes.Data.Conflict,
                    "This profile's installation identity is a valid canonical identity. A valid identity "
                    + "is never removed, so nothing was changed.");
        }

        if (probe.Error.Code != ErrorCodes.Covenant.IntegrityFailure)
        {
            return Result<bool>.Failure(probe.Error);
        }

        OsCredentialStoreResult deleted;

        try
        {
            deleted = _credentials.Delete(ArcanumCredentialIdentity.Service, Account(profileNamespace));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {
            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The damaged installation identity could not be removed from the operating-system credential "
                + "store. Unlock or repair the store and run the repair again.");
        }

        if (deleted.Status is not (OsCredentialStoreStatus.Ok or OsCredentialStoreStatus.NotFound))
        {
            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The damaged installation identity could not be removed from the operating-system credential "
                + "store. Unlock or repair the store and run the repair again.");
        }

        Result<BackupRestoreJournalIdentityProbe> readback = Probe(profileNamespace);

        return readback.IsSuccess && readback.Value.Presence is BackupRestoreJournalIdentityPresence.Absent
            ? Result<bool>.Success(true)
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The installation identity item did not read back as removed. Run the repair again.");
    }

    private Result<Guid> Write(BackupRestoreProfileNamespace profileNamespace, Guid installationId)
    {
        string account = Account(profileNamespace);

        string canonical = Canonical(installationId);

        OsCredentialStoreResult written;

        try
        {
            written = _credentials.Set(ArcanumCredentialIdentity.Service, account, canonical);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {
            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's external installation identity could not be written.");
        }

        if (written.Status is not OsCredentialStoreStatus.Ok)
        {
            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "This profile's external installation identity could not be written.");
        }

        Result<BackupRestoreJournalIdentityProbe> readback = Probe(profileNamespace);

        if (readback.IsFailure)
        {
            return Result<Guid>.Failure(readback.Error);
        }

        return readback.Value.Presence is BackupRestoreJournalIdentityPresence.Present
            && readback.Value.InstallationId == installationId
                ? installationId
                : new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "This profile's external installation identity did not read back as written.");
    }

    private static string Account(BackupRestoreProfileNamespace profileNamespace) =>
        ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(profileNamespace.AccountSuffix);

    private static string Canonical(Guid installationId) =>
        installationId.ToString("D").ToUpperInvariant();

    /// <summary>
    /// Accepts exactly one spelling.
    /// </summary>
    /// <remarks>
    /// Round-tripping the parse is what rejects the other four UUID formats and every case variant:
    /// one identity has to have one stored form, or a comparison against the database row would depend
    /// on which writer last touched the slot.
    /// </remarks>
    private static bool TryParseCanonical(string? value, out Guid installationId)
    {
        installationId = Guid.Empty;

        if (value is null
            || !Guid.TryParseExact(value, "D", out Guid parsed)
            || parsed == Guid.Empty
            || !string.Equals(Canonical(parsed), value, StringComparison.Ordinal))
        {
            return false;
        }

        installationId = parsed;

        return true;
    }
}
