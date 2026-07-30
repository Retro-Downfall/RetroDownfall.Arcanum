using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence.Subagents;

internal static class SubagentParentContextInjector
{
    public const string BudgetExhaustedMessage =
        "Subagent task failed: Delegated budget exhausted.";

    public static void AppendBudgetFailureIfRequired(
        IList<ChatMessage> parentMessages,
        string? toolName,
        string resultText)
    {
        ArgumentNullException.ThrowIfNull(parentMessages);
        ArgumentNullException.ThrowIfNull(resultText);

        if (string.Equals(
                toolName,
                ArcanumBuiltInToolNames.DelegateTask,
                StringComparison.Ordinal)
            && string.Equals(
                resultText,
                BudgetExhaustedMessage,
                StringComparison.Ordinal))
        {
            parentMessages.Add(
                new ChatMessage(
                    ChatRole.System,
                    BudgetExhaustedMessage));
        }
    }
}
