using System.Threading.Channels;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;

/// <summary>
/// Projects semantic <see cref="TurnEvent"/>s into NDJSON <see cref="IntelligenceEvent"/> frames.
/// Writes typed output into a transport-facing channel — does not serialize HTTP.
/// </summary>
internal sealed class IntelligenceEventProjection
{

    private readonly ChannelWriter<IntelligenceEvent> _writer;

    public IntelligenceEventProjection(ChannelWriter<IntelligenceEvent> writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public async ValueTask ApplyAsync(TurnEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        foreach (IntelligenceEvent frame in Map(evt))
        {
            await _writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        if (evt.IsTerminal)
        {
            _ = _writer.TryComplete();
        }
    }

    internal static IEnumerable<IntelligenceEvent> Map(TurnEvent evt) =>
        evt switch
        {
            TurnStatusChanged status =>
            [
                new IntelligenceEvent(IntelligenceEventType.Status, status.Message),
            ],

            SessionBound bound =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.SessionBound,
                    "Session started",
                    bound.SessionId.ToString("D")),
                new IntelligenceEvent(
                    IntelligenceEventType.ConversationBound,
                    "Conversation started",
                    bound.SessionId.ToString("D")),
            ],

            ContextCompressed compressed =>
            [
                new IntelligenceEvent(IntelligenceEventType.Status, compressed.Message),
            ],

            TextDelta delta =>
            [
                new IntelligenceEvent(IntelligenceEventType.Token, delta.Text, delta.Text),
            ],

            ToolCallProposed proposed =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.ToolCall,
                    proposed.ToolName,
                    proposed.ArgumentsJson,
                    ToolCall: new IntelligenceToolCallEvent(
                        proposed.CallId,
                        proposed.ToolName,
                        proposed.ArgumentsJson)),
            ],

            ApprovalRequested approval =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.Warded,
                    approval.ToolName,
                    WardId: approval.WardId,
                    WardToolName: approval.ToolName,
                    WardArguments: null,
                    Timestamp: approval.Correlation.Timestamp),
            ],

            ApprovalResolved resolved =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.WardResolved,
                    resolved.ToolName,
                    WardId: resolved.WardId,
                    WardToolName: resolved.ToolName,
                    WardAllowed: resolved.Allowed,
                    WardReason: resolved.Reason,
                    Timestamp: resolved.Correlation.Timestamp),
            ],

            HumanInputRequested human =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.ToolCall,
                    "ask_human",
                    human.Prompt,
                    ToolCall: new IntelligenceToolCallEvent(human.CallId, "ask_human", human.Prompt)),
            ],

            ToolInvocationCompleted completed when completed.Failed =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.ToolError,
                    completed.ToolName,
                    completed.PublicErrorText
                        ?? "Tool invocation failed and was tolerated; a synthetic error result was returned to the model.",
                    ToolCall: new IntelligenceToolCallEvent(
                        completed.CallId,
                        completed.ToolName,
                        completed.ResultText)),
                new IntelligenceEvent(
                    IntelligenceEventType.ToolResult,
                    completed.ToolName,
                    completed.ResultText,
                    ToolCall: new IntelligenceToolCallEvent(
                        completed.CallId,
                        completed.ToolName,
                        completed.ResultText)),
            ],

            ToolInvocationCompleted completed =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.ToolResult,
                    completed.ToolName,
                    completed.ResultText,
                    ToolCall: new IntelligenceToolCallEvent(
                        completed.CallId,
                        completed.ToolName,
                        completed.ResultText)),
            ],

            RunCompleted completed =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.Result,
                    completed.FinalText,
                    completed.FinalText,
                    completed.Usage,
                    FinishReason: completed.FinishReason)
                {
                    Warnings = completed.Warnings,
                },
            ],

            RunFailed failed =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.Error,
                    failed.Error.Message,
                    failed.Error.Code),
            ],

            RunAbandoned abandoned =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.Error,
                    abandoned.Error?.Message ?? "Turn abandoned.",
                    abandoned.Error?.Code ?? ErrorCodes.Hub.Error),
            ],

            _ => [],
        };

}
