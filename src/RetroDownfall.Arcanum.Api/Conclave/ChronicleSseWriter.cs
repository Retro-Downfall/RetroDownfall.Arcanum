using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Api.Conclave;

[ExcludeFromCodeCoverage] // Reason: HTTP SSE streaming glue; exercised via apprentice chronicle integration routes.
internal static class ChronicleSseWriter
{

    public static async Task WriteEventAsync(
        HttpContext httpContext,
        ApprenticeEvent @event,
        CancellationToken cancellationToken)
    {

        ChronicleSseStreamWriter writer = new(httpContext);

        await writer.WriteEventAsync(@event, cancellationToken).ConfigureAwait(false);

    }

    internal static void WritePassThroughEvent(
        Utf8JsonWriter writer,
        ApprenticeEvent envelope,
        IntelligenceEvent wizard)
    {
        writer.WriteStartObject();

        WriteWireType(writer, envelope.Type);

        writer.WriteString("apprenticeId", envelope.ApprenticeId.ToString());

        writer.WriteString("message", wizard.Message);

        if (wizard.Data is not null)
        {
            writer.WriteString("data", wizard.Data);
        }

        if (wizard.Usage is not null)
        {
            writer.WritePropertyName("usage");

            JsonSerializer.Serialize(writer, wizard.Usage, ArcanumJsonContext.Default.ChatCompletionUsage);
        }

        if (wizard.ToolCall is not null)
        {
            writer.WritePropertyName("toolCall");

            JsonSerializer.Serialize(writer, wizard.ToolCall, ArcanumJsonContext.Default.IntelligenceToolCallEvent);
        }

        if (wizard.WardId is not null)
        {
            writer.WriteString("wardId", wizard.WardId);
        }

        if (wizard.WardToolName is not null)
        {
            writer.WriteString("toolName", wizard.WardToolName);
        }

        if (wizard.WardArguments is { } wardArgs)
        {
            writer.WritePropertyName("arguments");

            wardArgs.WriteTo(writer);
        }

        if (wizard.WardAllowed is { } allowed)
        {
            writer.WriteBoolean("allowed", allowed);
        }

        if (wizard.WardReason is not null)
        {
            writer.WriteString("reason", wizard.WardReason);
        }

        writer.WriteString("timestamp", (wizard.Timestamp ?? envelope.Timestamp).ToString("O"));

        writer.WriteEndObject();
    }

    internal static void WriteLifecycleEvent(Utf8JsonWriter writer, ApprenticeEvent @event)
    {
        writer.WriteStartObject();

        WriteWireType(writer, @event.Type);

        writer.WriteString("apprenticeId", @event.ApprenticeId.ToString());

        writer.WriteString("timestamp", @event.Timestamp.ToString("O"));

        if (@event.Name is not null)
        {
            writer.WriteString("name", @event.Name);
        }

        if (@event.Goal is not null)
        {
            writer.WriteString("goal", @event.Goal);
        }

        if (@event.Plan is not null)
        {
            writer.WritePropertyName("plan");

            writer.WriteStartArray();

            foreach (PlanStep step in @event.Plan)
            {

                JsonSerializer.Serialize(writer, step, ArcanumJsonContext.Default.PlanStep);

            }

            writer.WriteEndArray();
        }

        if (@event.StepIndex is { } stepIndex)
        {
            writer.WriteNumber("stepIndex", stepIndex);
        }

        if (@event.Description is not null)
        {
            writer.WriteString("description", @event.Description);
        }

        if (@event.Result is not null)
        {
            writer.WriteString("result", @event.Result);
        }

        if (@event.DurationMs is { } durationMs)
        {
            writer.WriteNumber("durationMs", durationMs);
        }

        if (@event.Error is not null)
        {
            writer.WriteString("error", @event.Error);
        }

        if (@event.Summary is not null)
        {
            writer.WriteString("summary", @event.Summary);
        }

        if (@event.TotalDurationMs is { } totalDurationMs)
        {
            writer.WriteNumber("totalDurationMs", totalDurationMs);
        }

        if (@event.AtStep is { } atStep)
        {
            writer.WriteNumber("atStep", atStep);
        }

        if (@event.FromStep is { } fromStep)
        {
            writer.WriteNumber("fromStep", fromStep);
        }

        if (@event.Attempt is { } attempt)
        {
            writer.WriteNumber("attempt", attempt);
        }

        if (@event.BackoffMs is { } backoffMs)
        {
            writer.WriteNumber("backoffMs", backoffMs);
        }

        if (@event.SendingState is not null)
        {
            writer.WriteString("sendingState", @event.SendingState);
        }

        if (@event.SendingDirection is not null)
        {
            writer.WriteString("sendingDirection", @event.SendingDirection);
        }

        if (@event.RemoteCostKnown is { } remoteCostKnown)
        {
            writer.WriteBoolean("remoteCostKnown", remoteCostKnown);
        }

        if (@event.RemoteTotalTokens is { } remoteTotalTokens)
        {
            writer.WriteNumber("remoteTotalTokens", remoteTotalTokens);
        }

        if (@event.RemoteCostUsd is { } remoteCostUsd)
        {
            writer.WriteNumber("remoteCostUsd", remoteCostUsd);
        }

        writer.WriteEndObject();
    }

    private static void WriteWireType(Utf8JsonWriter writer, ApprenticeEventType type)
    {
        writer.WriteString("type", WireTypeName(type));
    }

    private static string WireTypeName(ApprenticeEventType type) => type switch
    {
        ApprenticeEventType.ApprenticeStarted => "apprenticeStarted",
        ApprenticeEventType.PlanGenerated => "planGenerated",
        ApprenticeEventType.StepStarted => "stepStarted",
        ApprenticeEventType.StepCompleted => "stepCompleted",
        ApprenticeEventType.StepFailed => "stepFailed",
        ApprenticeEventType.ToolCall => "toolCall",
        ApprenticeEventType.ToolResult => "toolResult",
        ApprenticeEventType.Warded => "warded",
        ApprenticeEventType.WardResolved => "wardResolved",
        ApprenticeEventType.ApprenticePaused => "apprenticePaused",
        ApprenticeEventType.ApprenticeResumed => "apprenticeResumed",
        ApprenticeEventType.ApprenticeCompleted => "apprenticeCompleted",
        ApprenticeEventType.ApprenticeFailed => "apprenticeFailed",
        ApprenticeEventType.ApprenticeCancelled => "apprenticeCancelled",
        ApprenticeEventType.StepRetrying => "stepRetrying",
        ApprenticeEventType.PlanRevised => "planRevised",
        ApprenticeEventType.ApprenticeEscalated => "apprenticeEscalated",
        ApprenticeEventType.ApprenticeIntervened => "apprenticeIntervened",
        ApprenticeEventType.EventsDropped => "eventsDropped",
        ApprenticeEventType.SendingDispatched => "sendingDispatched",
        ApprenticeEventType.SendingProgress => "sendingProgress",
        ApprenticeEventType.SendingCompleted => "sendingCompleted",
        ApprenticeEventType.SendingFailed => "sendingFailed",
        // NOTE: CastSent/SimulacrumStarted/SimulacrumCompleted intentionally fall through to
        // type.ToString() (PascalCase on the wire) today, inconsistent with every other camelCase
        // event type above. Left as-is here to avoid an unrelated wire-format break for existing
        // Chronicle consumers (the CLI chronicle parser reads these exact strings); fix in a
        // follow-up PR that updates this switch and the CLI parser together.
        _ => type.ToString(),
    };

}
