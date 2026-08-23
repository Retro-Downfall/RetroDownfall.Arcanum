using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class ApprenticePromptBuilderTests
{

    [Fact]
    public void BuildPlanGenerationPrompt_includes_apprentice_name_and_goal()
    {

        Apprentice apprentice = new()
        {
            Name = "Merlin",
            Goal = "Organize the spellbook",
        };

        string prompt = ApprenticePromptBuilder.BuildPlanGenerationPrompt(apprentice);

        Assert.Contains("Merlin", prompt, StringComparison.Ordinal);

        Assert.Contains("Organize the spellbook", prompt, StringComparison.Ordinal);

        Assert.Contains("JSON array", prompt, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void BuildStepExecutionPrompt_marks_current_step_and_dm_guidance()
    {

        Apprentice apprentice = new()
        {
            Name = "Rowan",
            Goal = "Fix tests",
        };

        List<PlanStep> plan =
        [
            new PlanStep { Index = 1, Description = "Read code", Status = "completed" },
            new PlanStep { Index = 2, Description = "Write tests" },
            new PlanStep { Index = 3, Description = "Run suite" },
        ];

        ApprenticeCheckpoint checkpoint = new()
        {
            DmGuidance = "Focus on edge cases.",
        };

        string prompt = ApprenticePromptBuilder.BuildStepExecutionPrompt(apprentice, plan, currentStepIndex: 1, checkpoint);

        Assert.Contains("CURRENT", prompt, StringComparison.Ordinal);

        Assert.Contains("Focus on edge cases.", prompt, StringComparison.Ordinal);

        Assert.Contains("petition_dungeon_master", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public void BuildWeaveEvaluationPrompt_lists_completed_and_remaining_steps()
    {

        Apprentice apprentice = new()
        {
            Name = "Ivy",
            Goal = "Ship feature",
        };

        List<PlanStep> plan =
        [
            new PlanStep { Index = 1, Description = "Plan", Result = "done" },
            new PlanStep { Index = 2, Description = "Build" },
            new PlanStep { Index = 3, Description = "Verify" },
        ];

        string prompt = ApprenticePromptBuilder.BuildWeaveEvaluationPrompt(apprentice, plan, completedStepIndex: 0);

        Assert.Contains("Result: done", prompt, StringComparison.Ordinal);

        Assert.Contains("Remaining steps", prompt, StringComparison.Ordinal);

        Assert.Contains("NO_CHANGE", prompt, StringComparison.Ordinal);

    }

}
