using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// The content-free facts a turn was planned against, echoed back from the begin transaction.
/// </summary>
/// <remarks>
/// Deliberately carries no title, summary, Entry, attachment, tool, or label content. Its job is to let
/// a later commit prove that nothing moved underneath the turn, and a preflight that carried content
/// would be a second copy of history for a consumer to accidentally reason about (§10.12).
///
/// <para><see cref="PreRequestHistoryRevision"/> is the watermark the turn froze. Inserting the user
/// entry and the assistant placeholder advances the live revision immediately, so the echoed value is
/// the only remaining record of what the turn actually planned against.</para>
/// </remarks>
public sealed record SessionTurnInputPreflight(
    Guid SessionId,
    SessionCampaignBinding Binding,
    long PreRequestHistoryRevision,
    int TaintedArtifactCount);

/// <summary>
/// What one successful assistant-begin produced.
/// </summary>
/// <remarks>
/// Every identity is required. The old contract returned a tuple whose Session ID might name a Session
/// the caller never asked for, because begin was also allowed to create one. Separating creation from
/// begin is what makes each of these an identity the caller already knew about.
/// </remarks>
public sealed record AssistantReplyBeginReceipt(
    Guid SessionId,
    Guid UserEntryId,
    Guid AssistantEntryId,
    SessionTurnInputPreflight Preflight);

/// <summary>
/// The narrow port that appends one turn's user entry and assistant placeholder.
/// </summary>
/// <remarks>
/// Separate from the broad legacy repository interface on purpose. Adding these methods there would
/// give every unrelated repository fake a mandatory Campaign-binding contract to satisfy, and the
/// resulting churn is exactly how a fake ends up implementing the check as "return success".
///
/// <para>The port cannot create a Session. Campaign resolution happens before begin, and a begin that
/// could also create a Session would be a second place where a Campaign binding gets decided — with no
/// resolver in front of it. A caller that needs a new Session uses
/// <see cref="CreateBoundSessionAsync"/>, which takes the canonical context explicitly.</para>
/// </remarks>
public interface ISessionTurnBeginStore
{

    /// <summary>
    /// Creates one Session and its immutable Campaign binding atomically.
    /// </summary>
    ValueTask<Result<Guid>> CreateBoundSessionAsync(
        CanonicalCampaignContext campaign,
        string title,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends the user entry and assistant placeholder to an existing, correctly bound Session.
    /// </summary>
    /// <remarks>
    /// Rechecks Campaign existence and the immutable Session binding inside the same transaction that
    /// inserts the rows. Resolution happened earlier, and a Campaign deleted in between must abort with
    /// zero Entry side effects rather than leaving a placeholder bound to something that is gone.
    /// </remarks>
    ValueTask<Result<AssistantReplyBeginReceipt>> BeginAssistantReplyAsync(
        Guid existingSessionId,
        CanonicalCampaignContext campaign,
        string prompt,
        string model,
        CancellationToken cancellationToken);

}
