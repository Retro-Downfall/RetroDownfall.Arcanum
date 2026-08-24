using System.Collections.Immutable;

using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// The four domain-separated commitments an attested full-reset managed-file reconciliation makes.
/// </summary>
/// <remarks>
/// Every preimage is built with <see cref="FullInstallationResetCanonicalEvidenceV1"/>, so the byte
/// layout is the same one the marker-pair evidence already uses: the ASCII domain, a single zero
/// separator, checked big-endian <c>u64</c> counts, and RFC-4122 network-order UUID bytes. Nothing
/// here is length-framed text, decimal, hexadecimal, or base64.
///
/// <para>None of these preimages carries a durable location, root, revision, parent segment, leaf,
/// file identity, content hash, pending label, final ownership, or serialized opener input. The
/// reconciliation is a statement about which rows reached which terminal state, and a commitment that
/// also described what was on disk would put the sensitive part of a deleted file back into the
/// authenticated record that outlives it.</para>
/// </remarks>
internal static class FullInstallationResetManagedFileDigests
{

    private const string SourceWriteIntentVectorDomain =
        "Arcanum.FullInstallationReset.ManagedWriteIntentVector.v1";

    private const string LocalErasureWorkItemVectorDomain =
        "Arcanum.FullInstallationReset.LocalErasureWorkItemVector.v1";

    private const string TerminalClassificationDomain =
        "Arcanum.FullInstallationReset.ManagedFileTerminalClassification.v1";

    private const string BlockerEvidenceDomain =
        "Arcanum.FullInstallationReset.ManagedFileBlockerEvidence.v1";

    private const byte Absent = 0x00;

    private const byte Present = 0x01;

    /// <summary>
    /// The exact ordered source write-operation identities this reconciliation is scoped to.
    /// </summary>
    internal static Result<byte[]> SourceWriteIntentVectorPreimage(
        ImmutableArray<Guid> sourceWriteOperationIds) =>
        IdentityVectorPreimage(SourceWriteIntentVectorDomain, sourceWriteOperationIds);

    /// <summary>
    /// The exact ordered local-erasure work-item identities this reconciliation is scoped to.
    /// </summary>
    internal static Result<byte[]> LocalErasureWorkItemVectorPreimage(
        ImmutableArray<Guid> workItemIds) =>
        IdentityVectorPreimage(LocalErasureWorkItemVectorDomain, workItemIds);

    internal static Result<CovenantDigest> SourceWriteIntentVector(
        ImmutableArray<Guid> sourceWriteOperationIds) =>
        Hash(SourceWriteIntentVectorPreimage(sourceWriteOperationIds));

    internal static Result<CovenantDigest> LocalErasureWorkItemVector(
        ImmutableArray<Guid> workItemIds) =>
        Hash(LocalErasureWorkItemVectorPreimage(workItemIds));

    /// <summary>
    /// The content-free commitment one manual arm makes about one refused row.
    /// </summary>
    /// <remarks>
    /// Three fields and no more: which row, which half of the inventory it belongs to, and which
    /// closed blocker code refused it. An operator still has to look at the file itself, which is the
    /// point — this digest exists so a later resume can prove it is looking at the same refusal, not
    /// so anything can reconstruct what the refusal was about.
    /// </remarks>
    internal static Result<CovenantDigest> BlockerEvidence(
        Guid identity,
        FullInstallationResetManagedFileBlockerArm arm,
        CovenantErasureBlocker blocker)
    {

        if (identity == Guid.Empty
            || arm is not FullInstallationResetManagedFileBlockerArm.ManualWriteOrphan
                and not FullInstallationResetManagedFileBlockerArm.ManualWorkItemOrphan
            || blocker is CovenantErasureBlocker.None)
        {

            return Invalid<CovenantDigest>();

        }

        using MemoryStream preimage =
            FullInstallationResetCanonicalEvidenceV1.Start(BlockerEvidenceDomain);

        FullInstallationResetCanonicalEvidenceV1.WriteGuid(preimage, identity);

        preimage.WriteByte((byte)arm);

        preimage.WriteByte((byte)blocker);

        return new CovenantDigest(SHA256.HashData(preimage.ToArray()));

    }

    /// <summary>
    /// The commitment made at <c>TerminalInventoryVerified</c>, over both halves of the inventory.
    /// </summary>
    internal static Result<byte[]> TerminalClassificationPreimage(
        ImmutableArray<FullInstallationResetManagedSourceClassificationV1> sources,
        ImmutableArray<FullInstallationResetManagedWorkItemClassificationV1> workItems)
    {

        if (sources.IsDefault
            || workItems.IsDefault
            || sources.Length > FullInstallationResetManagedFileBounds.MaximumVectorCount
            || workItems.Length > FullInstallationResetManagedFileBounds.MaximumVectorCount)
        {

            return Invalid<byte[]>();

        }

        FullInstallationResetManagedSourceClassificationV1[] copiedSources = [.. sources];

        FullInstallationResetManagedWorkItemClassificationV1[] copiedWorkItems = [.. workItems];

        if (!IsCanonicallyOrdered(
                copiedSources,
                static source => source.SourceWriteOperationId)
            || !IsCanonicallyOrdered(
                copiedWorkItems,
                static workItem => workItem.WorkItemId)
            || Array.Exists(copiedSources, static source => !IsCoherent(source))
            || Array.Exists(copiedWorkItems, static workItem => !IsCoherent(workItem)))
        {

            return Invalid<byte[]>();

        }

        using MemoryStream preimage =
            FullInstallationResetCanonicalEvidenceV1.Start(TerminalClassificationDomain);

        FullInstallationResetCanonicalEvidenceV1.WriteUInt64(
            preimage,
            checked((ulong)copiedSources.LongLength));

        foreach (FullInstallationResetManagedSourceClassificationV1 source in copiedSources)
        {

            FullInstallationResetCanonicalEvidenceV1.WriteGuid(
                preimage,
                source.SourceWriteOperationId);

            preimage.WriteByte((byte)source.TerminalPhase);

            WriteOptionalDigest(preimage, source.BlockerEvidenceDigest);

        }

        FullInstallationResetCanonicalEvidenceV1.WriteUInt64(
            preimage,
            checked((ulong)copiedWorkItems.LongLength));

        foreach (FullInstallationResetManagedWorkItemClassificationV1 workItem in copiedWorkItems)
        {

            FullInstallationResetCanonicalEvidenceV1.WriteGuid(preimage, workItem.WorkItemId);

            preimage.WriteByte((byte)workItem.TerminalState);

            if (workItem.DeletionEvidence is { } evidence)
            {

                preimage.WriteByte(Present);

                preimage.WriteByte((byte)evidence);

            }
            else
            {

                preimage.WriteByte(Absent);

            }

            WriteOptionalDigest(preimage, workItem.BlockerEvidenceDigest);

        }

        return preimage.ToArray();

    }

