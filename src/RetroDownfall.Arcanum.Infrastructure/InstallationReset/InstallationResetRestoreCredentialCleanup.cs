using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// How far the removal of one profile's three restore-journal credentials has got.
/// </summary>
/// <remarks>
/// The order is the whole content of this enum, and it is the reverse of the order the credentials
/// authorize each other in. The anchor is what makes a journal believable, so it goes first: once it
/// is gone, no surviving journal can authenticate and no partially removed state can be mistaken for a
/// restore in progress. The key goes next, then the installation identity, and only
/// <c>VerifiedAbsent</c> — a fresh read proving all three gone — permits anything downstream to report
/// the installation deleted.
///
/// <para>Every phase is idempotent. A crash between a removal and the record of it leaves the account
/// already absent, and the next pass reads that absence and advances rather than refusing.</para>
/// </remarks>
internal enum InstallationResetRestoreCredentialCleanupPhase : byte
{

    AnchorRemoved = 1,

    JournalKeyRemoved = 2,

    InstallationIdentityRemoved = 3,

    VerifiedAbsent = 4,

}

/// <summary>
/// The exact three profile-namespaced accounts one restore journal owns.
/// </summary>
/// <remarks>
/// Derived, never enumerated. The credential store answers by account name and cannot be listed, so
/// these three names are the only handle anything has on them — and a bare prefix, an unnamespaced
/// alias, or another profile's suffix is a different account that this installation has no claim to.
/// </remarks>
internal sealed record InstallationResetRestoreCredentialTrio(
    string InstallationAccount,
    string JournalKeyAccount,
    string AnchorAccount);

/// <summary>
/// The only remover of the three profile-namespaced restore-journal credentials.
/// </summary>
/// <remarks>
/// It runs after the host-tools markers, the Campaign markers, the managed files, and the Grimoire
/// have all been accounted for, and only against a proven-terminal restore projection. Until that
/// proof exists the three accounts are retained by every other path in the product, because they are
/// the only evidence that could finish an interrupted restore, and an installation that lost them
/// mid-restore would have a staging directory nothing could ever authenticate again.
///
/// <para>Each removal is a compare-removal: the account's current value has to reproduce the digest
/// the terminal proof projected for that exact account name. A value that changed since the proof
/// means something wrote to the slot after it was declared terminal, and the honest answer there is to
/// stop rather than to delete whatever is now there.</para>
/// </remarks>
internal sealed class InstallationResetRestoreCredentialCleanup(IOsCredentialStore credentials)
{

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    /// <summary>
    /// Derives the exact trio from the current profile-namespace digest.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The digest does not render as a canonical 64-character lowercase-hex suffix. A malformed suffix
    /// must never produce an account name, because the name that came out of it would belong to
    /// nobody.
    /// </exception>
    internal static InstallationResetRestoreCredentialTrio Derive(
        CovenantDigest profileNamespaceDigest)
    {

        string suffix = Convert.ToHexStringLower(profileNamespaceDigest.Bytes);

        return new InstallationResetRestoreCredentialTrio(
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(suffix),
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(suffix),
            ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(suffix));

    }

