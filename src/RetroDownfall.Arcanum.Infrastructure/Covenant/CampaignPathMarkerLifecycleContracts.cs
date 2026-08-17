using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// Which two-phase marker operation one durable intent belongs to, matching
/// <c>campaign_path_marker_intents.IntentKindCode</c>.
/// </summary>
/// <remarks>
/// The kind and its authorizing gate owner are one decision. A restore cleanup row is owned by a
/// global <see cref="CovenantExclusiveOperation.BackupRestore"/> lease, and a full-reset row has no
/// in-process owner at all — so a kind that could be relabelled after the fact would let ordinary
/// startup recovery adopt work whose authority it never held.
/// </remarks>
internal enum CampaignPathMarkerIntentKind : byte
{

    PathMutation = 1,

    CampaignDelete = 2,

    RestoreCleanup = 3,

    FullInstallationResetCleanup = 4,

}

/// <summary>
/// The durable phase ladder every marker intent climbs, matching
/// <c>campaign_path_marker_intents.PhaseCode</c>.
/// </summary>
/// <remarks>
/// Each value is written <em>after</em> the filesystem effect it names and before the next one is
/// attempted, so a crash always leaves the row naming the last effect that is known to have happened.
/// Recovery therefore never has to guess whether the syscall it was about to make already ran.
/// </remarks>
internal enum CampaignPathMarkerPhase : byte
{

    Prepared = 1,

    TempCreated = 2,

    TempWritten = 3,

    TempFsynced = 4,

    RenamedNoReplace = 5,

    ParentFsynced = 6,

    TargetReopenedOrAbsent = 7,

    CodecOrAbsenceVerified = 8,

    DatabaseStateCommitted = 9,

    SensitiveMaterialDestroyed = 10,

    ReopenPending = 11,

    Completed = 12,

    Compensated = 13,

    ManualBlocker = 14,

    OrphanReopenPending = 15,

    Orphaned = 16,

}

/// <summary>
/// Whether a cleanup target's root was reopened, or exactly why it could not be.
/// </summary>
internal enum CampaignPathCleanupRootBlocker : byte
{

    RootUnavailable = 1,

    PhysicalIdentityMismatch = 2,

    OwnershipMismatch = 3,

}

/// <summary>
/// Content-free evidence that a cleanup target could not be opened.
/// </summary>
/// <remarks>
/// Digests and a closed code, never a path or a handle. This value is what a blocked cleanup records
/// and what an operator later reads, and a blocker that carried a reopenable path would hand file
/// authority to exactly the branch that just proved it does not have it.
/// </remarks>
internal sealed record CampaignPathCleanupRootBlockerEvidence(
    CampaignPathCleanupRootBlocker Blocker,
    CovenantDigest AuthenticatedInventoryEntryDigest,
    CovenantDigest ObservationDigest);

/// <summary>
/// The closed set of answers to "can this Campaign root be worked on".
/// </summary>
/// <remarks>
/// Only <see cref="Opened"/> carries authority. The two blocked arms carry evidence and nothing else,
/// which is what makes "restore requires every destination seed to be opened" a type-level rule rather
/// than a runtime convention.
/// </remarks>
internal abstract record CampaignPathCleanupRootObservation
{

    private CampaignPathCleanupRootObservation()
    {
    }

    internal sealed record Opened(
        CampaignPathMarkerRootAuthority RootAuthority)
        : CampaignPathCleanupRootObservation;

    internal sealed record Unavailable(
        CampaignPathCleanupRootBlockerEvidence Evidence)
        : CampaignPathCleanupRootObservation;

    internal sealed record Mismatch(
        CampaignPathCleanupRootBlockerEvidence Evidence)
        : CampaignPathCleanupRootObservation;

}

