using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Every per-turn ambient a tool call reads, captured once by the turn and re-established before each
/// tool call.
/// </summary>
/// <remarks>
/// <c>RunInferenceAttemptAsync</c> is an async iterator. An <c>AsyncLocal</c> written in one
/// <c>MoveNextAsync</c> segment is rolled back when that segment returns at a <c>yield return</c>, and a
/// tool call always runs in the segment after its own ToolCall frame, so anything the turn published
/// before that frame reads null inside the tool. The turn therefore keeps the objects here and
/// <see cref="Apply"/> publishes them all, from a plain non-yielding method, immediately before each
/// tool call.
///
/// <para>One set rather than one re-set per ambient because the ambients depend on each other. The
/// session id switches on attachment post-processing, which the ledger and the inject-once tracker
/// bound; publishing the id without them turns those bounds from unreachable into absent, since both
/// answer "go ahead" when nothing is ambient. <see cref="Apply"/> therefore publishes the session id
/// only together with both.</para>
///
/// <para>The turn's own bookkeeping — advancing the provider round, the end-of-turn provenance
/// snapshot, and ending the turn — goes through the captured objects, never through an ambient read,
/// which would see whatever the segment running it happens to hold.</para>
/// </remarks>
internal sealed class TurnAmbientSet
{
    /// <summary>The Session the tool loop binds attachment tools and server-side tool calls to.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>The turn's ledger, provider round, and attachment promotion state.</summary>
    public ContextMaterializationLedgerAmbient.Turn? MaterializationTurn { get; set; }

    /// <summary>The turn's inject-once attachment tracker.</summary>
    public SessionAttachmentTurnBudget.Turn? AttachmentBudgetTurn { get; set; }

    /// <summary>The accounting nested billable work ledgers against, and its writer.</summary>
    public TurnAccountingHandle? Accounting { get; private set; }

    public ITurnRunWriter? AccountingWriter { get; private set; }

    private IDisposable? _covenantStagingPush;

    /// <summary>The staging material of the latest admitted provider dispatch, when the turn may stage.</summary>
    public CovenantToolStagingContext? CovenantStaging { get; private set; }

    /// <summary>The attended turn's one live human-prompt emitter.</summary>
    public IHumanPromptLiveEmitter? HumanPromptEmitter { get; set; }

    /// <summary>The turn's attachment promotion state, for its end-of-turn provenance handoff.</summary>
    public AttachmentMemoryGateAmbient.TurnScope? AttachmentMemory => MaterializationTurn?.AttachmentMemory;

    public void SetAccounting(TurnAccountingHandle accounting, ITurnRunWriter? writer)
    {
        ArgumentNullException.ThrowIfNull(accounting);

        Accounting = accounting;

        AccountingWriter = writer;
    }

    /// <summary>
    /// Makes <paramref name="context"/> the turn's Covenant staging material for this provider round,
    /// or clears it when the round's dispatch earned no admission receipt.
    /// </summary>
    /// <remarks>
    /// Pushed for the provider call in the calling segment, and captured for the tool calls that run
    /// after the round's ToolCall frames, where the push is already gone. Clearing matters as much as
    /// setting: the captured set outlives the round, so without it a previous round's receipt would
    /// ride into this round's tool calls and authorize staging against a dispatch it does not
    /// describe.
    /// </remarks>
    public void StageCovenantRound(CovenantToolStagingContext? context)
    {
        _covenantStagingPush?.Dispose();

        _covenantStagingPush = context is null ? null : CovenantToolStagingAmbient.Push(context);

        CovenantStaging = context;
    }

    /// <summary>Undoes the turn's last staging push when the turn ends.</summary>
    public void EndCovenantStaging()
    {
        _covenantStagingPush?.Dispose();

        _covenantStagingPush = null;
    }

    public void SetProviderRound(int providerRound)
    {
        if (MaterializationTurn is { } turn)
        {
            turn.ProviderRound = providerRound;
        }
    }

    /// <summary>
    /// Publishes every captured ambient to the current flow.
    /// </summary>
    /// <remarks>
    /// Call only from a method that does not itself <c>yield return</c>, with no <c>yield return</c>
    /// between this call and the tool invocation it serves. A plain call and an ordinary <c>await</c>
    /// both carry <c>AsyncLocal</c> state forward; only an iterator's <c>yield return</c> discards it.
    /// The writes stay inside the calling async method's flow and do not leak back into the iterator.
    /// </remarks>
    public void Apply()
    {
        bool attachmentBoundsCaptured = MaterializationTurn is not null && AttachmentBudgetTurn is not null;

        SessionAttachmentToolAmbient.CurrentSessionId = attachmentBoundsCaptured ? SessionId : null;

        ContextMaterializationLedgerAmbient.Enter(MaterializationTurn);

        SessionAttachmentTurnBudget.Enter(AttachmentBudgetTurn);

        if (Accounting is { } accounting)
        {
            TurnAccountingAmbient.Publish(accounting, AccountingWriter);
        }

        CovenantToolStagingAmbient.Current = CovenantStaging;

        if (HumanPromptEmitter is { } emitter)
        {
            HumanPromptLiveEmitterAmbient.Current = emitter;
        }
    }
}
