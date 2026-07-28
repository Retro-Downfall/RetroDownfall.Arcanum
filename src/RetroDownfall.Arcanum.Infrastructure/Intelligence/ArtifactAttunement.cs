using Microsoft.Extensions.AI;

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

        // Legacy declaredTools: ["use_commlink"] permits the internal canonical alert tool only —
        // it does not broaden access to an unrelated external MCP tool that happens to share the alias.
        if (allow.Contains("use_commlink"))
        {
            allow.Add("send_commlink_alert");
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
