using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The two ways a profile's restore history can be provably over.
/// </summary>
/// <remarks>
/// Two, and no third. <c>NeverRestoredAbsence</c> is an installation that has never authenticated a
/// restore at all: no anchor, no journal, and none of the three profile accounts a restore creates.
/// <c>ClosedAnchor</c> is an installation whose last restore finished and left its closing tombstone
/// behind. Everything else — an active restore, a journal with no anchor, an anchor from another
/// profile or another installation, or a half-removed credential set — is not terminal, and no proof
/// is minted for it. That refusal is the whole point: the three accounts may not be removed until this
/// answers, because removing them mid-restore destroys the only evidence that could finish it.
/// </remarks>
internal enum BackupRestoreFullResetTerminalArm : byte
{

    NeverRestoredAbsence = 1,

    ClosedAnchor = 2,

}

/// <summary>
/// What a terminal-state proof observed, and the single digest it commits to.
/// </summary>
/// <remarks>
/// The closed-anchor fields are all nonnull for <see cref="BackupRestoreFullResetTerminalArm.ClosedAnchor"/>
/// and all null for the absence arm, and the three account-value digests are what a later
/// compare-removal has to reproduce. The projection carries no secret: each account digest is bound to
/// its own account name, so a digest taken for the anchor cannot authorize removing the journal key.
/// </remarks>
internal sealed record BackupRestoreFullResetTerminalProjectionV1(
    byte Version,
    BackupRestoreFullResetTerminalArm Arm,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    Guid? ClosedOperationId,
    ulong? ClosedRevision,
    CovenantDigest? ClosedEnvelopeDigest,
    CovenantDigest? ClosedJournalLocationDigest,
    CovenantDigest? InstallationAccountValueDigest,
    CovenantDigest? JournalKeyAccountValueDigest,
    CovenantDigest? AnchorAccountValueDigest,
    CovenantDigest TerminalEvidenceDigest);

internal sealed partial class BackupRestoreJournalAnchorStore
{

    private const string TerminalEvidenceDomain =
        "Arcanum.FullInstallationReset.BackupRestoreTerminal.v1";

    private const string AccountValueDomain =
        "Arcanum.FullInstallationReset.BackupRestoreAccountValue.v1";

    private const byte AbsentField = 0x00;

    private const byte PresentField = 0x01;

    /// <summary>
    /// Proves that this profile's restore history is over, or refuses to.
    /// </summary>
    /// <remarks>
    /// Nothing here removes anything. It answers one question — may the three profile-namespaced
    /// restore accounts be removed — and it answers it only for the two arms above. The classification
    /// it rests on is the same one startup recovery uses, run without advancing the anchor, so a proof
    /// cannot be obtained by nudging the very state it is describing.
    ///
    /// <para>A mixed credential set produces no proof in either arm. Two of three accounts present is
    /// an installation somebody already started cleaning by hand, and the honest answer there is that a
    /// person has to look, not that the reset may finish the job.</para>
    /// </remarks>
    internal Result<BackupRestoreFullResetTerminalProjectionV1> ProveFullResetTerminal(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profileNamespace,
        Guid installationId,
        IReadOnlyList<string> candidateStagingRoots)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentNullException.ThrowIfNull(profileNamespace);

        ArgumentNullException.ThrowIfNull(candidateStagingRoots);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        if (installationId == Guid.Empty)
        {

            return NotTerminal();

        }

        Result<BackupRestoreJournalRecoveryState> classified = Classify(
            profileNamespace,
            candidateStagingRoots,
            advanceOneAhead: false);

        if (classified.IsFailure
            || classified.Value.Outcome is not BackupRestoreJournalRecoveryOutcome.NoActiveJournal)
        {

            return NotTerminal();

        }

        Result<BackupRestoreJournalAnchorV1?> anchorRead = TryReadAnchor(profileNamespace);

        if (anchorRead.IsFailure)
        {

            return Result<BackupRestoreFullResetTerminalProjectionV1>.Failure(anchorRead.Error);

        }

        Result<AccountValueDigests> accounts = ReadAccountValueDigests(profileNamespace);

        if (accounts.IsFailure)
        {

            return Result<BackupRestoreFullResetTerminalProjectionV1>.Failure(accounts.Error);

        }