/// <summary>
/// One Campaign a restore must clean the marker off, together with how its root was observed.
/// </summary>
/// <remarks>
/// Nonserializable, and deliberately so: the <see cref="Observation"/> can hold a live capability, and
/// a seed that round-tripped through JSON would be a filesystem authority a caller could mint from a
/// document.
///
/// <para><see cref="CanonicalDisplayPath"/> grants nothing. While this process holds the observation's
/// root authority every effect goes through it, so a display path that becomes a symlink between
/// preparation and reconciliation redirects nothing. A restarted process has no such authority left and
/// reopens the recorded path once, but adopts what it finds only after the marker living there derives
/// that directory's exact identity — the name is where to look, never permission to act (§10.19.7).</para>
/// </remarks>
internal sealed record CampaignPathRestoreCleanupSeed(
    Guid CampaignId,
    long PriorPathRevision,
    CovenantDigest IndexedIdentityDigest,
    string CanonicalDisplayPath,
    CampaignPathCleanupRootObservation Observation);

/// <summary>
/// The complete restore-cleanup preparation: one global owner and its ordered per-Campaign seeds.
/// </summary>
internal sealed record CampaignPathRestoreCleanupPreparation(
    CovenantExclusiveRecoveryOwner Owner,
    ImmutableArray<CampaignPathRestoreCleanupSeed> OrderedSeeds);

/// <summary>
/// What preparation durably committed, in the exact shape the restore journal has to publish.
/// </summary>
/// <remarks>
/// Content-free. The restore journal copies these four fields verbatim into its marker checkpoint, so
/// anything here that was not an identity or a count would end up inside an authenticated envelope the
/// recovery path decrypts before it has proven anything.
/// </remarks>
internal sealed record CampaignPathRestoreCleanupPreparationReceipt(
    CovenantExclusiveRecoveryOwner Owner,
    ImmutableArray<Guid> OrderedIntentIds,
    ulong IntentCount,
    CovenantDigest IntentVectorDigest)
{

    /// <summary>
    /// Compares the children element by element.
    /// </summary>
    /// <remarks>
    /// Spelled out because <see cref="ImmutableArray{T}"/> compares by underlying-array reference.
    /// Left to the compiler, a receipt reconstructed from the journal after a restart would compare
    /// unequal to the identical receipt preparation produced, and the one comparison that exists to
    /// prove a retry rebuilt the same vector would fail against a vector that is in fact the same.
    /// </remarks>
    public bool Equals(CampaignPathRestoreCleanupPreparationReceipt? other) =>
        other is not null
        && Owner == other.Owner
        && IntentCount == other.IntentCount
        && IntentVectorDigest == other.IntentVectorDigest
        && !OrderedIntentIds.IsDefault
        && !other.OrderedIntentIds.IsDefault
        && OrderedIntentIds.AsSpan().SequenceEqual(other.OrderedIntentIds.AsSpan());

    /// <inheritdoc />
    public override int GetHashCode()
    {

        HashCode hash = new();

        hash.Add(Owner);
        hash.Add(IntentCount);
        hash.Add(IntentVectorDigest);

        foreach (Guid intentId in OrderedIntentIds.IsDefault ? [] : OrderedIntentIds)
        {

            hash.Add(intentId);

        }

        return hash.ToHashCode();

    }

}

/// <summary>
/// The one canonical commitment to a restore's ordered cleanup children.
/// </summary>
/// <remarks>
/// The zero arm is the reason this exists as a frozen literal rather than a computation the caller
/// repeats. "This restore observed no Campaign marker" and "this restore never prepared its cleanup"
/// have to be distinguishable after a crash, and they are only distinguishable if the proven-empty
/// case produces a positive digest that nothing else can present (§10.12).
/// </remarks>
internal static class CampaignPathRestoreCleanupIntentVector
{

    internal const string Domain =
        "Arcanum.Covenant.BackupRestore.MarkerIntentVector.v1";

