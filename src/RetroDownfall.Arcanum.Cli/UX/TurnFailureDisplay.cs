using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Cli.UX;

internal static class TurnFailureDisplay
{
    public static string InterruptedRequest => Describe(new(TurnFailurePhase.CallerCancellation, TurnEffectState.Unknown, TurnRecoveryAction.InspectSession));

    public static string Describe(TurnFailureEvidence evidence)
    {
        string phase = evidence.Phase switch
        {
            TurnFailurePhase.ToolArguments => "Tool arguments",
            TurnFailurePhase.ToolAuthorization => "Tool authorization",
            TurnFailurePhase.ToolExecution => "Tool execution",
            TurnFailurePhase.ToolPrecondition => "Tool precondition",
            TurnFailurePhase.HostPreparation => "Turn preparation",
            TurnFailurePhase.HostExecution => "Turn execution",
            TurnFailurePhase.ProviderRequest => "Provider request",
            TurnFailurePhase.CallerCancellation => "Turn cancellation",
            _ => "Turn",
        };

        string effect = evidence.Effect switch
        {
            TurnEffectState.NotDispatched when evidence.ToolName is not null
                || evidence.Phase is TurnFailurePhase.ToolPrecondition or TurnFailurePhase.ToolArguments or TurnFailurePhase.ToolAuthorization
                => "The tool was never dispatched.",
            TurnEffectState.NotDispatched => "The request was never dispatched.",
            TurnEffectState.Completed => "The operation completed with a reported failure.",
            _ => "Completion is unknown.",
        };

        string action = evidence.NextAction switch
        {
            TurnRecoveryAction.CorrectArguments => "Check the tool schema and correct the arguments.",
            TurnRecoveryAction.UseAuthorizedScope => "Use a workspace path or tool within the current grant.",
            TurnRecoveryAction.InspectWorkspace => "Inspect the current workspace.",
            TurnRecoveryAction.CheckProvider => "Check the provider connection.",
            TurnRecoveryAction.ReadToolOutput when evidence.Output is { ToolName: "read_tool_output" } output
                && Guid.TryParseExact(output.Handle, "N", out Guid handle)
                => $"Read the retained diagnostics with read_tool_output using handle {handle:N}.",
            TurnRecoveryAction.ReadToolOutput => "Read the tool diagnostics for this Session.",
            _ => "Inspect the current Session before continuing.",
        };

        return $"{phase}. {effect} {action}";
    }

    public static string Append(string message, TurnFailureEvidence? evidence) =>
        evidence is null ? message : $"{message} {Describe(evidence)}";
}
