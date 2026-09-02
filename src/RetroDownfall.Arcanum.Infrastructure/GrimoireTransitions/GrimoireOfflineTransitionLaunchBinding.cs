using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

/// <summary>
/// The immutable facts a launch checkpoint carries, whichever of the two shapes recorded them.
/// </summary>
/// <remarks>
/// One projection, in the same way the legacy erasure checkpoints have one: the Covenant and factory
/// launches differ in the durable kind they are filed under and in nothing a transition reads, and a
/// second projection would be a second place for the two to disagree.
///
/// <para><see cref="Digest"/> is the value the journal commits to instead of copying these fields.
/// It covers every one of them, so a launch whose source and target were transposed cannot produce
/// the digest an already-published journal is bound to.</para>
/// </remarks>
internal sealed record GrimoireOfflineTransitionLaunchBinding(
    Guid OperationId,
    string OperationKind,
    GrimoireOfflineTransitionKind Kind,
    LongRunningOperationRecoveryPolicy RecoveryPolicy,
    CovenantExclusiveOperation Operation,
    CovenantDigest EffectDigest,
    Guid SourceDatasetGeneration,
    Guid TargetDatasetGeneration,
    GrimoireOfflineTransitionEpochTuple SourceEpochs,
    GrimoireOfflineTransitionEpochTuple TargetEpochs,
    long StartingRevision)
{

    /// <summary>The content-free digest of this launch, and of nothing else.</summary>
    internal CovenantDigest Digest => GrimoireOfflineTransitionLaunch.LaunchBindingDigest(this);

}

/// <summary>
/// What an observed database state is, measured against the launch that preselected its target.
/// </summary>
/// <remarks>
/// Three answers rather than two, because the canonical family transaction is not blindly
/// idempotent: it stamps a new random dataset generation and advances all three epochs, so a
/// process that finds the family already replaced cannot tell its own commit from an unrelated one
/// by inspecting the result. Preselecting the target before the effect is what turns that into a
/// decidable question, and everything the two committed tuples do not describe stays
/// <see cref="Ambiguous"/> rather than being rounded toward the nearer of them.
/// </remarks>
internal enum GrimoireOfflineTransitionObservedState : byte
{

    /// <summary>The exact source generation and epoch tuple: the transaction did not commit.</summary>
    ExactlyNotApplied = 1,

    /// <summary>The exact preselected target generation and epoch tuple: this transition's own commit.</summary>
    ExactlyApplied = 2,

    /// <summary>Anything else. The transition stays closed and requires reconciliation.</summary>
    Ambiguous = 3,

}

/// <summary>
/// The one way a durable launch checkpoint becomes journal authority.
/// </summary>
/// <remarks>
/// A launch row and a journal file are the two halves of one offline transition, and this is the
/// only seam between them. Both halves are written before any effect and neither may be inferred
/// from the other afterwards, so everything here is a projection or a refusal — never a repair.
/// </remarks>
internal static class GrimoireOfflineTransitionLaunch
{

    private const string LaunchBindingDomain = "arcanum.grimoire.offline-transition.launch-binding.v1";

    /// <summary>Projects a version-4 Covenant offline-transition launch.</summary>
    internal static Result<GrimoireOfflineTransitionLaunchBinding> FromLaunch(
        CovenantOfflineTransitionLaunchV4 launch) =>
        CovenantRecoveryCheckpointCodec.IsLaunchable(launch)
            ? Project(
                launch.OperationId,
                launch.OperationKind,
                launch.RecoveryPolicy,
                launch.Operation,
                launch.EffectDigest,
                launch.SourceDatasetGeneration,
                launch.TargetDatasetGeneration,
                launch.SourceEpochs,
                launch.TargetEpochs,
                launch.StartingRevision)
            : Unlaunchable();

    /// <summary>Projects a version-2 healthy-catalog factory offline-transition launch.</summary>
    internal static Result<GrimoireOfflineTransitionLaunchBinding> FromLaunch(
        DataRetentionFactoryTransitionLaunchV2 launch) =>
        CovenantRecoveryCheckpointCodec.IsLaunchable(launch)
            ? Project(
                launch.OperationId,
                launch.OperationKind,
                launch.RecoveryPolicy,
                launch.Operation,
                launch.EffectDigest,
                launch.SourceDatasetGeneration,
                launch.TargetDatasetGeneration,
                launch.SourceEpochs,
                launch.TargetEpochs,
                launch.StartingRevision)
            : Unlaunchable();

