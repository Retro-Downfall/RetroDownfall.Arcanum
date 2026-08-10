using System.Globalization;
using System.Text.Json;
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

    private bool _reasoningEmitted;

    public IntelligenceEventProjection(ChannelWriter<IntelligenceEvent> writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public async ValueTask ApplyAsync(TurnEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        IEnumerable<IntelligenceEvent> frames = evt is RunCompleted completed && _reasoningEmitted
            ? MapCompleted(completed, includeReasoning: false)
            : Map(evt);

        foreach (IntelligenceEvent frame in frames)
        {
            await _writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        if (evt is ReasoningDelta
            || evt is RunCompleted { Reasoning.Count: > 0 } && !_reasoningEmitted)
        {
            _reasoningEmitted = true;
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

            ContextAccounted accounted =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.Context,
                    "Context token accounting",
                    ContextBreakdown: accounted.Breakdown),
            ],

            TextDelta delta =>
            [
                new IntelligenceEvent(IntelligenceEventType.Token, delta.Text, delta.Text),
            ],

            ReasoningDelta delta =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.Reasoning,
                    delta.Reasoning.Text,
                    Reasoning: delta.Reasoning),
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
                        proposed.ArgumentsJson,
                        PreserveProviderCallId: proposed.PreserveProviderCallId)),
            ],

            ApprovalRequested approval =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.Warded,
                    approval.ToolName,
                    WardId: approval.WardId,
                    WardToolName: approval.ToolName,
                    WardArguments: TryReadWardArguments(approval.ArgumentsJson),
                    Timestamp: approval.Correlation.Timestamp,
                    WardOrigin: approval.Origin),
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
                    Timestamp: resolved.Correlation.Timestamp,
                    WardOrigin: resolved.Origin),
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
                    completed.ArgumentsJson)),
                new IntelligenceEvent(
                    IntelligenceEventType.ToolResult,
                    completed.ToolName,
                    completed.ResultText,
                    ToolCall: new IntelligenceToolCallEvent(
                        completed.CallId,
                        completed.ToolName,
                        completed.ArgumentsJson),
                    ToolDenied: completed.Denied),
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
                        completed.ArgumentsJson),
                    ToolDenied: completed.Denied),
            ],

            AttachmentRefreshed refreshed =>
            [
                new IntelligenceEvent(
                    IntelligenceEventType.AttachmentRefreshed,
                    "Session attachment source refreshed",
                    AttachmentRefresh: refreshed.Detail),
            ],

            RunCompleted completed => MapCompleted(completed, includeReasoning: true),

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

    /// <summary>
    /// Re-materializes the tool arguments the Ward gate is asking the operator to approve.
    /// <see cref="ApprovalRequested"/> flattens them to text on the way through the streaming
    /// mapper, and a <c>warded</c> frame without them leaves the operator consenting on a tool name
    /// alone — with the <c>_arcanumRiskDisclosure</c> DESIGN §11.14 requires silently suppressed.
    /// Empty or unparsable text is omitted rather than fatal: an unreadable argument document must
    /// not take down a turn that is already waiting on a human.
    /// </summary>
    private static JsonElement? TryReadWardArguments(string? argumentsJson)
    {

        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {

            using JsonDocument document = JsonDocument.Parse(argumentsJson);

            return document.RootElement.Clone();

        }
        catch (JsonException)
        {

            return null;

        }

    }

    private static IEnumerable<IntelligenceEvent> MapCompleted(
        RunCompleted completed,
        bool includeReasoning)
    {
        if (includeReasoning)
        {
            foreach (Core.Intelligence.ReasoningContentSegment reasoning in completed.Reasoning)
            {
                yield return new IntelligenceEvent(
                    IntelligenceEventType.Reasoning,
                    reasoning.Text,
                    Reasoning: reasoning);
            }
        }

        yield return new IntelligenceEvent(
            IntelligenceEventType.Result,
            completed.FinalText,
            completed.Usage?.TotalTokens.ToString(CultureInfo.InvariantCulture) ?? "0",
            completed.Usage,
            FinishReason: completed.FinishReason)
        {
            Warnings = completed.Warnings,
        };
    }

}