    internal static Result<CovenantDigest> TerminalClassification(
        ImmutableArray<FullInstallationResetManagedSourceClassificationV1> sources,
        ImmutableArray<FullInstallationResetManagedWorkItemClassificationV1> workItems) =>
        Hash(TerminalClassificationPreimage(sources, workItems));

    private static Result<byte[]> IdentityVectorPreimage(
        string domain,
        ImmutableArray<Guid> identities)
    {

        if (identities.IsDefault
            || identities.Length > FullInstallationResetManagedFileBounds.MaximumVectorCount)
        {

            return Invalid<byte[]>();

        }

        Guid[] copied = [.. identities];

        if (!IsCanonicallyOrdered(copied, static identity => identity))
        {

            return Invalid<byte[]>();

        }

        using MemoryStream preimage = FullInstallationResetCanonicalEvidenceV1.Start(domain);

        FullInstallationResetCanonicalEvidenceV1.WriteUInt64(
            preimage,
            checked((ulong)copied.LongLength));

        foreach (Guid identity in copied)
        {

            FullInstallationResetCanonicalEvidenceV1.WriteGuid(preimage, identity);

        }

        return preimage.ToArray();

    }

    /// <summary>
    /// Requires strictly ascending RFC-4122 network order, which also rejects empties and duplicates.
    /// </summary>
    /// <remarks>
    /// Ordered rather than sorted here on purpose. These identities come out of a database read whose
    /// order is part of what the checkpoint commits to, so a vector that quietly sorted itself would
    /// authenticate an inventory nobody actually observed.
    /// </remarks>
    private static bool IsCanonicallyOrdered<T>(T[] entries, Func<T, Guid> identity)
    {

        bool first = true;

        Guid previous = Guid.Empty;

        foreach (T entry in entries)
        {

            Guid current = identity(entry);

            if (current == Guid.Empty)
            {

                return false;

            }

            if (!first
                && FullInstallationResetCanonicalEvidenceV1.CompareGuid(previous, current) >= 0)
            {

                return false;

            }

            previous = current;

            first = false;

        }

        return true;

    }

    /// <summary>
    /// A source is coherent when its recorded phase and its blocker evidence agree about the arm.
    /// </summary>
    /// <remarks>
    /// <c>AdoptedAndLabeled</c> is accepted here and only here with blocker evidence attached. It is
    /// the exact phase of a source whose file could not be authenticated: its erasure work item
    /// refused, so the producer legitimately still owns a file, and the honest record of that is the
    /// phase the row actually holds rather than a terminal one it never reached. Without blocker
    /// evidence the same phase means the reconciliation simply never finished, which is refused.
    /// </remarks>
    private static bool IsCoherent(FullInstallationResetManagedSourceClassificationV1 source) =>
        source.TerminalPhase switch
        {
            ManagedFileWriteIntentPhase.Cleaned or ManagedFileWriteIntentPhase.Erased =>
                source.BlockerEvidenceDigest is null,
            ManagedFileWriteIntentPhase.ManualNonrevocable
                or ManagedFileWriteIntentPhase.AdoptedAndLabeled =>
                source.BlockerEvidenceDigest is not null,
            _ => false,
        };

    private static bool IsCoherent(FullInstallationResetManagedWorkItemClassificationV1 workItem) =>
        workItem.TerminalState switch
        {
            LocalErasureWorkItemState.Completed =>
                workItem.DeletionEvidence is not null
                && workItem.BlockerEvidenceDigest is null,
            LocalErasureWorkItemState.ManualBlocker =>
                workItem.DeletionEvidence is null
                && workItem.BlockerEvidenceDigest is not null,
            _ => false,
        };

    private static void WriteOptionalDigest(MemoryStream preimage, CovenantDigest? digest)
    {

        if (digest is { } present)
        {

            preimage.WriteByte(Present);

            FullInstallationResetCanonicalEvidenceV1.WriteDigest(preimage, present);

            return;

        }

        preimage.WriteByte(Absent);

    }

    private static Result<T> Invalid<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Data.InvalidRequest,
            "The full-installation reset managed-file evidence is invalid."));

    private static Result<CovenantDigest> Hash(Result<byte[]> preimage) =>
        preimage.IsFailure
            ? Result<CovenantDigest>.Failure(preimage.Error)
            : new CovenantDigest(SHA256.HashData(preimage.Value));

}
