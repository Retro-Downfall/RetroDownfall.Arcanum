using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A turn-begin store that records what it was asked and returns whatever the test scripted.
/// </summary>
/// <remarks>
/// Exists so a writer test can assert the one property that used to be untestable: that a begin
/// failure reaches the caller as a failure instead of an empty handle (§10.12).
/// </remarks>
internal sealed class FakeSessionTurnBeginStore : ISessionTurnBeginStore
{

    /// <summary>Scripted begin outcome. Defaults to a fresh successful receipt.</summary>
    public Result<AssistantReplyBeginReceipt>? BeginResult { get; set; }

    /// <summary>Scripted creation outcome. Defaults to a fresh Session identity.</summary>
    public Result<Guid>? CreateResult { get; set; }

    public Guid? LastBeginSessionId { get; private set; }

    public CanonicalCampaignContext? LastCampaign { get; private set; }

    public int BeginCalls { get; private set; }

    public int CreateCalls { get; private set; }

    /// <summary>Thrown from begin, for the cancellation paths a Result cannot express.</summary>
    public Exception? BeginThrows { get; set; }

    public ValueTask<Result<Guid>> CreateBoundSessionAsync(
        CanonicalCampaignContext campaign,
        string title,
        CancellationToken cancellationToken)
    {

        CreateCalls++;

        LastCampaign = campaign;

        return ValueTask.FromResult(CreateResult ?? Result<Guid>.Success(Guid.NewGuid()));

    }

    public ValueTask<Result<AssistantReplyBeginReceipt>> BeginAssistantReplyAsync(
        Guid existingSessionId,
        CanonicalCampaignContext campaign,
        string prompt,
        string model,
        CancellationToken cancellationToken)
    {

        BeginCalls++;

        if (BeginThrows is { } failure)
        {
            throw failure;
        }

        LastBeginSessionId = existingSessionId;

        LastCampaign = campaign;

        return ValueTask.FromResult(
            BeginResult
            ?? Result<AssistantReplyBeginReceipt>.Success(
                new AssistantReplyBeginReceipt(
                    existingSessionId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    new SessionTurnInputPreflight(
                        existingSessionId,
                        campaign.Binding,
                        PreRequestHistoryRevision: 0,
                        TaintedArtifactCount: 0))));

    }

}