    /// <summary>
    /// A legacy version-3 retention-mutation checkpoint is never a launch.
    /// </summary>
    /// <remarks>
    /// The legacy shapes record an owner and the phase they reached, and no target at all. Filling
    /// the missing target in — from the live database, from a plan, from a default — would authorize
    /// an offline transition against a generation nobody committed to replacing, and the evidence
    /// that it had happened would be gone by the time the family was already replaced. The refusal is
    /// unconditional rather than conditional on the payload being malformed: a perfectly valid legacy
    /// row still says nothing about a target, and a rule that only refused broken ones would be a
    /// rule about parsing rather than about authority.
    /// </remarks>
    internal static Result<GrimoireOfflineTransitionLaunchBinding> FromLegacy(
        DataRetentionMutationCheckpointV3 checkpoint) =>
        Unlaunchable();

    /// <summary>A legacy version-1 factory-erasure checkpoint is never a launch, for the same reason.</summary>
    internal static Result<GrimoireOfflineTransitionLaunchBinding> FromLegacy(
        DataRetentionFactoryResetCheckpointV1 checkpoint) =>
        Unlaunchable();

    /// <summary>
    /// Builds the journal binding this launch may be published under.
    /// </summary>
    /// <remarks>
    /// The launch digest is derived here rather than accepted from the caller, because it is what
    /// later proves the journal and the row are halves of the same transition. A caller that could
    /// supply it could publish a journal bound to a launch it is not.
    ///
    /// <para><paramref name="expectedDatabaseOperationRevision"/> cannot be derived and stays the
    /// caller's to read back: committing the launch checkpoint advances the row, and a lease renewal
    /// before the opening publication may advance it again, so only a reread immediately before
    /// publishing knows the value. What is enforced here is the one thing that is knowable — it must
    /// be past the revision the launch itself recorded, because an expected revision at or below that
    /// one names a row state from before the checkpoint existed.</para>
    /// </remarks>
    internal static Result<GrimoireOfflineTransitionBinding> JournalBinding(
        GrimoireOfflineTransitionLaunchBinding launch,
        ulong slotEpoch,
        byte payloadVersion,
        long expectedDatabaseOperationRevision,
        CovenantDigest? parentReceiptBindingDigest) =>
        launch is not null
        && slotEpoch != 0
        && payloadVersion != 0
        && expectedDatabaseOperationRevision > launch.StartingRevision
        && parentReceiptBindingDigest is not { IsValid: false }
            ? Result<GrimoireOfflineTransitionBinding>.Success(
                new GrimoireOfflineTransitionBinding(
                    launch.OperationId,
                    launch.Kind,
                    payloadVersion,
                    slotEpoch,
                    launch.EffectDigest,
                    launch.SourceDatasetGeneration,
                    launch.TargetDatasetGeneration,
                    launch.SourceEpochs,
                    launch.TargetEpochs,
                    launch.Digest,
                    checked((ulong)expectedDatabaseOperationRevision),
                    parentReceiptBindingDigest))
            : Result<GrimoireOfflineTransitionBinding>.Failure(Unresumable());

    /// <summary>
    /// Classifies an observed generation and epoch tuple against this launch's committed pair.
    /// </summary>
    /// <remarks>
    /// Both halves have to match together. A target generation carrying a source epoch, or a source
    /// generation carrying one advanced epoch, is the shape a transaction that partly committed or
    /// that somebody else ran would leave, and calling either of those "applied" would accept a
    /// database this transition never produced. A state past the target is ambiguous for the same
    /// reason rather than more-applied-than-expected.
    ///
    /// <para>An absent generation is ambiguous rather than a missing source. A zero generation is
    /// the value an uninitialized read produces, and treating it as "not applied" would authorize
    /// running the effect against a database nobody has established the state of.</para>
    /// </remarks>
    internal static GrimoireOfflineTransitionObservedState Classify(
        GrimoireOfflineTransitionLaunchBinding launch,
        Guid observedDatasetGeneration,
        GrimoireOfflineTransitionEpochTuple observedEpochs)
    {

        ArgumentNullException.ThrowIfNull(launch);

        if (observedDatasetGeneration == Guid.Empty || observedEpochs is null)
        {

            return GrimoireOfflineTransitionObservedState.Ambiguous;

        }

        if (observedDatasetGeneration == launch.SourceDatasetGeneration
            && observedEpochs == launch.SourceEpochs)
        {

            return GrimoireOfflineTransitionObservedState.ExactlyNotApplied;

        }

        return observedDatasetGeneration == launch.TargetDatasetGeneration
            && observedEpochs == launch.TargetEpochs
                ? GrimoireOfflineTransitionObservedState.ExactlyApplied
                : GrimoireOfflineTransitionObservedState.Ambiguous;

    }

