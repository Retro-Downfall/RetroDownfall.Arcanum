using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class WizardIntelligenceProvider
{
    private sealed class ToolStrategyRecovery
    {
        private MeAiChatMessage? _guidance;

        private bool _offeredAlternative;

        internal bool OfferedAlternative => _offeredAlternative;

        internal static Error? ReconciliationDependency(IReadOnlyList<TurnFailureEvidence>? observations)
        {
            TurnFailureEvidence? dependency = observations?.FirstOrDefault(
                static fact => fact.NextAction == TurnRecoveryAction.ReconcileTool && fact.InvocationId is not null);

            if (dependency is not null)
            {
                return new Error(ErrorCodes.Hub.ToolReconciliationRequired,
                    "Inspect the recorded tool invocation disposition in the current Session before continuing.")
                { FailureEvidence = dependency };
            }

            dependency = observations?.FirstOrDefault(static fact =>
                fact.Effect == TurnEffectState.Unknown || fact.NextAction == TurnRecoveryAction.ReconcileTool);

            if (dependency is null) return null;

            string message = dependency.Output is null
                ? "Tool completion is unresolved. Inspect the current workspace or Session before continuing."
                : "Tool completion is unresolved. Inspect the retained diagnostics and reconcile the current workspace before continuing.";

            return new Error(ErrorCodes.Hub.ToolReconciliationRequired, message) { FailureEvidence = dependency };
        }

        internal bool Offer(
            List<MeAiChatMessage> messages,
            ChatOptions options,
            ToolLoopProgressDetector progress,
            IReadOnlyList<ToolLoopProgressEntry> round,
            IReadOnlyList<TurnFailureEvidence>? observations,
            bool repeated,
            bool protectedContent)
        {
            if (_guidance is not null)
            {
                int previous = messages.FindIndex(message => ReferenceEquals(message, _guidance));

                if (previous >= 0) messages.RemoveAt(previous);
            }

            _guidance = null;

            if (!repeated && !progress.ReacquiredEvictedRound) _offeredAlternative = false;

            if (protectedContent || progress.ReacquiredEvictedRound || (repeated && _offeredAlternative))
                return false;

            string? advice = observations?.Select(fact => AdviseFailure(fact, options, progress, repeated))
                .FirstOrDefault(static guidance => guidance is not null);

            if (repeated && advice is null
                && round.Any(entry => IsWorkspaceTool(entry.ToolName) && IsNativeObservation(options, entry.ToolName)))
            {
                string? diagnostic = ChooseObservation(options, progress, repeated: true);

                if (diagnostic is not null)
                    advice = $"Use {diagnostic} for a different authorized workspace observation. Identify any information or authority needed to continue.";
            }

            if (advice is null) return false;

            string prefix = repeated
                ? "Arcanum recovery guidance. The workspace evidence returned unchanged."
                : "Arcanum recovery guidance. Use the current observation to revise the next action.";

            _guidance = new MeAiChatMessage(ChatRole.System, $"{prefix} {advice}");

            messages.Add(_guidance);

            _offeredAlternative |= repeated;

            return true;
        }

        private static string? AdviseFailure(
            TurnFailureEvidence fact, ChatOptions options, ToolLoopProgressDetector progress, bool repeated)
        {
            if (fact.ToolName is not { } name || !IsWorkspaceTool(name)) return null;

            if (fact.Output is { ToolName: ToolRiskClassifier.ReadToolOutputToolName } output
                && Guid.TryParseExact(output.Handle, "N", out _)
                && output.Streams.Contains("diagnostics", StringComparer.Ordinal)
                && IsNativeObservation(options, output.ToolName)
                && (!repeated || !progress.RecentlyObserved(output.ToolName)))
            {
                return $"Inspect the failed {name} using {output.ToolName} with handle {output.Handle}, stream diagnostics, and offset 0. Follow nextOffset to the relevant evidence before revising the action.";
            }

            if (fact.NextAction == TurnRecoveryAction.CorrectArguments
                && options.Tools?.OfType<AIFunction>().Any(tool => tool.Name == name) == true)
            {
                return $"Validate the {name} arguments against its current schema and correct the invalid values before using it again.";
            }

            if (fact.NextAction == TurnRecoveryAction.UseAuthorizedScope && !repeated)
            {
                return $"The requested {name} scope was refused. Use only an already-authorized scope or identify the missing authority.";
            }

            if (fact.NextAction is not (TurnRecoveryAction.InspectWorkspace or TurnRecoveryAction.ReadToolOutput))
                return null;

            bool patch = name == ToolRiskClassifier.ApplyPatchToolName;

            if (patch && IsNativeObservation(options, "read_file") && (!repeated || !progress.RecentlyObserved("read_file")))
            {
                return "Refresh the affected file with read_file before rebuilding the patch from the current contents.";
            }

            string? diagnostic = ChooseObservation(options, progress, repeated);

            return diagnostic is null
                ? null
                : $"Inspect the current workspace with {diagnostic} before revising {name}. Verify the revised action against the new observation.";
        }

        private static string? ChooseObservation(ChatOptions options, ToolLoopProgressDetector progress, bool repeated)
        {
            foreach (string name in new[] { "list_directory", ToolRiskClassifier.SearchWorkspaceToolName, "read_file" })
            {
                if (IsNativeObservation(options, name) && (!repeated || !progress.RecentlyObserved(name))) return name;
            }

            return null;
        }

        private static bool IsNativeObservation(ChatOptions options, string name) =>
            options.Tools?.Any(tool => tool.Name == name && ReadOnlyToolProvenance.IsCurrent(tool)) == true;

        private static bool IsWorkspaceTool(string name) => name is
            "read_file" or "list_directory" or "write_file" or "edit_file" or ToolRiskClassifier.SearchWorkspaceToolName
            or ToolRiskClassifier.WorkspaceCheckToolName or ToolRiskClassifier.ApplyPatchToolName
            or ToolRiskClassifier.ReadToolOutputToolName;
    }
}
