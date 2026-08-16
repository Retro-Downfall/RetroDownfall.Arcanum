using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// The durable outcome of one assistant placeholder, matching
/// <c>assistant_entry_finalizations.OutcomeCode</c>.
/// </summary>
/// <remarks>
/// A successful empty response is an ordinary <see cref="Committed"/> row. Empty content is never a
/// sentinel here: inferring "unfinished" from an empty string is how a valid empty answer used to
/// become a turn that looked like it had never run.
/// </remarks>
public enum AssistantFinalizationOutcome : byte
{
    Committed = 1,
    Discarded = 2,
    CommittedImported = 3,
    CommittedForked = 4,
}

/// <summary>
/// Everything one atomic turn publication needs, and nothing it has to re-derive.
/// </summary>
/// <remarks>
/// The assistant response and the staged Covenant batch commit together or not at all. Publishing a
/// profile change for a turn whose answer was never persisted would leave the agent "remembering"
/// something the operator never received an answer about, and publishing the answer without the
/// batch would silently lose an operator-visible acknowledgement the tool already reported (§10.13).
/// </remarks>
public sealed record TurnCommitRequest
{

    public TurnCommitRequest(
        Guid assistantEntryId,
        Guid sessionId,
        AssistantFinalizationOutcome outcome,
        string finalText,
        CovenantDigest requestDigest,
        ContentSensitivity contentSensitivity,
        GenerationProvenance contentProvenance,
        CovenantDigest? finalReceiptDigest = null,
        ImmutableArray<CovenantMutationIntent> mutations = default,
        CovenantMutationBatchBinding? mutationBinding = null,
        Guid? campaignId = null,
        Guid? turnId = null,
        CovenantDigest? producingPlanDigest = null,
        CovenantDigest? producingAdmissionDigest = null)
    {

        ArgumentNullException.ThrowIfNull(finalText);

        ArgumentNullException.ThrowIfNull(contentProvenance);

        AssistantEntryId = CovenantValidation.RequireNonEmpty(assistantEntryId, nameof(assistantEntryId));

        SessionId = CovenantValidation.RequireNonEmpty(sessionId, nameof(sessionId));

        Outcome = Enum.IsDefined(outcome)
            ? outcome
            : throw new ArgumentOutOfRangeException(nameof(outcome));

        FinalText = finalText;

        RequestDigest = CovenantValidation.RequireDigest(requestDigest, nameof(requestDigest));

        ContentSensitivity = contentSensitivity is ContentSensitivity.None
            or ContentSensitivity.CovenantDerived
            ? contentSensitivity
            : throw new ArgumentOutOfRangeException(nameof(contentSensitivity));

        ContentProvenance = contentProvenance;

        FinalReceiptDigest = finalReceiptDigest is { } digest
            ? CovenantValidation.RequireDigest(digest, nameof(finalReceiptDigest))
            : null;

        Mutations = mutations.IsDefault ? [] : mutations;

        MutationBinding = mutationBinding;

        if (!Mutations.IsEmpty && MutationBinding is null)
        {
            throw new ArgumentException(
                "A staged Covenant batch requires its dataset, key-epoch, and registry binding.",
                nameof(mutationBinding));
        }

        if (!Mutations.IsEmpty && Outcome is not AssistantFinalizationOutcome.Committed)
        {
            throw new ArgumentException(
                "Only a committed assistant response may publish staged Covenant mutations.",
                nameof(mutations));
        }

        CampaignId = campaignId == Guid.Empty
            ? throw new ArgumentException(
                "An optional Campaign identity cannot be empty when present.",
                nameof(campaignId))
            : campaignId;

        TurnId = turnId == Guid.Empty
            ? throw new ArgumentException(
                "An optional turn identity cannot be empty when present.",
                nameof(turnId))
            : turnId;

        if (producingPlanDigest.HasValue != producingAdmissionDigest.HasValue)
        {
            throw new ArgumentException(
                "A producing Covenant plan and its admission are recorded as a pair or not at all.",
                nameof(producingPlanDigest));
        }

        ProducingPlanDigest = producingPlanDigest;

        ProducingAdmissionDigest = producingAdmissionDigest;

    }

    public Guid AssistantEntryId { get; }

    public Guid SessionId { get; }

    public AssistantFinalizationOutcome Outcome { get; }

    public string FinalText { get; }

    /// <summary>The canonical request identity a replay has to match exactly.</summary>
    public CovenantDigest RequestDigest { get; }

    public ContentSensitivity ContentSensitivity { get; }

    public GenerationProvenance ContentProvenance { get; }

    public CovenantDigest? FinalReceiptDigest { get; }

    public ImmutableArray<CovenantMutationIntent> Mutations { get; }

    public CovenantMutationBatchBinding? MutationBinding { get; }

    /// <summary>The canonical Campaign this turn was bound to, for the label's owner index.</summary>
    public Guid? CampaignId { get; }

    /// <summary>The logical turn that produced the response, for the label's owner index.</summary>
    public Guid? TurnId { get; }

    /// <summary>The Covenant plan behind this response, when the call itself admitted Covenant.</summary>
    public CovenantDigest? ProducingPlanDigest { get; }

    /// <summary>The admission that froze that plan. Recorded only as a pair with it.</summary>
    public CovenantDigest? ProducingAdmissionDigest { get; }

    /// <summary>
    /// The information-flow label this finalization has to persist beside its content.
    /// </summary>
    /// <remarks>
    /// Built here rather than at the writer so there is exactly one derivation from a commit request
    /// to a label: the assistant entry is the highest-volume tainted artifact in the product, and two
    /// derivations would eventually disagree about which of them was authoritative (§10.12).
    /// </remarks>
    public DerivedArtifactWrite ToDerivedArtifactWrite() =>
        new(
            SensitiveArtifactKind.AssistantEntry,
            AssistantEntryId,
            SessionId,
            CampaignId,
            TurnId,
            artifactRevision: 1,
            DerivedArtifactContentDigest.ForText(FinalText),
            ContentSensitivity,
            ContentProvenance,
            ProducingPlanDigest,
            ProducingAdmissionDigest);

}

/// <summary>
/// The generation and epoch values a staged batch was planned under and must still hold at commit.
/// </summary>
public sealed record CovenantMutationBatchBinding(
    Guid DatasetGeneration,
    long ExpectedKeyReclamationEpoch,
    long ExpectedCampaignRegistryEpoch);

/// <summary>The durable answer one finalization attempt resolved to.</summary>
public sealed record TurnCommitReceipt(
    Guid AssistantEntryId,
    AssistantFinalizationOutcome Outcome,
    bool Replayed,
    ImmutableArray<CovenantMutationReceipt> MutationReceipts);

/// <summary>
/// The single writer of assistant finalization and Covenant publication.
/// </summary>
/// <remarks>
/// Every begin, finalize, and discard path routes through here. Ordinary, cancelled, and interrupted
/// finalization pass an empty batch and a terminal outcome; only an eligible successful completion
/// passes a sealed one. One implementation is what makes "the response and the profile change share
/// a fate" a property rather than an intention.
/// </remarks>
public interface IGrimoireTurnCommitter
{

    Task<Result<TurnCommitReceipt>> CommitTurnAsync(
        TurnCommitRequest request,
        CancellationToken cancellationToken);

}
