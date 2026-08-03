using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence;

internal readonly record struct AttunementResult(IReadOnlyList<AITool> Allowed, IReadOnlyList<string> Excluded);

internal static class ArtifactAttunement
{

    internal static AttunementResult ApplyAttunement(IReadOnlyList<AITool> mcpTools, IReadOnlyList<string>? declaredTools)
    {
        if (declaredTools is not { Count: > 0 })
        {
            return new AttunementResult(mcpTools, []);
        }

        HashSet<string> allow = new(declaredTools, StringComparer.OrdinalIgnoreCase);

        if (allow.Contains(
                ToolRiskClassifier.ExecuteCommandToolName))
        {

            allow.Add(
                ToolRiskClassifier.ReadCommandOutputToolName);

        }

        var allowed = new List<AITool>(mcpTools.Count);

        var excluded = new List<string>();

        foreach (AITool t in mcpTools)
        {
            if (t is AIFunction fn && allow.Contains(fn.Name))
            {
                allowed.Add(t);
            }
            else if (t is AIFunction fn2)
            {
                excluded.Add(fn2.Name);
            }
        }

        return new AttunementResult(allowed, excluded);
    }

}