    /// <summary>
    /// The domain-separated digest of one launch binding.
    /// </summary>
    /// <remarks>
    /// Built here rather than from the frozen Covenant digest manifest: that manifest's tags are
    /// policy identities pinned by a corpus, and a journal-local binding is neither policy nor
    /// public. The preimage follows the journal envelope's own idiom — an ASCII domain, a separator
    /// that cannot occur in it, big-endian fixed-width fields, and a length prefix on every text
    /// field so no value can borrow a character from its neighbour.
    /// </remarks>
    internal static CovenantDigest LaunchBindingDigest(GrimoireOfflineTransitionLaunchBinding binding)
    {

        ArgumentNullException.ThrowIfNull(binding);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(LaunchBindingDomain));

        hash.AppendData([0]);

        AppendGuid(hash, binding.OperationId);

        hash.AppendData([(byte)binding.Kind, (byte)binding.Operation]);

        AppendText(hash, binding.OperationKind);

        AppendText(hash, binding.RecoveryPolicy.ToString());

        hash.AppendData(binding.EffectDigest.Bytes);

        AppendGuid(hash, binding.SourceDatasetGeneration);

        AppendGuid(hash, binding.TargetDatasetGeneration);

        AppendEpochs(hash, binding.SourceEpochs);

        AppendEpochs(hash, binding.TargetEpochs);

        AppendUInt64(hash, unchecked((ulong)binding.StartingRevision));

        return new CovenantDigest(hash.GetHashAndReset());

    }

    private static Result<GrimoireOfflineTransitionLaunchBinding> Project(
        Guid operationId,
        string operationKind,
        string recoveryPolicy,
        CovenantExclusiveOperation operation,
        string effectDigest,
        Guid sourceDatasetGeneration,
        Guid targetDatasetGeneration,
        CovenantOfflineTransitionEpochsV1 sourceEpochs,
        CovenantOfflineTransitionEpochsV1 targetEpochs,
        long startingRevision) =>
        TryReadKind(operation, out GrimoireOfflineTransitionKind kind)
        && CovenantRecoveryCheckpointCodec.TryReadRecoveryPolicy(
            recoveryPolicy,
            operationKind,
            out LongRunningOperationRecoveryPolicy policy)
            ? Result<GrimoireOfflineTransitionLaunchBinding>.Success(
                new GrimoireOfflineTransitionLaunchBinding(
                    operationId,
                    operationKind,
                    kind,
                    policy,
                    operation,
                    new CovenantDigest(Convert.FromHexString(effectDigest)),
                    sourceDatasetGeneration,
                    targetDatasetGeneration,
                    Tuple(sourceEpochs),
                    Tuple(targetEpochs),
                    startingRevision))
            : Unlaunchable();

    /// <summary>
    /// The transition kind an exclusive operation launches, for the two that launch one.
    /// </summary>
    /// <remarks>
    /// Every other exclusive operation is refused rather than mapped. Journal possession alone never
    /// mints authority, and a default arm here would have handed a schema repair or a backup restore
    /// the closed-period powers of an erasure.
    /// </remarks>
    private static bool TryReadKind(
        CovenantExclusiveOperation operation,
        out GrimoireOfflineTransitionKind kind)
    {

        kind = operation switch
        {

            CovenantExclusiveOperation.CovenantReset => GrimoireOfflineTransitionKind.CovenantReset,

            CovenantExclusiveOperation.HealthyCatalogFactoryErasure =>
                GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,

            _ => default,

        };

        return kind != default;

    }

    private static GrimoireOfflineTransitionEpochTuple Tuple(CovenantOfflineTransitionEpochsV1 epochs) =>
        new(epochs.AcceleratorEpoch, epochs.KeyReclamationEpoch, epochs.EnvelopeKeyEpoch);

    private static void AppendGuid(IncrementalHash hash, Guid value) =>
        hash.AppendData(value.ToByteArray(bigEndian: true));

    private static void AppendText(IncrementalHash hash, string value)
    {

        byte[] encoded = Encoding.UTF8.GetBytes(value);

        byte[] length = new byte[sizeof(ushort)];

        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)encoded.Length));

        hash.AppendData(length);

        hash.AppendData(encoded);

    }

    private static void AppendEpochs(IncrementalHash hash, GrimoireOfflineTransitionEpochTuple epochs)
    {

        AppendUInt64(hash, epochs.AcceleratorEpoch);

        AppendUInt64(hash, epochs.KeyReclamationEpoch);

        AppendUInt64(hash, epochs.EnvelopeKeyEpoch);

    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {

        byte[] encoded = new byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(encoded, value);

        hash.AppendData(encoded);

    }

    private static Result<GrimoireOfflineTransitionLaunchBinding> Unlaunchable() =>
        Result<GrimoireOfflineTransitionLaunchBinding>.Failure(Unresumable());

    private static Error Unresumable() =>
        new(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "This durable launch cannot bind an offline transition in this build.");

}