    /// <summary>
    /// SHA-256 of the domain, one <c>0x00</c> separator, and a checked <c>UInt64BE</c> zero, with no
    /// item bytes. Frozen here so no caller recomputes it and no caller can substitute a different
    /// empty encoding.
    /// </summary>
    internal const string EmptyDigestHex =
        "4da7d983be83d8a8f9c50e927319a6297f451d9c7366ce0df0506882fd1fee64";

    /// <summary>
    /// The ceiling on cleanup children in one restore.
    /// </summary>
    /// <remarks>
    /// Bounded because the resulting vector is copied into the authenticated restore journal, which is
    /// itself a bounded guarded-root payload. An unbounded inventory would let a destination with
    /// enough Campaigns produce a checkpoint the journal cannot hold, and the failure would land after
    /// the staged children were already committed.
    /// </remarks>
    internal const int MaximumIntents = 4_096;

    /// <summary>
    /// Computes the canonical vector digest, or refuses without hashing.
    /// </summary>
    internal static Result<CovenantDigest> Compute(ImmutableArray<Guid> orderedIntentIds)
    {

        if (orderedIntentIds.IsDefault)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "An uninitialized marker-intent vector is not a proven empty inventory.");

        }

        if (orderedIntentIds.Length > MaximumIntents)
        {

            return new Error(
                ErrorCodes.Covenant.CapacityExceeded,
                "This restore names more Campaign cleanup children than one authenticated journal may carry.");

        }

        HashSet<Guid> seen = new(orderedIntentIds.Length);

        foreach (Guid intentId in orderedIntentIds)
        {

            if (intentId == Guid.Empty || !seen.Add(intentId))
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A marker-intent vector must name distinct, nonempty children.");

            }

        }

        byte[] preimage = new byte[
            Domain.Length
            + 1
            + sizeof(ulong)
            + (orderedIntentIds.Length * 16)];

        int written = Encoding.ASCII.GetBytes(Domain, preimage);

        preimage[written++] = 0x00;

        BinaryPrimitives.WriteUInt64BigEndian(
            preimage.AsSpan(written),
            checked((ulong)orderedIntentIds.Length));

        written += sizeof(ulong);

        foreach (Guid intentId in orderedIntentIds)
        {

            if (!intentId.TryWriteBytes(preimage.AsSpan(written), bigEndian: true, out int itemBytes)
                || itemBytes != 16)
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A marker-intent child could not be encoded in network byte order.");

            }

            written += itemBytes;

        }

        return new CovenantDigest(SHA256.HashData(preimage.AsSpan(0, written)));

    }

    /// <summary>
    /// The frozen empty-vector digest as a value, for the zero arm's exact comparisons.
    /// </summary>
    internal static CovenantDigest EmptyDigest { get; } =
        new(Convert.FromHexString(EmptyDigestHex));

}

/// <summary>
/// The exact children a gate-owned reconciliation is allowed to touch.
/// </summary>
internal sealed record CampaignPathMarkerGateReconcileRequest(
    CovenantExclusiveRecoveryOwner Owner,
    ImmutableArray<Guid> OrderedIntentIds,
    CovenantDigest IntentVectorDigest);

/// <summary>
/// How a whole set of marker children ended.
/// </summary>
internal enum CampaignPathMarkerAggregateOutcome : byte
{

    Committed = 1,

    Compensated = 2,

    Orphaned = 3,

}

/// <summary>
/// The aggregate result the caller turns into exactly one gate disposition.
/// </summary>
/// <remarks>
/// The lifecycle never invokes the disposition or the finalizer itself. It has proven what happened on
/// the filesystem; the caller owns the lease and is the only thing that can decide, once, whether
/// admission reopens — and the finalizer runs only after that decision succeeds.
/// </remarks>
internal sealed record CampaignPathMarkerGateCompletion(
    CampaignPathMarkerAggregateOutcome Outcome,
    CovenantExclusiveLeaseDisposition Disposition,
    ICovenantExclusivePostDispositionFinalizer Finalizer);
