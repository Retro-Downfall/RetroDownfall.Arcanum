using System.Text.Json;

using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Turns a settled <c>dispatch_sending</c> tool result into the Chronicle frames an operator sees.
/// </summary>
/// <remarks>
/// Separate from <c>ApprenticeService</c> because the mapping carries real decisions — which instants the
/// frames are stamped with, and how an unreported remote cost is represented — and those are worth
/// asserting on directly rather than through a whole Apprentice run.
/// </remarks>
internal static class SendingChronicleFrames
{

    private const string Outbound = "outbound";

    /// <summary>
    /// Builds the <c>sendingDispatched</c> frame plus its terminal partner. Returns nothing for a payload
    /// that cannot be parsed: Chronicle observability is best-effort and must never fail a step.
    /// </summary>
    internal static IReadOnlyList<ApprenticeEvent> Build(
        Guid apprenticeId,
        string resultText,
        DateTimeOffset fallbackNow)
    {

        DispatchSendingResultWire? payload;

        try
        {

            payload = JsonSerializer.Deserialize(
                resultText.Trim(),
                McpJsonSerializerContext.Default.DispatchSendingResultWire);

        }
        catch (JsonException)
        {

            return [];

        }

        if (payload is null)
        {

            return [];

        }

        // Distinct instants, not one shared `now`: collapsing them onto a single timestamp made remote
        // wall-clock underivable from the Chronicle, which is the only place it is ever recorded.
        DateTimeOffset dispatchedAt = payload.DispatchedAt ?? fallbackNow;

        DateTimeOffset settledAt = payload.SettledAt ?? fallbackNow;

        long? remoteDurationMs = settledAt >= dispatchedAt
            ? (long)(settledAt - dispatchedAt).TotalMilliseconds
            : null;

        ApprenticeEvent dispatched = new()
        {
            Type = ApprenticeEventType.SendingDispatched,
            ApprenticeId = apprenticeId,
            Timestamp = dispatchedAt,
            Description = payload.AgentUrl,
            Summary = payload.TaskId,
            SendingDirection = Outbound,
        };

        ApprenticeEvent terminal = payload.Succeeded
            ? new ApprenticeEvent
            {
                Type = ApprenticeEventType.SendingCompleted,
                ApprenticeId = apprenticeId,
                Timestamp = settledAt,
                Description = payload.AgentUrl,
                Summary = payload.TaskId,
                Result = payload.Response,
                DurationMs = remoteDurationMs,
                SendingDirection = Outbound,
                SendingState = payload.ContinuationTaskId is null
                    ? "completed"
                    : $"{payload.ContinuationNeed}-required",

                // Explicitly unknown when the peer reported nothing — never a silent zero (issue #60).
                RemoteCostKnown = payload.CostKnown,
                RemoteTotalTokens = payload.RemoteTotalTokens,
                RemoteCostUsd = payload.RemoteCostUsd,
            }
            : new ApprenticeEvent
            {
                Type = ApprenticeEventType.SendingFailed,
                ApprenticeId = apprenticeId,
                Timestamp = settledAt,
                Description = payload.AgentUrl,
                Summary = payload.TaskId,
                Error = payload.Error,
                DurationMs = remoteDurationMs,
                SendingDirection = Outbound,
                RemoteCostKnown = payload.CostKnown,
                RemoteTotalTokens = payload.RemoteTotalTokens,
                RemoteCostUsd = payload.RemoteCostUsd,
            };

        return [dispatched, terminal];

    }

}
