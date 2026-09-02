using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The three persisted Covenant epochs, as one launch records them.
/// </summary>
/// <remarks>
/// A tuple rather than six loose fields, so a source and a target can only be compared as wholes.
/// It is deliberately not the journal's own epoch tuple: the journal file and the database row are
/// two durable surfaces with two compatibility promises, and one shape shared between them would
/// mean a change to either rewrote the other (§10.16).
/// </remarks>
public sealed record CovenantOfflineTransitionEpochsV1(
    ulong AcceleratorEpoch,
    ulong KeyReclamationEpoch,
    ulong EnvelopeKeyEpoch);

/// <summary>
/// The immutable launch binding of a Covenant reset run as an offline transition.
/// </summary>
/// <remarks>
/// V4 exists because V3 has nowhere to put a target. A reset's phase authority moves out of the
/// database and into the authenticated journal, which leaves the row holding exactly one thing the
/// journal cannot hold for it: the immutable statement of what was launched. Operation, kind,
/// recovery policy, canonical effect, the exact source generation and epoch tuple the plan was made
/// against, and the exact target tuple it preselected are all authority a later process would
/// otherwise have to infer, and inference is how a destructive plan nobody committed gets adopted.
///
/// <para>The target is preselected rather than generated inside the canonical transaction, because
/// that transaction stamps a new generation and advances all three epochs — so a recovery pass that
/// found the family already replaced could not otherwise tell its own commit from somebody else's.
/// The three target epochs are each the successor of their own source, which is what makes the
/// target computable before the transaction runs rather than discovered after it.</para>
///
/// <para><paramref name="RecoveryPolicy"/> travels as the policy's declared name rather than its
/// numeric code. The code is also a public operator-API value, governed by a different compatibility
/// promise, and a durable payload that borrowed it would let a wire decision rewrite recovery state.
/// <paramref name="OperationKind"/> is the durable ledger kind for the same reason.</para>
///
/// <para><paramref name="StartingRevision"/> is the row revision observed immediately before this
/// checkpoint was committed. It is history rather than a compare-exchange value: committing the
/// checkpoint advances the revision, so no payload can name the revision it will itself produce. The
/// journal carries the expected revision the terminal compare-exchange actually uses.</para>
/// </remarks>
public sealed record CovenantOfflineTransitionLaunchV4(
    int Version,
    Guid OperationId,
    string OperationKind,
    string RecoveryPolicy,
    CovenantExclusiveOperation Operation,
    string EffectDigest,
    Guid SourceDatasetGeneration,
    Guid TargetDatasetGeneration,
    CovenantOfflineTransitionEpochsV1 SourceEpochs,
    CovenantOfflineTransitionEpochsV1 TargetEpochs,
    long StartingRevision)
{

    /// <summary>The only Covenant launch version this build writes.</summary>
    public const int CurrentVersion = 4;

}

/// <summary>
/// The immutable launch binding of a healthy-catalog factory erasure run as an offline transition.
/// </summary>
/// <remarks>
/// The same fields as the Covenant launch, because the two transitions do the same thing to storage
/// and differ only in what they preserve. It is a separate shape for the same reason V1 was separate
/// from V3: the two are filed under different durable kinds, and one shape spanning both would be a
/// payload whose meaning depended on the row it happened to be read from.
///
/// <para>Strictness comes from the pinned triple — version, ledger kind, and exclusive operation —
/// rather than from the runtime type. Two payloads that differ only in a version number are two
/// different destructive plans, and the decoder refuses the wrong one on all three.</para>
/// </remarks>
public sealed record DataRetentionFactoryTransitionLaunchV2(
    int Version,
    Guid OperationId,
    string OperationKind,
    string RecoveryPolicy,
    CovenantExclusiveOperation Operation,
    string EffectDigest,
    Guid SourceDatasetGeneration,
    Guid TargetDatasetGeneration,
    CovenantOfflineTransitionEpochsV1 SourceEpochs,
    CovenantOfflineTransitionEpochsV1 TargetEpochs,
    long StartingRevision)
{

    /// <summary>The only factory launch version this build writes.</summary>
    public const int CurrentVersion = 2;

}

public static partial class CovenantRecoveryCheckpointCodec
{

    /// <summary>
    /// The largest epoch a launch may preselect a successor for.
    /// </summary>
    /// <remarks>
    /// The persisted columns are signed integers whose update trigger refuses both a decrease and any
    /// change at all once saturated. A launch that preselected a target beyond that ceiling would be
    /// refused by the database after the transition had already closed admission, which is the one
    /// moment there is no safe answer left.
    /// </remarks>
    private const ulong AdvanceableEpochCeiling = (ulong)long.MaxValue - 1;

    public static byte[] Encode(CovenantOfflineTransitionLaunchV4 launch) =>
        JsonSerializer.SerializeToUtf8Bytes(
            launch,
            CovenantRecoveryJsonContext.Default.CovenantOfflineTransitionLaunchV4);

    public static byte[] Encode(DataRetentionFactoryTransitionLaunchV2 launch) =>
        JsonSerializer.SerializeToUtf8Bytes(
            launch,
            CovenantRecoveryJsonContext.Default.DataRetentionFactoryTransitionLaunchV2);

