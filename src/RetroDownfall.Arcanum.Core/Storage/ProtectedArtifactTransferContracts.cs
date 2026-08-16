using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Which protected transfer one effect digest belongs to.
/// </summary>
/// <remarks>
/// Numbered and closed because the code is part of the digest preimage. An import and a fork of the
/// same Session into the same destination would otherwise produce the same owner, and one could adopt
/// the other's half-finished journal.
/// </remarks>
public enum ProtectedSessionTransferKind : byte
{

    Import = 1,

    Fork = 2,

}

/// <summary>
/// One explicit source-to-destination Campaign mapping for a selective import.
/// </summary>
/// <remarks>
/// Explicit rather than inferred. The archive's Campaign identity means nothing on this machine, and
/// an import that guessed — by name, by position, or by dropping the binding — would either attach a
/// Session to a Campaign the operator never chose or silently produce an unbound Session whose
/// standing instructions no longer apply to it.
/// </remarks>
public sealed record BackupSessionCampaignMapping(
    Guid SourceCampaignId,
    Guid DestinationCampaignId);

/// <summary>
/// The exact checked counts a transfer manifest authenticates.
/// </summary>
/// <remarks>
/// Counted rather than trusted. The store recomputes every one of these from the source it holds and
/// refuses the whole transfer on a mismatch, so a caller that omitted an artifact cannot get a
/// partial graph committed by simply not mentioning the rest of it.
/// </remarks>
public sealed record ProtectedSessionTransferCounts(
    ulong Sessions,
    ulong Entries,
    ulong Attachments,
    ulong AttachmentBlobs,
    ulong Finalizations,
    ulong SensitivityLabels)
{

    /// <summary>
    /// The ceiling on any single count.
    /// </summary>
    /// <remarks>
    /// Bounded so an overflowed or hostile count cannot be encoded into the digest at all. A count
    /// that cannot be represented is a refusal before destination acquisition, not a wrapped value
    /// that later compares equal to something smaller.
    /// </remarks>
    public const ulong Maximum = 1_048_576;

    /// <summary>
    /// The canonical count-vector digest, or a refusal without hashing.
    /// </summary>
    public Result<CovenantDigest> ComputeVectorDigest()
    {

        ulong[] ordered =
        [
            Sessions,
            Entries,
            Attachments,
            AttachmentBlobs,
            Finalizations,
            SensitivityLabels,
        ];

        foreach (ulong value in ordered)
        {

            if (value > Maximum)
            {

                return new Error(
                    ErrorCodes.Covenant.CapacityExceeded,
                    "A protected transfer manifest count exceeds its approved bound.");

            }

        }

        byte[] preimage = new byte[
            ProtectedSessionTransferDigests.CountVectorDomain.Length
            + 1
            + sizeof(ulong)
            + (ordered.Length * sizeof(ulong))];

        int written = Encoding.ASCII.GetBytes(
            ProtectedSessionTransferDigests.CountVectorDomain,
            preimage);

        preimage[written++] = 0x00;

        BinaryPrimitives.WriteUInt64BigEndian(preimage.AsSpan(written), (ulong)ordered.Length);

        written += sizeof(ulong);

        foreach (ulong value in ordered)
        {

            BinaryPrimitives.WriteUInt64BigEndian(preimage.AsSpan(written), value);

            written += sizeof(ulong);

        }

        return new CovenantDigest(SHA256.HashData(preimage.AsSpan(0, written)));

    }

}

/// <summary>
/// Everything a selective Session import is authorized to do, computed before the destination scope
/// is acquired.
/// </summary>
/// <remarks>
/// Carries no graph, no attachment bytes, no label decisions, no serializable source capability, and
/// no live lease. Those all travel separately, through the source lease the store borrows, because a
/// request that could carry them would be a request that could describe a transfer nobody read the
/// source for.
/// </remarks>
public sealed record ImportedSessionTransferRequest
{

    public ImportedSessionTransferRequest(
        Guid operationId,
        Guid sourceSessionId,
        Guid destinationSessionId,
        CovenantDigest sourceEvidenceDigest,
        CovenantDigest sourceManifestDigest,
        ProtectedSessionTransferCounts manifestCounts,
        BackupSessionCampaignMapping? campaignMapping,
        CovenantDigest destinationBindingDigest,
        CovenantDigest transferEffectDigest)
    {

        OperationId = operationId != Guid.Empty
            ? operationId
            : throw new ArgumentException("A transfer operation identity cannot be empty.", nameof(operationId));

        SourceSessionId = sourceSessionId != Guid.Empty
            ? sourceSessionId
            : throw new ArgumentException("A source Session identity cannot be empty.", nameof(sourceSessionId));

        DestinationSessionId = destinationSessionId != Guid.Empty
            ? destinationSessionId
            : throw new ArgumentException(
                "A destination Session identity cannot be empty.",
                nameof(destinationSessionId));

        SourceEvidenceDigest = Require(sourceEvidenceDigest, nameof(sourceEvidenceDigest));

        SourceManifestDigest = Require(sourceManifestDigest, nameof(sourceManifestDigest));

        ManifestCounts = manifestCounts ?? throw new ArgumentNullException(nameof(manifestCounts));

        CampaignMapping = campaignMapping;

        DestinationBindingDigest = Require(destinationBindingDigest, nameof(destinationBindingDigest));

        TransferEffectDigest = Require(transferEffectDigest, nameof(transferEffectDigest));

    }

