using System.Text.Json;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.TheForge;

public static class ApprenticePlanParser
{

    // W3.6: cap raw plan text before parsing so a runaway LLM response cannot force an unbounded
    // JSON parse/allocation. Generous relative to MaxPlanSteps × per-step size.
    public const int MaxResponseChars = 256 * 1024;

    public static List<PlanStep> ParsePlan(string responseText, int maxSteps = 200)
    {

        ArgumentNullException.ThrowIfNull(responseText);

        if (responseText.Length > MaxResponseChars)
        {

            throw new InvalidOperationException(
                $"Plan generation response is {responseText.Length} characters; the maximum allowed is {MaxResponseChars}.");

        }

        string trimmed = StripMarkdownFences(responseText.Trim());

        List<PlanStep>? steps;

        try
        {

            steps = JsonSerializer.Deserialize(trimmed, TheForgeJsonContext.Default.ListPlanStep);

        }
        catch (JsonException ex)
        {

            // W3.6: surface malformed plan JSON as a domain error (consistent with the empty/oversize
            // cases and the sibling TryParseRevisedPlan), not a raw JsonException for callers to know to catch.
            throw new InvalidOperationException("Plan generation returned malformed JSON.", ex);

        }

        if (steps is null || steps.Count == 0)
        {

            throw new InvalidOperationException("Plan generation returned an empty or invalid JSON array.");

        }

        if (steps.Count > maxSteps)
        {

            throw new InvalidOperationException(
                $"Plan generation returned {steps.Count} steps; the maximum allowed is {maxSteps}.");

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

    public static bool TryParseRevisedPlan(string responseText, out List<PlanStep>? steps, int maxSteps = 200)
    {

        steps = null;

        if (string.IsNullOrWhiteSpace(responseText))
        {

            return false;

        }

        string trimmed = StripMarkdownFences(responseText.Trim());

        if (string.Equals(trimmed, "NO_CHANGE", StringComparison.OrdinalIgnoreCase))
        {

            return false;

        }

        try
        {

            List<PlanStep>? parsed = JsonSerializer.Deserialize(trimmed, TheForgeJsonContext.Default.ListPlanStep);

            if (parsed is null || parsed.Count == 0)
            {

                return false;

            }

            if (parsed.Count > maxSteps)
            {

                return false;

            }

            List<PlanStep> normalized = new(parsed.Count);

            for (int i = 0; i < parsed.Count; i++)
            {

                PlanStep step = parsed[i];

                normalized.Add(step with
                {
                    Index = step.Index > 0 ? step.Index : i + 1,
                    Status = string.IsNullOrWhiteSpace(step.Status) ? "pending" : step.Status,
                });

            }

            steps = normalized;

            return true;

        }
        catch (JsonException)
        {

            return false;

        }

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
