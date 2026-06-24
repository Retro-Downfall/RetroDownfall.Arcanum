using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.TheForge;

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

}