    public Guid OperationId { get; }

    public Guid SourceSessionId { get; }

    public Guid DestinationSessionId { get; }

    public CovenantDigest SourceEvidenceDigest { get; }

    public CovenantDigest SourceManifestDigest { get; }

    public ProtectedSessionTransferCounts ManifestCounts { get; }

    /// <summary>
    /// The explicit destination Campaign, or <see langword="null"/> for a source Session that was
    /// never Campaign-bound.
    /// </summary>
    public BackupSessionCampaignMapping? CampaignMapping { get; }

    public CovenantDigest DestinationBindingDigest { get; }

    public CovenantDigest TransferEffectDigest { get; }

    private static CovenantDigest Require(CovenantDigest value, string parameterName) =>
        value.IsValid
            ? value
            : throw new ArgumentException("The Covenant digest is uninitialized.", parameterName);

}

/// <summary>
/// What one committed import produced, in counts only.
/// </summary>
public sealed record ImportedSessionCommitReceipt(
    Guid OperationId,
    Guid DestinationSessionId,
    Guid? DestinationCampaignId,
    long Entries,
    long Attachments,
    long DeduplicatedBlobs,
    long ImportedFinalizations);

/// <summary>
/// The result of a protected transfer, together with the one decision the caller now owes its lease.
/// </summary>
/// <remarks>
/// Nonserializable, deep-copies no artifact, and never owns the caller's lease. The store proved what
/// happened; only the caller can spend the lease's single reopening decision, and the finalizer must
/// not run until that decision has actually succeeded (§10.12).
/// </remarks>
public sealed class ProtectedSessionTransferCompletion<T>
{

    public ProtectedSessionTransferCompletion(
        Result<T> result,
        CovenantExclusiveLeaseDisposition disposition,
        ICovenantExclusivePostDispositionFinalizer finalizer)
    {

        Result = result;

        Disposition = disposition is CovenantExclusiveLeaseDisposition.RollbackAndReopen
            or CovenantExclusiveLeaseDisposition.CommitAndReopen
            or CovenantExclusiveLeaseDisposition.KeepClosed
            ? disposition
            : throw new ArgumentOutOfRangeException(nameof(disposition));

        Finalizer = finalizer ?? throw new ArgumentNullException(nameof(finalizer));

    }

    public Result<T> Result { get; }

    public CovenantExclusiveLeaseDisposition Disposition { get; }

    public ICovenantExclusivePostDispositionFinalizer Finalizer { get; }

}

/// <summary>
/// The one preimage every protected transfer effect digest is built from.
/// </summary>
/// <remarks>
/// Never accepted as caller input. The digest is what binds an exclusive owner to a specific
/// destructive plan, so a caller that could supply it directly could hand recovery a plan the request
/// does not describe and have it finished under this installation's authority.
/// </remarks>
public static class ProtectedSessionTransferDigests
{

    public const string EffectDomain = "Arcanum.Covenant.ProtectedSessionTransfer.v1";

    public const string CountVectorDomain = "Arcanum.Covenant.ProtectedSessionTransfer.Counts.v1";

    public const string DestinationBindingDomain =
        "Arcanum.Covenant.ProtectedSessionTransfer.DestinationBinding.v1";

    public const string ManifestDomain = "Arcanum.Covenant.ProtectedSessionTransfer.Manifest.v1";

    /// <summary>
    /// Binds the destination scope and its explicit Campaign mapping.
    /// </summary>
    public static CovenantDigest DestinationBinding(
        CovenantScope scope,
        Guid? destinationCampaignId,
        Guid? sourceCampaignId)
    {

        byte[] preimage = new byte[
            DestinationBindingDomain.Length + 1 + 1 + 1 + 16 + 1 + 16];

        int written = Encoding.ASCII.GetBytes(DestinationBindingDomain, preimage);

        preimage[written++] = 0x00;

        preimage[written++] = (byte)scope;

        written += WriteOptionalUuid(preimage.AsSpan(written), destinationCampaignId);

        written += WriteOptionalUuid(preimage.AsSpan(written), sourceCampaignId);

        return new CovenantDigest(SHA256.HashData(preimage.AsSpan(0, written)));

    }

