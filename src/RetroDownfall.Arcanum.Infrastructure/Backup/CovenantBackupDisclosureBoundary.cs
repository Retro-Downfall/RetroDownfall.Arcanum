using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The two durability barriers a physical backup crosses before it can read or write a byte.
/// </summary>
/// <remarks>
/// An encrypted archive is a nonrevocable disclosure: once the file exists, deleting the installation
/// does not unmake it. The acknowledgement therefore commits <em>before</em> the snapshot reads its
/// first page and again before the archive codec writes its first byte, never after. A journal
/// written afterwards cannot record the one case it exists for — a backup that produced bytes and
/// then crashed — and the operator would be told nothing left while an archive sat on disk (§10.13).
/// </remarks>
internal interface ICovenantBackupDisclosureBoundary
{

    /// <summary>
    /// Commits the snapshot-read acknowledgement and returns the physical attempt it was assigned.
    /// </summary>
    Task<Result<CovenantBackupDisclosureAcknowledgement>> BeforeSnapshotReadAsync(
        Guid operationId,
        CovenantDigest backupIdentity,
        CovenantDigest destinationIdentity,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits the archive-write acknowledgement and returns the physical attempt it was assigned.
    /// </summary>
    Task<Result<CovenantBackupDisclosureAcknowledgement>> BeforeArchiveWriteAsync(
        Guid operationId,
        CovenantDigest backupIdentity,
        CovenantDigest destinationIdentity,
        CancellationToken cancellationToken);

}

/// <summary>
/// Which of the two physical effects one acknowledgement covers.
/// </summary>
/// <remarks>
/// Two separate effects rather than one backup-wide receipt. Reading the protected database and
/// writing an encrypted archive fail independently and leave different residue, so counting them
/// together would report a disclosure that never happened or miss one that did.
/// </remarks>
internal enum CovenantBackupDisclosureEffect : byte
{

    SnapshotRead = 1,

