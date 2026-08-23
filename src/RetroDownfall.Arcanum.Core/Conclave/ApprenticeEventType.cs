using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Conclave;

[JsonConverter(typeof(JsonStringEnumConverter<ApprenticeEventType>))]
public enum ApprenticeEventType
{

    [JsonStringEnumMemberName("apprenticeStarted")]
    ApprenticeStarted,

    [JsonStringEnumMemberName("planGenerated")]
    PlanGenerated,

    [JsonStringEnumMemberName("stepStarted")]
    StepStarted,

    [JsonStringEnumMemberName("stepCompleted")]
    StepCompleted,

    [JsonStringEnumMemberName("stepFailed")]
    StepFailed,

    [JsonStringEnumMemberName("toolCall")]
    ToolCall,

    [JsonStringEnumMemberName("toolResult")]
    ToolResult,

    [JsonStringEnumMemberName("warded")]
    Warded,

    [JsonStringEnumMemberName("wardResolved")]
    WardResolved,

    [JsonStringEnumMemberName("apprenticePaused")]
    ApprenticePaused,

    [JsonStringEnumMemberName("apprenticeResumed")]
    ApprenticeResumed,

    [JsonStringEnumMemberName("apprenticeCompleted")]
    ApprenticeCompleted,

    [JsonStringEnumMemberName("apprenticeFailed")]
    ApprenticeFailed,

    [JsonStringEnumMemberName("apprenticeCancelled")]
    ApprenticeCancelled,

    [JsonStringEnumMemberName("stepRetrying")]
    StepRetrying,

    [JsonStringEnumMemberName("planRevised")]
    PlanRevised,

    [JsonStringEnumMemberName("apprenticeEscalated")]
    ApprenticeEscalated,

    [JsonStringEnumMemberName("apprenticeIntervened")]
    ApprenticeIntervened,

    [JsonStringEnumMemberName("castSent")]
    CastSent,

    [JsonStringEnumMemberName("simulacrumStarted")]
    SimulacrumStarted,

    [JsonStringEnumMemberName("simulacrumCompleted")]
    SimulacrumCompleted,

    [JsonStringEnumMemberName("eventsDropped")]
    EventsDropped,

    /// <summary>
    /// The Archmage Client (<c>dispatch_sending</c>) created an A2A task on a remote agent. Carries the
    /// remote agent URL and A2A task id in <see cref="ApprenticeEvent.Summary"/>/<see cref="ApprenticeEvent.Result"/>.
    /// </summary>
    [JsonStringEnumMemberName("sendingDispatched")]
    SendingDispatched,

    /// <summary>A status update was received from a remote A2A agent for an in-flight delegated Sending.</summary>
    [JsonStringEnumMemberName("sendingProgress")]
    SendingProgress,

    /// <summary>A delegated Sending completed successfully on the remote A2A agent.</summary>
    [JsonStringEnumMemberName("sendingCompleted")]
    SendingCompleted,

    /// <summary>A delegated Sending failed, timed out, or was rejected by the remote A2A agent.</summary>
    [JsonStringEnumMemberName("sendingFailed")]
    SendingFailed,

}
