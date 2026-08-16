using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// Where a resumable accelerator rebuild has reached.
/// </summary>
/// <remarks>
/// <see cref="Completed"/> and <see cref="RestartRequired"/> are terminal. A restart carries the
/// stale captured identity on purpose: the operation that owns it needs to report what it was
/// rebuilding when the ground moved, and a progress record that quietly re-captured would hide the
/// discontinuity.
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantIndexRebuildPhase>))]
public enum CovenantIndexRebuildPhase : byte
{

    BaseScan = 1,

    DeltaCatchUp = 2,

    Verifying = 3,

    Completed = 4,

    RestartRequired = 5,

}

/// <summary>
/// One immutable checkpoint of a resumable rebuild.
/// </summary>
/// <remarks>
/// Every identity the rebuild captured travels with it. The rebuilder rechecks all of them inside the
/// same transaction as each batch, so a reset, an epoch change, or an outbox overflow between batches
/// discards the partial generation instead of publishing it.
///
/// <para><c>LastContiguousAppliedSequence</c> never makes FTS eligible on its own: eligibility is
/// published once, atomically, at the end of verification.</para>
/// </remarks>
public sealed record CovenantIndexRebuildProgress(
    Guid DatasetGeneration,
    ulong AcceleratorEpoch,
    long BaseTargetSearchSequence,
    long CapturedCoreCampaignDeletionSequence,
    CovenantIndexRebuildPhase Phase,
    long? BaseScanAfterSearchRowId,
    long LastContiguousAppliedSequence,
    long BaseHeadsProcessed,
    long? BaseHeadsTotal,
    long DeltaRowsProcessed)
{

    /// <summary>
    /// How many base heads one batch may commit.
    /// </summary>
    public const int BaseBatchHeads = 256;

    private readonly Guid _datasetGeneration = RequireGeneration(DatasetGeneration);

    private readonly ulong _acceleratorEpoch = RequirePositive(AcceleratorEpoch, nameof(AcceleratorEpoch));

    private readonly long _baseTargetSearchSequence =
        RequireNonNegative(BaseTargetSearchSequence, nameof(BaseTargetSearchSequence));

    private readonly long _capturedCoreCampaignDeletionSequence =
        RequireNonNegative(CapturedCoreCampaignDeletionSequence, nameof(CapturedCoreCampaignDeletionSequence));

    private readonly long? _baseScanAfterSearchRowId = RequireCursor(BaseScanAfterSearchRowId);

    private readonly long _lastContiguousAppliedSequence =
        RequireNonNegative(LastContiguousAppliedSequence, nameof(LastContiguousAppliedSequence));

    private readonly long _baseHeadsProcessed = RequireNonNegative(BaseHeadsProcessed, nameof(BaseHeadsProcessed));

    private readonly long? _baseHeadsTotal = RequireNonNegative(BaseHeadsTotal, nameof(BaseHeadsTotal));

    private readonly long _deltaRowsProcessed = RequireNonNegative(DeltaRowsProcessed, nameof(DeltaRowsProcessed));

    /// <remarks>
    /// The validation lives in the accessors rather than in a property initializer so that a
    /// <c>with</c> expression is checked too. An initializer runs only in the primary constructor,
    /// which would leave every derived checkpoint unvalidated.
    /// </remarks>
    public Guid DatasetGeneration
    {

        get => _datasetGeneration;

        init => _datasetGeneration = RequireGeneration(value);

    }

    public ulong AcceleratorEpoch
    {

        get => _acceleratorEpoch;

        init => _acceleratorEpoch = RequirePositive(value, nameof(AcceleratorEpoch));

    }

    public long BaseTargetSearchSequence
    {

        get => _baseTargetSearchSequence;

        init => _baseTargetSearchSequence = RequireNonNegative(value, nameof(BaseTargetSearchSequence));

    }

    public long CapturedCoreCampaignDeletionSequence
    {

        get => _capturedCoreCampaignDeletionSequence;

        init => _capturedCoreCampaignDeletionSequence =
            RequireNonNegative(value, nameof(CapturedCoreCampaignDeletionSequence));

    }

    /// <summary>
    /// The most recently committed stable projection row ID. Null is valid only before the first base
    /// row: zero would be indistinguishable from a real cursor, and row IDs start at one.
    /// </summary>
    public long? BaseScanAfterSearchRowId
    {

        get => _baseScanAfterSearchRowId;

        init => _baseScanAfterSearchRowId = RequireCursor(value);

    }

    public long LastContiguousAppliedSequence
    {

        get => _lastContiguousAppliedSequence;

        init => _lastContiguousAppliedSequence = RequireNonNegative(value, nameof(LastContiguousAppliedSequence));

    }

    public long BaseHeadsProcessed
    {

        get => _baseHeadsProcessed;

        init => _baseHeadsProcessed = RequireNonNegative(value, nameof(BaseHeadsProcessed));

    }

    public long? BaseHeadsTotal
    {

        get => _baseHeadsTotal;

        init => _baseHeadsTotal = RequireNonNegative(value, nameof(BaseHeadsTotal));

    }

    public long DeltaRowsProcessed
    {

        get => _deltaRowsProcessed;

        init => _deltaRowsProcessed = RequireNonNegative(value, nameof(DeltaRowsProcessed));

    }

    /// <summary>
    /// Whether this checkpoint can still be advanced.
    /// </summary>
    public bool IsTerminal =>
        Phase is CovenantIndexRebuildPhase.Completed or CovenantIndexRebuildPhase.RestartRequired;

    private static Guid RequireGeneration(Guid value) =>
        value != Guid.Empty
            ? value
            : throw new ArgumentException(
                "A rebuild checkpoint requires a nonempty dataset generation.",
                nameof(DatasetGeneration));

    private static ulong RequirePositive(ulong value, string parameterName) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(parameterName);

    private static long RequireNonNegative(long value, string parameterName) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(parameterName);

    private static long? RequireNonNegative(long? value, string parameterName) =>
        value is null or >= 0 ? value : throw new ArgumentOutOfRangeException(parameterName);

    private static long? RequireCursor(long? value) =>
        value is null or > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(BaseScanAfterSearchRowId));

}
