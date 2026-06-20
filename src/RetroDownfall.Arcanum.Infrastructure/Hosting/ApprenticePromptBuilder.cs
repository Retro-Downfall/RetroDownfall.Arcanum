using System.Text;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal static class ApprenticePromptBuilder
{

    public static string BuildPlanGenerationPrompt(Apprentice apprentice)
    {
        return $$"""
            You are the Wizard of Arcanum, creating a plan for your Apprentice: {{apprentice.Name}}.
            The Dungeon Master has given this goal: {{apprentice.Goal}}

            Generate a detailed step-by-step plan to accomplish this goal.
            Each step should be a concrete, actionable task that uses available tools
            (file operations, commands, lore queries, etc.).

            Return ONLY a JSON array of step objects. No other text.
            Format: [{"index": 1, "description": "..."}, {"index": 2, "description": "..."}]
            """;
    }

    public static string BuildStepExecutionPrompt(
        Apprentice apprentice,
        IReadOnlyList<PlanStep> plan,
        int currentStepIndex,
        ApprenticeCheckpoint? checkpoint = null)
    {
        StringBuilder sb = new();

        sb.AppendLine($"You are {apprentice.Name}, an Apprentice of the Wizard of Arcanum.");

        sb.AppendLine($"The Dungeon Master's goal for you: {apprentice.Goal}");

        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(checkpoint?.DmGuidance))
        {

            sb.AppendLine("The Dungeon Master has intervened with this guidance:");

            sb.AppendLine(checkpoint.DmGuidance.Trim());

            sb.AppendLine();

        }

        sb.AppendLine("Plan progress:");

        for (int i = 0; i < plan.Count; i++)
        {
            PlanStep step = plan[i];

            if (i < currentStepIndex || string.Equals(step.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"✓ Step {step.Index}: {step.Description}");
            }
            else if (i == currentStepIndex)
            {
                sb.AppendLine($"→ Step {step.Index}: {step.Description}  ← CURRENT");
            }
            else
            {
                sb.AppendLine($"  Step {step.Index}: {step.Description}");
            }
        }

        sb.AppendLine();

        sb.AppendLine("Execute the current step now. Use the available tools.");

        sb.AppendLine("When the step is complete, summarize what you accomplished.");

        sb.AppendLine(
            "If you encounter an unresolvable obstacle, call the petition_dungeon_master tool with a clear reason instead of guessing.");

        sb.AppendLine("If you encounter a transient error you cannot resolve, explain why the step failed.");

        return sb.ToString();

    }

    public static string BuildWeaveEvaluationPrompt(
        Apprentice apprentice,
        IReadOnlyList<PlanStep> plan,
        int completedStepIndex)
    {
        StringBuilder sb = new();

        sb.AppendLine("You are the Wizard of Arcanum, evaluating whether the Weave must shift for your Apprentice.");

        sb.AppendLine($"Apprentice: {apprentice.Name}");

        sb.AppendLine($"Goal: {apprentice.Goal}");

        sb.AppendLine();

        sb.AppendLine("Completed steps:");

        for (int i = 0; i <= completedStepIndex && i < plan.Count; i++)
        {
            PlanStep step = plan[i];

            sb.AppendLine($"- Step {step.Index}: {step.Description}");

            if (!string.IsNullOrWhiteSpace(step.Result))
            {

                sb.AppendLine($"  Result: {step.Result.Trim()}");

            }

        }

        sb.AppendLine();

        sb.AppendLine("Remaining steps (current plan tail):");

        for (int i = completedStepIndex + 1; i < plan.Count; i++)
        {
            PlanStep step = plan[i];

            sb.AppendLine($"- Step {step.Index}: {step.Description}");

        }

        sb.AppendLine();

        sb.AppendLine(
            "Based on the completed step's outcome and the workspace state implied by the results, decide whether the remaining plan should change.");

        sb.AppendLine("If the remaining steps are still correct, reply with exactly: NO_CHANGE");

        sb.AppendLine(
            "If strategy must change, reply with ONLY a JSON array of revised remaining step objects (no markdown fences):");

        sb.AppendLine("[{\"index\": N, \"description\": \"...\"}, ...]");

        return sb.ToString();

    }

}
