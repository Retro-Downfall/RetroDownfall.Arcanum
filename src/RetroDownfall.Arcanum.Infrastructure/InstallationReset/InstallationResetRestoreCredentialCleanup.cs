using System.Collections.Immutable;

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
/// <summary>
/// One ordered removal step: which phase it completes, which account, and what it must still hold.
/// </summary>
/// <remarks>
/// The projected digest travels with the step because a resumed removal cannot re-derive it. Once the
/// anchor is gone the credential set no longer has the shape the terminal proof was made from, so the
/// proof is persisted with the operation and each surviving account is still compared against the
/// value that was projected for it while all three were there.
/// </remarks>
internal sealed record InstallationResetRestoreCredentialStep(
    InstallationResetRestoreCredentialCleanupPhase CompletedPhase,
    string Account,
    CovenantDigest? ProjectedValueDigest);

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
    /// The three accounts in the one legal removal order, paired with the digests projected for them.
    /// </summary>
    /// <remarks>
    /// Anchor, then key, then installation identity — the reverse of the order the credentials
    /// authorize each other in. Removing the anchor first means no surviving journal can authenticate
    /// from that moment on, so no partially removed state can be mistaken for a restore in progress;
    /// reversing it would leave a profile whose key is gone while its anchor still claims an
    /// operation, which is a state no recovery can either finish or safely abandon.
    /// </remarks>
    internal static Result<ImmutableArray<InstallationResetRestoreCredentialStep>> OrderedSteps(
        BackupRestoreFullResetTerminalProjectionV1 terminal)
    {

        ArgumentNullException.ThrowIfNull(terminal);

        if (terminal.Version != 1 || !Enum.IsDefined(terminal.Arm))
        {

            return Result<ImmutableArray<InstallationResetRestoreCredentialStep>>.Failure(
                BlockedError());

        }

        InstallationResetRestoreCredentialTrio trio = Derive(terminal.ProfileNamespaceDigest);

        ImmutableArray<InstallationResetRestoreCredentialStep> steps =
        [
            new InstallationResetRestoreCredentialStep(
                InstallationResetRestoreCredentialCleanupPhase.AnchorRemoved,
                trio.AnchorAccount,
                terminal.AnchorAccountValueDigest),
            new InstallationResetRestoreCredentialStep(
                InstallationResetRestoreCredentialCleanupPhase.JournalKeyRemoved,
                trio.JournalKeyAccount,
                terminal.JournalKeyAccountValueDigest),
            new InstallationResetRestoreCredentialStep(
                InstallationResetRestoreCredentialCleanupPhase.InstallationIdentityRemoved,
                trio.InstallationAccount,
                terminal.InstallationAccountValueDigest),
        ];

        return Result<ImmutableArray<InstallationResetRestoreCredentialStep>>.Success(steps);

    }

    /// <summary>
    /// Performs one ordered step: compare-removes its account, or observes it already gone.
    /// </summary>
    internal Result RemoveStep(InstallationResetRestoreCredentialStep step)
    {

        ArgumentNullException.ThrowIfNull(step);

        return CompareRemove(step.Account, step.ProjectedValueDigest);

    }

    /// <summary>
    /// Rereads all three and reports whether every one is gone.
    /// </summary>
    /// <remarks>
    /// An observation rather than a conclusion. <c>VerifiedAbsent</c> is what everything downstream
    /// acts on, and inferring it from the deletes that were just issued would make it a restatement of
    /// the intent rather than of the outcome.
    /// </remarks>
    internal Result VerifyAllAbsent(BackupRestoreFullResetTerminalProjectionV1 terminal)
    {

        Result<ImmutableArray<InstallationResetRestoreCredentialStep>> steps =
            OrderedSteps(terminal);

        if (steps.IsFailure)
        {

            return Result.Failure(steps.Error);

        }

        foreach (InstallationResetRestoreCredentialStep step in steps.Value)
        {

            Result<bool> absent = IsAbsent(step.Account);

            if (absent.IsFailure)
            {

                return Result.Failure(absent.Error);

            }

            if (!absent.Value)
            {

                return Result.Failure(BlockedError());

            }

        }

        return Result.Success();

    }

    /// <summary>
    /// Removes all three in order and proves them absent, in one call.
    /// </summary>
    /// <remarks>
    /// The uninterrupted path. A caller that has to publish a durable checkpoint between steps drives
    /// <see cref="OrderedSteps"/> and <see cref="RemoveStep"/> itself instead.
    /// </remarks>
    internal Result<InstallationResetRestoreCredentialCleanupPhase> Remove(
        BackupRestoreFullResetTerminalProjectionV1 terminal)
    {

        Result<ImmutableArray<InstallationResetRestoreCredentialStep>> steps =
            OrderedSteps(terminal);

        if (steps.IsFailure)
        {

            return Result<InstallationResetRestoreCredentialCleanupPhase>.Failure(steps.Error);

        }

        foreach (InstallationResetRestoreCredentialStep step in steps.Value)
        {

            Result removed = RemoveStep(step);

            if (removed.IsFailure)
            {

                return Result<InstallationResetRestoreCredentialCleanupPhase>.Failure(removed.Error);

            }

        }

        Result verified = VerifyAllAbsent(terminal);

        return verified.IsFailure
            ? Result<InstallationResetRestoreCredentialCleanupPhase>.Failure(verified.Error)
            : Result<InstallationResetRestoreCredentialCleanupPhase>.Success(
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