    /// <summary>
    /// Removes the three accounts in the one legal order and proves all three absent.
    /// </summary>
    /// <remarks>
    /// The projection is the authorization. Its arm decides what may be there at all: an installation
    /// that never restored must have all three absent already, and one whose last restore closed must
    /// have all three present with exactly the projected values.
    /// </remarks>
    internal Result<InstallationResetRestoreCredentialCleanupPhase> Remove(
        BackupRestoreFullResetTerminalProjectionV1 terminal)
    {

        ArgumentNullException.ThrowIfNull(terminal);

        if (terminal.Version != 1
            || !Enum.IsDefined(terminal.Arm))
        {

            return Blocked();

        }

        InstallationResetRestoreCredentialTrio trio = Derive(terminal.ProfileNamespaceDigest);

        // Anchor, then key, then installation identity. Reversing this would leave an installation
        // whose journal key is gone but whose anchor still claims an operation, which is a state no
        // recovery can either finish or safely abandon.
        (string Account, CovenantDigest? Projected)[] ordered =
        [
            (trio.AnchorAccount, terminal.AnchorAccountValueDigest),
            (trio.JournalKeyAccount, terminal.JournalKeyAccountValueDigest),
            (trio.InstallationAccount, terminal.InstallationAccountValueDigest),
        ];

        foreach ((string account, CovenantDigest? projected) in ordered)
        {

            Result removed = CompareRemove(account, projected);

            if (removed.IsFailure)
            {

                return Result<InstallationResetRestoreCredentialCleanupPhase>.Failure(removed.Error);

            }

        }

        // Reread rather than inferred from the deletes above. VerifiedAbsent is what everything
        // downstream is allowed to act on, so it has to be an observation rather than a conclusion.
        foreach ((string account, _) in ordered)
        {

            Result<bool> absent = IsAbsent(account);

            if (absent.IsFailure)
            {

                return Result<InstallationResetRestoreCredentialCleanupPhase>.Failure(absent.Error);

            }

            if (!absent.Value)
            {

                return Blocked();

            }

        }

        return Result<InstallationResetRestoreCredentialCleanupPhase>.Success(
            InstallationResetRestoreCredentialCleanupPhase.VerifiedAbsent);

    }

    /// <summary>
    /// Removes one account only when its current value reproduces the projected digest.
    /// </summary>
    /// <remarks>
    /// An account already absent is a completed step rather than a failure — that is what makes a
    /// crash between the delete and the record of it recoverable. An account that is present and holds
    /// a value the projection did not commit to is refused, including every value under the absence
    /// arm, which projected none at all: that is a write that happened after the state was declared
    /// terminal, and deleting it would destroy the evidence of whatever made it.
    /// </remarks>
    private Result CompareRemove(string account, CovenantDigest? projected)
    {

        OsCredentialStoreResult before;

        try
        {

            before = _credentials.TryGet(ArcanumCredentialIdentity.Service, account);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return Unavailable();

        }

        if (before.Status is OsCredentialStoreStatus.NotFound)
        {

            // Already gone, whichever arm projected it. This is exactly the state a crash between the
            // delete and the record of it leaves behind, so treating it as a violation would make the
            // operation unresumable from the one window it was designed to survive. The projection is
            // still the authorization — it is what a resume reads instead of re-deriving a terminal
            // proof from a credential set this operation has already started removing.
            return Result.Success();

        }

        if (before.Status is not OsCredentialStoreStatus.Ok || before.Value is not { } value)
        {

            return Unavailable();

        }

        if (projected is not { } expected
            || BackupRestoreJournalAnchorStore.AccountValueDigest(account, value) != expected)
        {

            return Result.Failure(BlockedError());

        }

        OsCredentialStoreResult deleted;

        try
        {

            deleted = _credentials.Delete(ArcanumCredentialIdentity.Service, account);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return Unavailable();

        }

        return deleted.Status is OsCredentialStoreStatus.Ok or OsCredentialStoreStatus.NotFound
            ? Result.Success()
            : Unavailable();

    }

    private Result<bool> IsAbsent(string account)
    {

        try
        {

            OsCredentialStoreResult result =
                _credentials.TryGet(ArcanumCredentialIdentity.Service, account);

            return result.Status switch
            {
                OsCredentialStoreStatus.NotFound => Result<bool>.Success(true),
                OsCredentialStoreStatus.Ok => Result<bool>.Success(false),
                _ => Result<bool>.Failure(UnavailableError()),
            };

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return Result<bool>.Failure(UnavailableError());

        }

    }

    private static Result<InstallationResetRestoreCredentialCleanupPhase> Blocked() =>
        Result<InstallationResetRestoreCredentialCleanupPhase>.Failure(BlockedError());

    private static Error BlockedError() =>
        new(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The profile-namespaced restore credentials are not in the state the terminal proof committed to.");

    private static Result Unavailable() =>
        Result.Failure(UnavailableError());

    private static Error UnavailableError() =>
        new(
            ErrorCodes.Covenant.Unavailable,
            "A profile-namespaced restore credential could not be read or removed.");

}