    /// <summary>
    /// Folds one ordered artifact preimage into a running manifest digest.
    /// </summary>
    /// <remarks>
    /// Order matters and is the caller's responsibility to keep canonical. A manifest whose items
    /// could be reordered without changing the digest would not detect a graph that lost one row and
    /// gained another.
    /// </remarks>
    public static CovenantDigest Manifest(ImmutableArray<byte[]> orderedItemPreimages)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(ManifestDomain));

        hash.AppendData([0x00]);

        Span<byte> count = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(
            count,
            orderedItemPreimages.IsDefault ? 0UL : checked((ulong)orderedItemPreimages.Length));

        hash.AppendData(count);

        Span<byte> length = stackalloc byte[sizeof(uint)];

        foreach (byte[] item in orderedItemPreimages.IsDefault ? [] : orderedItemPreimages)
        {

            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)item.Length));

            hash.AppendData(length);

            hash.AppendData(item);

        }

        return new CovenantDigest(hash.GetHashAndReset());

    }

    /// <summary>
    /// Computes the effect digest, or refuses without hashing.
    /// </summary>
    public static Result<CovenantDigest> Effect(
        ProtectedSessionTransferKind kind,
        Guid operationId,
        Guid sourceSessionId,
        Guid? cutoffEntryId,
        Guid destinationSessionId,
        CovenantDigest destinationBindingDigest,
        CovenantDigest? sourceEvidenceDigest,
        CovenantDigest? manifestDigest,
        ProtectedSessionTransferCounts counts)
    {

        if (kind is not ProtectedSessionTransferKind.Import and not ProtectedSessionTransferKind.Fork
            || operationId == Guid.Empty
            || sourceSessionId == Guid.Empty
            || destinationSessionId == Guid.Empty
            || cutoffEntryId == Guid.Empty
            || !destinationBindingDigest.IsValid
            || counts is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A protected transfer effect digest requires a complete, well-formed plan.");

        }

        if (sourceEvidenceDigest is { IsValid: false } || manifestDigest is { IsValid: false })
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A protected transfer effect digest cannot carry a malformed evidence digest.");

        }

        Result<CovenantDigest> countVector = counts.ComputeVectorDigest();

        if (countVector.IsFailure)
        {

            return countVector.Error;

        }

        byte[] preimage = new byte[
            EffectDomain.Length + 1 + 1 + 16 + 16 + 1 + 16 + 16 + 32 + 1 + 32 + 1 + 32 + 32];

        int written = Encoding.ASCII.GetBytes(EffectDomain, preimage);

        preimage[written++] = 0x00;

        preimage[written++] = (byte)kind;

        written += WriteUuid(preimage.AsSpan(written), operationId);

        written += WriteUuid(preimage.AsSpan(written), sourceSessionId);

        written += WriteOptionalUuid(preimage.AsSpan(written), cutoffEntryId);

        written += WriteUuid(preimage.AsSpan(written), destinationSessionId);

        destinationBindingDigest.Bytes.CopyTo(preimage.AsSpan(written));

        written += 32;

        written += WriteOptionalDigest(preimage.AsSpan(written), sourceEvidenceDigest);

        written += WriteOptionalDigest(preimage.AsSpan(written), manifestDigest);

        countVector.Value.Bytes.CopyTo(preimage.AsSpan(written));

        written += 32;

        return new CovenantDigest(SHA256.HashData(preimage.AsSpan(0, written)));

    }

    private static int WriteUuid(Span<byte> destination, Guid value)
    {

        if (!value.TryWriteBytes(destination, bigEndian: true, out int written) || written != 16)
        {

            throw new InvalidOperationException("The identity could not be encoded in network byte order.");

        }

        return written;

    }

    private static int WriteOptionalUuid(Span<byte> destination, Guid? value)
    {

        // A presence byte rather than a zero UUID: "absent" and "the nil identity" have to be
        // different preimages, or a caller could substitute one for the other.
        if (value is not { } present)
        {

            destination[0] = 0x00;

            return 1;

        }

        destination[0] = 0x01;

        return 1 + WriteUuid(destination[1..], present);

    }

    private static int WriteOptionalDigest(Span<byte> destination, CovenantDigest? value)
    {

        if (value is not { } present)
        {

            destination[0] = 0x00;

            return 1;

        }

        destination[0] = 0x01;

        present.Bytes.CopyTo(destination[1..]);

        return 1 + 32;

    }

}
