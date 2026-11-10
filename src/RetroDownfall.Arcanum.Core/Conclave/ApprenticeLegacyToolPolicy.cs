using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Conclave;

/// <summary>Checks executable plan prose without changing retained plans or completed-step history.</summary>
public static class ApprenticeLegacyToolPolicy
{
    public static Error? Diagnose(IReadOnlyList<PlanStep> plan, int currentStep)
    {
        ToolNameDiagnostic[] diagnostics = GetDiagnostics(plan, currentStep);

        return diagnostics.Length == 0 ? null : new(ErrorCodes.Guild.LegacyToolName,
            $"{ErrorCodes.Guild.LegacyToolName}: This unfinished plan uses removed tools: {string.Join("; ", diagnostics.Select(static item => $"{item.ToolName} → {item.Replacement}"))}. Start a new Quest with a corrected plan.");
    }

    public static ToolNameDiagnostic[] GetDiagnostics(IReadOnlyList<PlanStep> plan, int currentStep)
    {
        List<string> removed = [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        for (int index = Math.Max(0, currentStep); index < plan.Count; index++)
        {
            PlanStep step = plan[index];

            if (step is null || step.Status == "completed" || step.Description is null) continue;

            ReadOnlySpan<char> description = step.Description.AsSpan();

            for (int position = 0; position < description.Length;)
            {
                if (!IsIdentifier(description[position]))
                {
                    position++;

                    continue;
                }

                int start = position++;

                while (position < description.Length && IsIdentifier(description[position])) position++;

                string token = description[start..position].ToString();

                if (ArcanumToolNameCatalog.TryGetReplacement(token, out _) && seen.Add(token)) removed.Add(token);
            }
        }

        return ToolNameDiagnostics.Removed(removed);
    }

    private static bool IsIdentifier(char value) => char.IsLetterOrDigit(value) || value == '_';
}
