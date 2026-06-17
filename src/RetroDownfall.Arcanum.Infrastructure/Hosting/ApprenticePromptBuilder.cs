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

    public static string BuildStepExecutionPrompt(Apprentice apprentice, IReadOnlyList<PlanStep> plan, int currentStepIndex)
    {
        StringBuilder sb = new();

        sb.AppendLine($"You are {apprentice.Name}, an Apprentice of the Wizard of Arcanum.");

        sb.AppendLine($"The Dungeon Master's goal for you: {apprentice.Goal}");

        sb.AppendLine();

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

        sb.AppendLine("If you encounter an error you cannot resolve, explain why the step failed.");

        return sb.ToString();

    }

}
