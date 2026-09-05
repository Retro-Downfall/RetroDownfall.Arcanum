using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

/// <summary>
/// The two ways a profile's offline-transition slot can be provably over.
/// </summary>
/// <remarks>
/// Two, and no third. <c>NeverTransitionedAbsence</c> is an installation whose slot was never opened:
/// no anchor, no key, no journal file. <c>ClosedAnchor</c> is one whose last transition retired and
/// left its closing tombstone behind. Everything else is not terminal and mints no proof — an active
/// anchor, a surviving journal file, and above all a key sitting beside an absent anchor, which is the
/// residue of a genesis that began. Genesis mints the key itself and refuses to start when one is
/// already present, so that combination is durable evidence rather than a tidy-up opportunity.
/// </remarks>
internal enum GrimoireOfflineTransitionFullResetTerminalArm : byte
{

    NeverTransitionedAbsence = 1,

    ClosedAnchor = 2,

}

/// <summary>
/// What a transition-slot terminal proof observed, and the single digest it commits to.
/// </summary>
/// <remarks>
/// The closed-anchor fields are all nonnull for
/// <see cref="GrimoireOfflineTransitionFullResetTerminalArm.ClosedAnchor"/> and all null for the
/// absence arm. The two account-value digests are what a later compare-removal has to reproduce, and
/// each is bound to its own account name, so a digest taken for the anchor cannot authorize removing
/// the key.
/// </remarks>
internal sealed record GrimoireOfflineTransitionFullResetTerminalProjectionV1(
    byte Version,
    GrimoireOfflineTransitionFullResetTerminalArm Arm,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    ulong? ClosedSlotEpoch,
    Guid? ClosedOperationId,
    ulong? ClosedRevision,
    CovenantDigest? ClosedEnvelopeDigest,
    CovenantDigest? JournalKeyAccountValueDigest,
    CovenantDigest? AnchorAccountValueDigest,
    CovenantDigest TerminalEvidenceDigest);

internal sealed partial class GrimoireOfflineTransitionJournalAnchorStore
{

    private const string TerminalEvidenceDomain =
        "Arcanum.FullInstallationReset.GrimoireTransitionTerminal.v1";

    private const string TerminalAccountValueDomain =
        "Arcanum.FullInstallationReset.GrimoireTransitionAccountValue.v1";

    private const byte AbsentField = 0x00;

    private const byte PresentField = 0x01;

    /// <summary>
    /// Proves that this profile's offline-transition slot is over, or refuses to.
    /// </summary>
    /// <remarks>
    /// Nothing here removes anything. It answers one question — may the two profile-namespaced
    /// transition accounts be removed — and it answers it only for the two arms above. The journal file
    /// and its three siblings are proved absent through the same durable primitive retirement uses, so
    /// a proof cannot be obtained against a slot whose file is merely unreadable at this moment.
    ///
    /// <para>A mixed credential set produces no proof in either arm. One of two accounts present is a
    /// slot somebody already started clearing by hand, and the honest answer there is that a person has
    /// to look.</para>
    /// </remarks>
    internal Result<GrimoireOfflineTransitionFullResetTerminalProjectionV1> ProveFullResetTerminal(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        Guid installationId)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(location);

        heldInstallationLock.AssertHeldFor(location.GuardedDirectory);

        if (installationId == Guid.Empty)
        {

            return NotTerminal();

        }

        // The file goes first. An anchor that says Closed while the journal it closed is still on disk
        // is not a finished slot, and the accounts are the only thing that could ever authenticate
        // whatever is still there.
        Result absent = new GrimoireOfflineTransitionJournalFileStore()
            .ProveAbsentDurably(heldInstallationLock, location);

        if (absent.IsFailure)
        {

            return NotTerminal();

        }

        Result<GrimoireOfflineTransitionAnchorV1?> anchorRead = Read(location);

        if (anchorRead.IsFailure)
        {

            return Result<GrimoireOfflineTransitionFullResetTerminalProjectionV1>.Failure(
                anchorRead.Error);

        }

        Result<TerminalAccountDigests> accounts = ReadTerminalAccountDigests(location);

        if (accounts.IsFailure)
        {

            return Result<GrimoireOfflineTransitionFullResetTerminalProjectionV1>.Failure(
                accounts.Error);

        }

        if (anchorRead.Value is not { } anchor)
        {

            return accounts.Value.Anchor is null && accounts.Value.JournalKey is null
                ? ProjectTerminal(
                    GrimoireOfflineTransitionFullResetTerminalArm.NeverTransitionedAbsence,
                    location,
                    installationId,
                    anchor: null,
                    accounts.Value)
                : NotTerminal();

        }

