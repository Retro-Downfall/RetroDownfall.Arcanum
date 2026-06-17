using System.Text.Json;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.TheForge;

public static class ApprenticePlanParser
{

    public static List<PlanStep> ParsePlan(string responseText)
    {

        string trimmed = StripMarkdownFences(responseText.Trim());

        List<PlanStep>? steps = JsonSerializer.Deserialize(trimmed, TheForgeJsonContext.Default.ListPlanStep);

        if (steps is null || steps.Count == 0)
        {

            throw new InvalidOperationException("Plan generation returned an empty or invalid JSON array.");

        }

        List<PlanStep> normalized = new(steps.Count);

        for (int i = 0; i < steps.Count; i++)
        {

            PlanStep step = steps[i];

            normalized.Add(step with
            {
                Index = step.Index > 0 ? step.Index : i + 1,
                Status = string.IsNullOrWhiteSpace(step.Status) ? "pending" : step.Status,
            });

        }

        return normalized;

    }

    public static bool IsPlanEmpty(string? planJson)
    {

        if (string.IsNullOrWhiteSpace(planJson))
        {

            return true;

        }

        List<PlanStep>? steps = JsonSerializer.Deserialize(planJson, TheForgeJsonContext.Default.ListPlanStep);

        return steps is null || steps.Count == 0;

    }

    private static string StripMarkdownFences(string text)
    {

        if (!text.StartsWith("```", StringComparison.Ordinal))
        {

            return text;

        }

        int firstNewline = text.IndexOf('\n');

        if (firstNewline < 0)
        {

            return text;

        }

        int closingFence = text.LastIndexOf("```", StringComparison.Ordinal);

        if (closingFence <= firstNewline)
        {

            return text;

        }

        return text[(firstNewline + 1)..closingFence].Trim();

    }

}
