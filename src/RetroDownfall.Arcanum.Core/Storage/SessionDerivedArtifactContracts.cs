using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// One replacement of a Session's mutable summary, with the sensitivity of the new value.
/// </summary>
/// <remarks>
/// The Loremaster summarizes tainted history, so its output is tainted whether or not it visibly
/// quotes a Covenant fragment. Sensitivity is therefore stated by the caller that knows what went
/// into the summary, not inferred from the text that came out: no substring detector can decide this
/// and none is consulted (§10.12).
/// </remarks>
public sealed record SessionSummaryArtifactWrite(
    Guid SessionId,
    string? Summary,
    DateTimeOffset? SummarizedThroughUtc,
    ContentSensitivity Sensitivity,
    GenerationProvenance Provenance,
    Guid? CampaignId = null,
    Guid? TurnId = null,
    CovenantDigest? ProducingPlanDigest = null,
    CovenantDigest? ProducingAdmissionDigest = null,
    CovenantDigest? ProducingMaintenanceReceiptDigest = null);

/// <summary>One replacement of a Session's mutable title, with the sensitivity of the new value.</summary>
/// <remarks>
/// A model-generated title propagates taint for the same reason a summary does. A clean
/// operator-authored replacement is the one path that removes a prior tainted label, and it does so
/// only in the transaction that overwrites the title it described.
/// </remarks>
public sealed record SessionTitleArtifactWrite(
    Guid SessionId,
    string? Title,
    ContentSensitivity Sensitivity,
    GenerationProvenance Provenance,
    Guid? CampaignId = null,
    Guid? TurnId = null,
    CovenantDigest? ProducingPlanDigest = null,
    CovenantDigest? ProducingAdmissionDigest = null,
    CovenantDigest? ProducingMaintenanceReceiptDigest = null);

/// <summary>The immutable identity and revision one replacement produced.</summary>
public sealed record SessionDerivedArtifactWriteReceipt(
    Guid ArtifactId,
    long Revision,
    ContentSensitivity Sensitivity,
    Guid? LabelId);

/// <summary>
/// The only writer of <c>Sessions.Summary</c>.
/// </summary>
/// <remarks>
/// Direct column updates are what this interface exists to replace. A caller that wrote the column
/// alone would leave the immutable artifact, the current pointer, and the sensitivity label
/// describing bytes that are no longer there, and every protected read downstream would trust that
/// stale evidence.
/// </remarks>
public interface ISessionSummaryArtifactStore
{

    Task<Result<SessionDerivedArtifactWriteReceipt>> ReplaceAsync(
        SessionSummaryArtifactWrite request,
        CancellationToken cancellationToken);

}

/// <summary>The only writer of <c>Sessions.Title</c>, under the same contract.</summary>
public interface ISessionTitleArtifactStore
{

    Task<Result<SessionDerivedArtifactWriteReceipt>> ReplaceAsync(
        SessionTitleArtifactWrite request,
        CancellationToken cancellationToken);

}
