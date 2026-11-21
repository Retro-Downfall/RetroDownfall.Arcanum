using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>What the owning boundary observed about the failed effect. Contains no provider text or arguments.</summary>
public sealed record TurnFailureEvidence(TurnFailurePhase Phase, TurnEffectState Effect, TurnRecoveryAction NextAction,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ToolIndex = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ToolRound = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolName = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TurnFailureOutput? Output { get; init; }

    public static TurnFailureEvidence? FromToolOutcome(ToolOutcome? outcome, string name, int round, int index)
    {
        // A native read or listing has one lookup. An external server cannot supply these native
        // codes. A generic tool failure alone cannot prove whether a mutation took effect.
        if (name is not ("read_file" or "list_directory") || outcome is null) return null;

        return outcome.Code switch
        {
            ToolOutcomeCode.WorkspacePolicyDenied or ToolOutcomeCode.ProtectedPathDenied or ToolOutcomeCode.HardLinkDenied
                => new(TurnFailurePhase.ToolAuthorization, TurnEffectState.NotDispatched, TurnRecoveryAction.UseAuthorizedScope, index, round, name),
            ToolOutcomeCode.NotFound or ToolOutcomeCode.NotAFile or ToolOutcomeCode.NotADirectory
                => new(TurnFailurePhase.ToolExecution, TurnEffectState.Completed, TurnRecoveryAction.InspectWorkspace, index, round, name),
            ToolOutcomeCode.InvalidArguments or ToolOutcomeCode.InvalidRange
                => new(TurnFailurePhase.ToolArguments, TurnEffectState.NotDispatched, TurnRecoveryAction.CorrectArguments, index, round, name),
            _ => null,
        };
    }
}

/// <summary>An owner-issued reference for paged access using the current tool and Session authority.</summary>
public sealed record TurnFailureOutput(string ToolName, string Handle, string[] Streams);

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<TurnFailurePhase>))]
public enum TurnFailurePhase
{
    [JsonStringEnumMemberName("toolArguments")]
    ToolArguments = 1,

    [JsonStringEnumMemberName("toolAuthorization")]
    ToolAuthorization,

    [JsonStringEnumMemberName("toolExecution")]
    ToolExecution,

    [JsonStringEnumMemberName("toolPrecondition")]
    ToolPrecondition,

    [JsonStringEnumMemberName("hostPreparation")]
    HostPreparation,

    [JsonStringEnumMemberName("hostExecution")]
    HostExecution,

    [JsonStringEnumMemberName("providerRequest")]
    ProviderRequest,

    [JsonStringEnumMemberName("callerCancellation")]
    CallerCancellation,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<TurnEffectState>))]
public enum TurnEffectState
{
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("notDispatched")]
    NotDispatched,

    [JsonStringEnumMemberName("completed")]
    Completed,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<TurnRecoveryAction>))]
public enum TurnRecoveryAction
{
    [JsonStringEnumMemberName("correctArguments")]
    CorrectArguments = 1,

    [JsonStringEnumMemberName("useAuthorizedScope")]
    UseAuthorizedScope,

    [JsonStringEnumMemberName("inspectWorkspace")]
    InspectWorkspace,

    [JsonStringEnumMemberName("inspectSession")]
    InspectSession,

    [JsonStringEnumMemberName("checkProvider")]
    CheckProvider,

    [JsonStringEnumMemberName("readToolOutput")]
    ReadToolOutput,
}