    public static Result<CovenantOfflineTransitionLaunchV4> DecodeCovenantOfflineTransitionLaunch(
        ReadOnlySpan<byte> payload) =>
        Decode(
            payload,
            static bytes => JsonSerializer.Deserialize(
                bytes,
                CovenantRecoveryJsonContext.Default.CovenantOfflineTransitionLaunchV4),
            static launch => IsLaunchable(launch));

    public static Result<DataRetentionFactoryTransitionLaunchV2> DecodeDataRetentionFactoryTransitionLaunch(
        ReadOnlySpan<byte> payload) =>
        Decode(
            payload,
            static bytes => JsonSerializer.Deserialize(
                bytes,
                CovenantRecoveryJsonContext.Default.DataRetentionFactoryTransitionLaunchV2),
            static launch => IsLaunchable(launch));

    /// <summary>
    /// Whether this Covenant launch is one the codec would accept from durable storage.
    /// </summary>
    /// <remarks>
    /// Exposed so the journal-side projection admits exactly what recovery admits. Two rules about
    /// what a launch is would agree on the day they were written and diverge on the first change,
    /// and the half that disagreed would be the half authorizing a destructive plan.
    /// </remarks>
    internal static bool IsLaunchable(CovenantOfflineTransitionLaunchV4 launch) =>
        launch is not null
        && launch.Version == CovenantOfflineTransitionLaunchV4.CurrentVersion
        && IsLaunchable(
            launch.OperationId,
            launch.OperationKind,
            launch.RecoveryPolicy,
            launch.Operation,
            launch.EffectDigest,
            launch.SourceDatasetGeneration,
            launch.TargetDatasetGeneration,
            launch.SourceEpochs,
            launch.TargetEpochs,
            launch.StartingRevision,
            LongRunningOperationKinds.DataRetentionMutation,
            CovenantExclusiveOperation.CovenantReset);

    /// <summary>Whether this factory launch is one the codec would accept from durable storage.</summary>
    internal static bool IsLaunchable(DataRetentionFactoryTransitionLaunchV2 launch) =>
        launch is not null
        && launch.Version == DataRetentionFactoryTransitionLaunchV2.CurrentVersion
        && IsLaunchable(
            launch.OperationId,
            launch.OperationKind,
            launch.RecoveryPolicy,
            launch.Operation,
            launch.EffectDigest,
            launch.SourceDatasetGeneration,
            launch.TargetDatasetGeneration,
            launch.SourceEpochs,
            launch.TargetEpochs,
            launch.StartingRevision,
            LongRunningOperationKinds.DataRetentionFactoryReset,
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure);

    /// <summary>
    /// The recovery policy a launch names, when it names the one its kind is registered under.
    /// </summary>
    /// <remarks>
    /// Round-tripped rather than parsed, because <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/>
    /// also accepts the numeric code. A payload spelling the policy as <c>"2"</c> would then be
    /// admitted under a name it never wrote, which is precisely the coupling to the wire code this
    /// field exists to avoid.
    /// </remarks>
    internal static bool TryReadRecoveryPolicy(
        string? name,
        string operationKind,
        out LongRunningOperationRecoveryPolicy policy)
    {

        policy = default;

        return name is not null
            && Enum.TryParse(name, ignoreCase: false, out policy)
            && string.Equals(policy.ToString(), name, StringComparison.Ordinal)
            && LongRunningOperationPolicyCatalog.IsRegistered(operationKind, policy);

    }

    private static bool IsLaunchable(
        Guid operationId,
        string operationKind,
        string recoveryPolicy,
        CovenantExclusiveOperation operation,
        string effectDigest,
        Guid sourceDatasetGeneration,
        Guid targetDatasetGeneration,
        CovenantOfflineTransitionEpochsV1? sourceEpochs,
        CovenantOfflineTransitionEpochsV1? targetEpochs,
        long startingRevision,
        string expectedKind,
        CovenantExclusiveOperation expectedOperation) =>
        operationId != Guid.Empty
        && string.Equals(operationKind, expectedKind, StringComparison.Ordinal)
        && TryReadRecoveryPolicy(recoveryPolicy, expectedKind, out _)
        && operation == expectedOperation
        && IsCanonicalEffectDigest(effectDigest)
        && sourceDatasetGeneration != Guid.Empty
        && targetDatasetGeneration != Guid.Empty
        && sourceDatasetGeneration != targetDatasetGeneration
        && sourceEpochs is not null
        && targetEpochs is not null
        && IsPreselectedSuccessor(sourceEpochs.AcceleratorEpoch, targetEpochs.AcceleratorEpoch)
        && IsPreselectedSuccessor(sourceEpochs.KeyReclamationEpoch, targetEpochs.KeyReclamationEpoch)
        && IsPreselectedSuccessor(sourceEpochs.EnvelopeKeyEpoch, targetEpochs.EnvelopeKeyEpoch)
        && startingRevision >= 0;

    /// <summary>
    /// Each target epoch is the successor of its own source, and of no other.
    /// </summary>
    /// <remarks>
    /// Compared member by member rather than as a set, because the canonical transaction advances
    /// all three by one in the same statement: a launch whose accelerator and envelope targets were
    /// transposed would satisfy any rule that only asked whether every target was some source plus
    /// one, and would then verify a replaced family against epochs belonging to the wrong counter.
    /// </remarks>
    private static bool IsPreselectedSuccessor(ulong source, ulong target) =>
        source is > 0 and <= AdvanceableEpochCeiling && target == source + 1;

}