        BackupRestoreJournalAnchorV1? anchor = anchorRead.Value;

        if (anchor is null)
        {

            // Never restored. A key or an installation identity sitting beside an absent anchor means a
            // restore did happen and somebody removed part of its evidence.
            return accounts.Value.Anchor is null
                && accounts.Value.JournalKey is null
                && accounts.Value.Installation is null
                ? Project(
                    BackupRestoreFullResetTerminalArm.NeverRestoredAbsence,
                    profileNamespace,
                    installationId,
                    anchor: null,
                    accounts.Value)
                : NotTerminal();

        }

        if (anchor.State is not BackupRestoreJournalAnchorState.Closed
            || anchor.ProfileNamespaceDigest != profileNamespace.Digest
            || anchor.InstallationId != installationId
            || accounts.Value.Anchor is null
            || accounts.Value.JournalKey is null
            || accounts.Value.Installation is null)
        {

            return NotTerminal();

        }

        return Project(
            BackupRestoreFullResetTerminalArm.ClosedAnchor,
            profileNamespace,
            installationId,
            anchor,
            accounts.Value);

    }

    private static Result<BackupRestoreFullResetTerminalProjectionV1> Project(
        BackupRestoreFullResetTerminalArm arm,
        BackupRestoreProfileNamespace profileNamespace,
        Guid installationId,
        BackupRestoreJournalAnchorV1? anchor,
        AccountValueDigests accounts)
    {

        BackupRestoreFullResetTerminalProjectionV1 projection = new(
            Version: 1,
            arm,
            profileNamespace.Digest,
            installationId,
            anchor?.OperationId,
            anchor?.Revision,
            anchor?.EnvelopeDigest,
            anchor?.JournalLocationDigest,
            accounts.Installation,
            accounts.JournalKey,
            accounts.Anchor,
            TerminalEvidence(
                arm,
                profileNamespace.Digest,
                installationId,
                anchor,
                accounts));

        return Result<BackupRestoreFullResetTerminalProjectionV1>.Success(projection);

    }

    /// <summary>
    /// The one commitment a terminal proof makes, over the arm and everything that decided it.
    /// </summary>
    /// <remarks>
    /// Every optional field is written with a policy-v1 presence byte before it, so an absent closed
    /// anchor and a closed anchor whose fields happened to be zero cannot produce the same preimage.
    /// The three account digests follow in the order the accounts are named — installation, journal
    /// key, anchor — which is deliberately not the order they are removed in: the commitment describes
    /// the credential set, and the removal order is a separate crash-safety decision.
    /// </remarks>
    private static CovenantDigest TerminalEvidence(
        BackupRestoreFullResetTerminalArm arm,
        CovenantDigest profileNamespaceDigest,
        Guid installationId,
        BackupRestoreJournalAnchorV1? anchor,
        AccountValueDigests accounts)
    {

        using MemoryStream preimage = new();

        preimage.Write(Encoding.ASCII.GetBytes(TerminalEvidenceDomain));

        preimage.WriteByte(0x00);

        preimage.WriteByte((byte)arm);

        preimage.Write(profileNamespaceDigest.Bytes);

        WriteGuid(preimage, installationId);

        WriteOptionalGuid(preimage, anchor?.OperationId);

        WriteOptionalUInt64(preimage, anchor?.Revision);

        WriteOptionalDigest(preimage, anchor?.EnvelopeDigest);

        WriteOptionalDigest(preimage, anchor?.JournalLocationDigest);

        WriteOptionalDigest(preimage, accounts.Installation);

        WriteOptionalDigest(preimage, accounts.JournalKey);

        WriteOptionalDigest(preimage, accounts.Anchor);

        return new CovenantDigest(SHA256.HashData(preimage.ToArray()));

    }

    private Result<AccountValueDigests> ReadAccountValueDigests(
        BackupRestoreProfileNamespace profileNamespace)
    {

        string suffix = profileNamespace.AccountSuffix;

        Result<CovenantDigest?> installation = ReadAccountValueDigest(
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(suffix));

        Result<CovenantDigest?> journalKey = ReadAccountValueDigest(
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(suffix));

        Result<CovenantDigest?> anchor = ReadAccountValueDigest(
            ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(suffix));

        if (installation.IsFailure)
        {

            return Result<AccountValueDigests>.Failure(installation.Error);

        }

        if (journalKey.IsFailure)
        {

            return Result<AccountValueDigests>.Failure(journalKey.Error);

        }

        return anchor.IsFailure
            ? Result<AccountValueDigests>.Failure(anchor.Error)
            : Result<AccountValueDigests>.Success(
                new AccountValueDigests(installation.Value, journalKey.Value, anchor.Value));

    }

    /// <summary>
    /// Digests one account's current value, bound to the account it came from.
    /// </summary>
    /// <remarks>
    /// The account name is part of the preimage so a digest taken for one slot cannot authorize a
    /// compare-removal of another. Without that binding, three accounts that happened to hold the same
    /// bytes would each satisfy the other's removal check.
    /// </remarks>
    private Result<CovenantDigest?> ReadAccountValueDigest(string account)
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

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "A profile-namespaced restore credential could not be read.");

        }

        if (result.Status is OsCredentialStoreStatus.NotFound)
        {

            return Result<CovenantDigest?>.Success(null);

        }

        if (result.Status is not OsCredentialStoreStatus.Ok || result.Value is not { } value)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "A profile-namespaced restore credential could not be read.");

        }

        return Result<CovenantDigest?>.Success(AccountValueDigest(account, value));

    }

    /// <summary>
    /// The account-bound digest a later compare-removal reproduces before it deletes anything.
    /// </summary>
    internal static CovenantDigest AccountValueDigest(string account, string value)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(account);

        ArgumentNullException.ThrowIfNull(value);

        using MemoryStream preimage = new();

        preimage.Write(Encoding.ASCII.GetBytes(AccountValueDomain));

        preimage.WriteByte(0x00);

        WriteLengthPrefixed(preimage, Encoding.UTF8.GetBytes(account));

        WriteLengthPrefixed(preimage, Encoding.UTF8.GetBytes(value));

        return new CovenantDigest(SHA256.HashData(preimage.ToArray()));

    }

    private static void WriteLengthPrefixed(MemoryStream target, byte[] value)
    {

        Span<byte> length = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(length, checked((ulong)value.LongLength));

        target.Write(length);

        target.Write(value);

    }

    private static void WriteGuid(MemoryStream target, Guid value)
    {

        Span<byte> encoded = stackalloc byte[16];

        _ = value.TryWriteBytes(encoded, bigEndian: true, out _);

        target.Write(encoded);

    }

    private static void WriteOptionalGuid(MemoryStream target, Guid? value)
    {

        if (value is { } present)
        {

            target.WriteByte(PresentField);

            WriteGuid(target, present);

            return;

        }

        target.WriteByte(AbsentField);

    }

    private static void WriteOptionalUInt64(MemoryStream target, ulong? value)
    {

        if (value is { } present)
        {

            target.WriteByte(PresentField);

            Span<byte> encoded = stackalloc byte[sizeof(ulong)];

            BinaryPrimitives.WriteUInt64BigEndian(encoded, present);

            target.Write(encoded);

            return;

        }

        target.WriteByte(AbsentField);

    }

    private static void WriteOptionalDigest(MemoryStream target, CovenantDigest? value)
    {

        if (value is { } present)
        {

            target.WriteByte(PresentField);

            target.Write(present.Bytes);

            return;

        }

        target.WriteByte(AbsentField);

    }

    /// <summary>
    /// One content-free refusal for every non-terminal shape.
    /// </summary>
    /// <remarks>
    /// Indistinguishable on purpose. "A restore is running", "the anchor belongs to another profile",
    /// and "somebody removed one of the three accounts" are exactly the distinctions that would help
    /// an attacker steer an installation being erased, and none of them changes the answer: the
    /// credentials stay.
    /// </remarks>
    private static Result<BackupRestoreFullResetTerminalProjectionV1> NotTerminal() =>
        Result<BackupRestoreFullResetTerminalProjectionV1>.Failure(
            new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "This profile's restore state is not provably terminal."));

    private sealed record AccountValueDigests(
        CovenantDigest? Installation,
        CovenantDigest? JournalKey,
        CovenantDigest? Anchor);

}