        return anchor.State is GrimoireOfflineTransitionAnchorState.Closed
            && anchor.InstallationId == installationId
            && accounts.Value.Anchor is not null
            && accounts.Value.JournalKey is not null
                ? ProjectTerminal(
                    GrimoireOfflineTransitionFullResetTerminalArm.ClosedAnchor,
                    location,
                    installationId,
                    anchor,
                    accounts.Value)
                : NotTerminal();

    }

    /// <summary>
    /// Compare-removes the anchor account, which is the first of the pair to go.
    /// </summary>
    /// <remarks>
    /// Anchor before key, the reverse of the order they authorize each other in. Once the anchor is
    /// gone no surviving journal could authenticate, so no partially removed state can be mistaken for
    /// a transition in progress; reversing it would leave a slot whose key is gone while its anchor
    /// still claims an operation, which is a state no recovery can finish or safely abandon.
    /// </remarks>
    internal Result RemoveAnchorForFullReset(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        CovenantDigest projectedValueDigest) =>
        CompareRemoveForFullReset(
            heldInstallationLock,
            location,
            TerminalAccounts(location.ProfileNamespace).AnchorAccount,
            projectedValueDigest);

    /// <summary>Compare-removes the key account, which goes only after the anchor.</summary>
    internal Result RemoveJournalKeyForFullReset(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        CovenantDigest projectedValueDigest) =>
        CompareRemoveForFullReset(
            heldInstallationLock,
            location,
            TerminalAccounts(location.ProfileNamespace).KeyAccount,
            projectedValueDigest);

    /// <summary>A fresh read proving both accounts gone, which is the only end of the removal.</summary>
    internal Result VerifyTerminalPairAbsent(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(location);

        heldInstallationLock.AssertHeldFor(location.GuardedDirectory);

        Result<TerminalAccountDigests> accounts = ReadTerminalAccountDigests(location);

        return accounts.IsFailure
            ? Result.Failure(accounts.Error)
            : accounts.Value.Anchor is null && accounts.Value.JournalKey is null
                ? Result.Success()
                : Result.Failure(NotTerminal().Error);

    }

    /// <summary>
    /// Removes one account only when its current value still reproduces the projected digest.
    /// </summary>
    /// <remarks>
    /// An already-absent account is read as absent and the pass advances, so a removal resumed after a
    /// crash between the deletion and the record of it finishes rather than refuses. A value that
    /// changed since the proof was taken means something wrote to the slot after it was declared
    /// terminal, and the honest answer there is to stop rather than delete whatever is now there.
    /// </remarks>
    private Result CompareRemoveForFullReset(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        string account,
        CovenantDigest projectedValueDigest)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(location);

        heldInstallationLock.AssertHeldFor(location.GuardedDirectory);

        if (!projectedValueDigest.IsValid)
        {

            return NotTerminal().Error;

        }

        Result<CovenantDigest?> before = ReadTerminalAccountDigest(account);

        if (before.IsFailure)
        {

            return Result.Failure(before.Error);

        }

        if (before.Value is null)
        {

            return Result.Success();

        }

        if (before.Value != projectedValueDigest)
        {

            return NotTerminal().Error;

        }

        try
        {

            OsCredentialStoreResult deleted = _credentials.Delete(
                ArcanumCredentialIdentity.Service,
                account);

            if (deleted.Status is not OsCredentialStoreStatus.Ok
                and not OsCredentialStoreStatus.NotFound)
            {

                return Unavailable().Error;

            }

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return Unavailable().Error;

        }

        Result<CovenantDigest?> after = ReadTerminalAccountDigest(account);

        return after.IsFailure
            ? Result.Failure(after.Error)
            : after.Value is null
                ? Result.Success()
                : NotTerminal().Error;

    }

    /// <summary>The account-bound digest a later compare-removal reproduces before it deletes.</summary>
    internal static CovenantDigest TerminalAccountValueDigest(string account, string value)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(account);

        ArgumentNullException.ThrowIfNull(value);

        using MemoryStream preimage = new();

        preimage.Write(Encoding.ASCII.GetBytes(TerminalAccountValueDomain));

        preimage.WriteByte(0x00);

        WriteLengthPrefixed(preimage, Encoding.UTF8.GetBytes(account));

        WriteLengthPrefixed(preimage, Encoding.UTF8.GetBytes(value));

        return new CovenantDigest(SHA256.HashData(preimage.ToArray()));

    }

    /// <summary>The two accounts this slot owns, derived from the profile namespace and nowhere else.</summary>
    internal static (string AnchorAccount, string KeyAccount) TerminalAccounts(
        BackupRestoreProfileNamespace profileNamespace)
    {

        ArgumentNullException.ThrowIfNull(profileNamespace);

        return (
            ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccount(
                profileNamespace.AccountSuffix),
            ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount(
                profileNamespace.AccountSuffix));

    }

    private static Result<GrimoireOfflineTransitionFullResetTerminalProjectionV1> ProjectTerminal(
        GrimoireOfflineTransitionFullResetTerminalArm arm,
        GrimoireOfflineTransitionJournalLocation location,
        Guid installationId,
        GrimoireOfflineTransitionAnchorV1? anchor,
        TerminalAccountDigests accounts) =>
        Result<GrimoireOfflineTransitionFullResetTerminalProjectionV1>.Success(
            new GrimoireOfflineTransitionFullResetTerminalProjectionV1(
                Version: 1,
                arm,
                location.ProfileNamespace.Digest,
                installationId,
                anchor?.SlotEpoch,
                anchor?.OperationId,
                anchor?.Revision,
                anchor?.EnvelopeDigest,
                accounts.JournalKey,
                accounts.Anchor,
                TerminalEvidence(
                    arm,
                    location.ProfileNamespace.Digest,
                    installationId,
                    anchor,
                    accounts)));

    /// <summary>
    /// The one commitment a terminal proof makes, over the arm and everything that decided it.
    /// </summary>
    /// <remarks>
    /// Every optional field is written with a presence byte before it, so an absent closed anchor and a
    /// closed anchor whose fields happened to be zero cannot produce the same preimage. The two account
    /// digests follow in the order the accounts are named — key, then anchor — which is deliberately
    /// the reverse of the order they are removed in: the commitment describes the credential set, and
    /// the removal order is a separate crash-safety decision.
    /// </remarks>
    private static CovenantDigest TerminalEvidence(
        GrimoireOfflineTransitionFullResetTerminalArm arm,
        CovenantDigest profileNamespaceDigest,
        Guid installationId,
        GrimoireOfflineTransitionAnchorV1? anchor,
        TerminalAccountDigests accounts)
    {

        using MemoryStream preimage = new();

        preimage.Write(Encoding.ASCII.GetBytes(TerminalEvidenceDomain));

        preimage.WriteByte(0x00);

        preimage.WriteByte((byte)arm);

        preimage.Write(profileNamespaceDigest.Bytes);

        WriteGuid(preimage, installationId);

        WriteOptionalUInt64(preimage, anchor?.SlotEpoch);

        WriteOptionalGuid(preimage, anchor?.OperationId);

        WriteOptionalUInt64(preimage, anchor?.Revision);

        WriteOptionalDigest(preimage, anchor?.EnvelopeDigest);

        WriteOptionalDigest(preimage, accounts.JournalKey);

        WriteOptionalDigest(preimage, accounts.Anchor);

        return new CovenantDigest(SHA256.HashData(preimage.ToArray()));

    }

    private Result<TerminalAccountDigests> ReadTerminalAccountDigests(
        GrimoireOfflineTransitionJournalLocation location)
    {

        (string anchorAccount, string keyAccount) = TerminalAccounts(location.ProfileNamespace);

        Result<CovenantDigest?> anchor = ReadTerminalAccountDigest(anchorAccount);

        Result<CovenantDigest?> journalKey = ReadTerminalAccountDigest(keyAccount);

        return anchor.IsFailure
            ? Result<TerminalAccountDigests>.Failure(anchor.Error)
            : journalKey.IsFailure
                ? Result<TerminalAccountDigests>.Failure(journalKey.Error)
                : Result<TerminalAccountDigests>.Success(
                    new TerminalAccountDigests(journalKey.Value, anchor.Value));

    }

    private Result<CovenantDigest?> ReadTerminalAccountDigest(string account)
    {

        OsCredentialStoreResult result;

        try
        {

            result = _credentials.TryGet(ArcanumCredentialIdentity.Service, account);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return Unavailable<CovenantDigest?>();

        }

        if (result.Status is OsCredentialStoreStatus.NotFound)
        {

            return Result<CovenantDigest?>.Success(null);

        }

        return result.Status is not OsCredentialStoreStatus.Ok || result.Value is not { } value
            ? Unavailable<CovenantDigest?>()
            : Result<CovenantDigest?>.Success(TerminalAccountValueDigest(account, value));

    }

    private static Result<GrimoireOfflineTransitionFullResetTerminalProjectionV1> NotTerminal() =>
        new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The offline-transition slot is not provably terminal.");

    private static void WriteGuid(MemoryStream target, Guid value)
    {

        Span<byte> buffer = stackalloc byte[16];

        _ = value.TryWriteBytes(buffer, bigEndian: true, out _);

        target.Write(buffer);

    }

    private static void WriteOptionalGuid(MemoryStream target, Guid? value)
    {

        target.WriteByte(value is null ? AbsentField : PresentField);

        if (value is { } present)
        {

            WriteGuid(target, present);

        }

    }

    private static void WriteOptionalUInt64(MemoryStream target, ulong? value)
    {

        target.WriteByte(value is null ? AbsentField : PresentField);

        if (value is { } present)
        {

            Span<byte> buffer = stackalloc byte[8];

            BinaryPrimitives.WriteUInt64BigEndian(buffer, present);

            target.Write(buffer);

        }

    }

    private static void WriteOptionalDigest(MemoryStream target, CovenantDigest? value)
    {

        target.WriteByte(value is null ? AbsentField : PresentField);

        if (value is { } present)
        {

            target.Write(present.Bytes);

        }

    }

    private static void WriteLengthPrefixed(MemoryStream target, byte[] value)
    {

        Span<byte> length = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);

        target.Write(length);

        target.Write(value);

    }

    private sealed record TerminalAccountDigests(
        CovenantDigest? JournalKey,
        CovenantDigest? Anchor);

}