    ArchiveWrite = 2,

}

/// <summary>
/// The durable proof that one backup effect was accounted for before it happened.
/// </summary>
/// <remarks>
/// Content-free: identities, an ordinal, and the receipt digest. Nothing here names a destination
/// path, because recording where an archive was written would recreate inside the journal exactly the
/// exposure the journal exists to account for.
/// </remarks>
internal sealed record CovenantBackupDisclosureAcknowledgement(
    Guid OperationId,
    CovenantBackupDisclosureEffect Effect,
    ulong PhysicalAttemptOrdinal,
    CovenantDigest EffectIdentityDigest,
    CovenantDigest ReceiptDigest);

/// <summary>
/// The one implementation, backed by the durable disclosure journal.
/// </summary>
/// <remarks>
/// Each attempt gets its own effect identity, so a retry after a failed snapshot or archive write is
/// counted as a second physical disclosure rather than folded into the first. Counting is allowed to
/// overstate and never to understate: a backup that may have produced bytes has to be reported as one
/// that did.
/// </remarks>
internal sealed class CovenantBackupDisclosureBoundary(
    ICovenantDisclosureJournal journal,
    Func<Guid> installationIdentity,
    TimeProvider timeProvider) : ICovenantBackupDisclosureBoundary
{

    internal const string EffectDomain = "Arcanum.Covenant.BackupDisclosureEffect.v1";

    /// <summary>
    /// Physical attempts already allocated for one operation and effect.
    /// </summary>
    /// <remarks>
    /// Kept per boundary instance because one instance serves one backup operation's attempts. The
    /// durable ordinal comes back from the journal; this counter only distinguishes attempts within
    /// the run so a retry cannot present the first attempt's effect identity.
    /// </remarks>
    private readonly Dictionary<CovenantBackupDisclosureEffect, ulong> _attempts = [];

    private readonly Lock _gate = new();

    public Task<Result<CovenantBackupDisclosureAcknowledgement>> BeforeSnapshotReadAsync(
        Guid operationId,
        CovenantDigest backupIdentity,
        CovenantDigest destinationIdentity,
        CancellationToken cancellationToken) =>
        AcknowledgeAsync(
            operationId,
            CovenantBackupDisclosureEffect.SnapshotRead,
            backupIdentity,
            destinationIdentity,
            cancellationToken);

    public Task<Result<CovenantBackupDisclosureAcknowledgement>> BeforeArchiveWriteAsync(
        Guid operationId,
        CovenantDigest backupIdentity,
        CovenantDigest destinationIdentity,
        CancellationToken cancellationToken) =>
        AcknowledgeAsync(
            operationId,
            CovenantBackupDisclosureEffect.ArchiveWrite,
            backupIdentity,
            destinationIdentity,
            cancellationToken);

    /// <summary>
    /// The frozen effect preimage. Every field is an identity or a counter; none is a path.
    /// </summary>
    internal static CovenantDigest EffectIdentity(
        Guid installationId,
        Guid operationId,
        CovenantBackupDisclosureEffect effect,
        ulong physicalAttemptOrdinal,
        CovenantDigest backupIdentity,
        CovenantDigest destinationIdentity)
    {

        byte[] preimage = new byte[
            EffectDomain.Length + 1 + 16 + 16 + 1 + sizeof(ulong) + 32 + 32];

        int written = Encoding.ASCII.GetBytes(EffectDomain, preimage);

        preimage[written++] = 0x00;

        _ = installationId.TryWriteBytes(preimage.AsSpan(written), bigEndian: true, out int identityBytes);

        written += identityBytes;

        _ = operationId.TryWriteBytes(preimage.AsSpan(written), bigEndian: true, out int operationBytes);

        written += operationBytes;

        preimage[written++] = (byte)effect;

        BinaryPrimitives.WriteUInt64BigEndian(preimage.AsSpan(written), physicalAttemptOrdinal);

        written += sizeof(ulong);

        backupIdentity.Bytes.CopyTo(preimage.AsSpan(written));

        written += 32;

        destinationIdentity.Bytes.CopyTo(preimage.AsSpan(written));

        written += 32;

        return new CovenantDigest(SHA256.HashData(preimage.AsSpan(0, written)));

    }

    private async Task<Result<CovenantBackupDisclosureAcknowledgement>> AcknowledgeAsync(
        Guid operationId,
        CovenantBackupDisclosureEffect effect,
        CovenantDigest backupIdentity,
        CovenantDigest destinationIdentity,
        CancellationToken cancellationToken)
    {

        if (operationId == Guid.Empty || !backupIdentity.IsValid || !destinationIdentity.IsValid)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A backup disclosure acknowledgement requires a complete operation and effect identity.");

        }

        ulong attempt;

        lock (_gate)
        {

            attempt = _attempts.TryGetValue(effect, out ulong previous) ? previous + 1 : 1;

            _attempts[effect] = attempt;

        }

        // Read now rather than at container build time. The identity is published by authority
        // convergence during startup, and a value captured before that would be the empty one.
        Guid installationId = installationIdentity();

        if (installationId == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                "This installation has no established Covenant authority to disclose under.");

        }

        CovenantDigest effectIdentity = EffectIdentity(
            installationId,
            operationId,
            effect,
            attempt,
            backupIdentity,
            destinationIdentity);

        // A full physical backup of a Covenant-enabled installation carries the whole protected
        // family, so the disclosure is declared derived rather than measured row by row. Overstating
        // here is the safe direction: the journal may never understate what left.
        GenerationProvenance provenance = GenerationProvenance.CreateExact([operationId]);

        ProviderCallSensitivity sensitivity = new(
            ContentSensitivity.CovenantDerived,
            provenance,
            CovenantDigests.Sensitivity(new SensitivityDigestInput(
                ContentSensitivity.CovenantDerived,
                provenance.Mode,
                provenance.ExactGenerationIds,
                provenance.BloomBits)));

        CovenantDisclosureDraft draft = new(
            installationId,
            CovenantDisclosureSubjectKind.Operation,
            operationId,
            effectIdentity,
            CovenantEgressDestination.EncryptedBackup,

            // Nonrevocable by construction. Local erasure cannot reach a file the operator already
            // holds, and reporting it as revocable would promise an undo Arcanum cannot perform.
            CovenantDisclosureRevocability.Nonrevocable,
            destinationIdentity,
            sensitivity.Digest,
            null,
            null,
            backupIdentity,
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

        Result<CovenantDisclosureReceipt> acknowledged = await journal.AcknowledgeAsync(
            draft,
            CovenantDisclosureEffectCategory.EncryptedBackup,
            sensitivity,
            cancellationToken).ConfigureAwait(false);

        return acknowledged.IsFailure
            ? acknowledged.Error
            : new CovenantBackupDisclosureAcknowledgement(
                operationId,
                effect,
                acknowledged.Value.AllocatedSubjectOrdinal,
                effectIdentity,
                acknowledged.Value.Digest);

    }

}
