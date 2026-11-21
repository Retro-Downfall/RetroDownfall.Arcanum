using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage;

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

        TurnRecoveryAction nextAction = evidence.NextAction switch
        {
            TurnRecoveryAction.InspectSession when evidence.RecoveryCheckpoint is { } checkpoint => checkpoint.NextAction,
            TurnRecoveryAction.ProvideInput or TurnRecoveryAction.ResumeSession
                when evidence.RecoveryCheckpoint?.NextAction == TurnRecoveryAction.ReconcileTool => TurnRecoveryAction.ReconcileTool,
            _ => evidence.NextAction,
        };

        string action = nextAction switch
        {
            TurnRecoveryAction.CorrectArguments => "Check the tool schema and correct the arguments.",
            TurnRecoveryAction.UseAuthorizedScope => "Use a workspace path or tool within the current grant.",
            TurnRecoveryAction.InspectWorkspace => "Inspect the current workspace.",
            TurnRecoveryAction.CheckProvider => "Check the provider connection.",
            TurnRecoveryAction.ReadToolOutput when evidence.Output is { ToolName: "read_tool_output" } output
                && Guid.TryParseExact(output.Handle, "N", out Guid handle)
                => $"Read the retained diagnostics with read_tool_output using handle {handle:N}.",
            TurnRecoveryAction.ReadToolOutput => "Read the tool diagnostics for this Session.",
            TurnRecoveryAction.ReconcileTool => DescribeReconciliation(evidence),
            TurnRecoveryAction.ProvideInput or TurnRecoveryAction.ResumeSession => DescribeContinuation(nextAction, evidence.RecoveryCheckpoint),
            _ => "Inspect the current Session before continuing.",
        };

        return $"{phase}. {effect} {action}";
    }

    private static string DescribeReconciliation(TurnFailureEvidence evidence)
    {
        SessionRecoveryCheckpoint? checkpoint = evidence.RecoveryCheckpoint;

        string session = checkpoint?.SessionId.ToString("D") ?? "{sessionId}";

        string invocation = (checkpoint?.InvocationId ?? evidence.InvocationId)?.ToString("D") ?? "{invocationId}";

        string recovery = checkpoint is null ? string.Empty : $"Recovery checkpoint {checkpoint.Id:D}. ";

        return $"{recovery}Check the tool invocation before continuing. Use GET /api/sessions/{session}/tool-invocations/{invocation}.";
    }

    private static string DescribeContinuation(TurnRecoveryAction action, SessionRecoveryCheckpoint? checkpoint)
    {
        if (checkpoint is null) return "Find the current recovery checkpoint with arcanum session show --json.";

        bool needsInput = action == TurnRecoveryAction.ProvideInput;

        string instruction = needsInput ? "Answer the saved question" : "Continue the same Session";

        string prompt = needsInput ? "your answer" : "your guidance";

        return $"{instruction} with POST /api/intelligence/ping. Set sessionId to {checkpoint.SessionId:D} and "
            + $"recoveryCheckpointId to {checkpoint.Id:D}. Send a new Idempotency-Key and supply {prompt} as prompt.";
    }

    public static string Append(string message, TurnFailureEvidence? evidence) =>
        evidence is null ? message : $"{message} {Describe(evidence)}";
}
