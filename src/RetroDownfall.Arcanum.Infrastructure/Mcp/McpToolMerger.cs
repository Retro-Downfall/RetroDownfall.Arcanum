using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Stateless merge of MCP tool rows into a workspace tool surface: internal → global → local with local-wins dedup (Ordinal tool names).
/// </summary>
internal static class McpToolMerger
{

    private static readonly HashSet<string> ReservedInternalToolNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ToolRiskClassifier.ExecuteCommandToolName,

            ToolRiskClassifier.ApplyPatchToolName,

            ToolRiskClassifier.WorkspaceCheckToolName,

            ToolRiskClassifier.SearchWorkspaceToolName,

            // Covenant tools must remain unshadowable independently of Ward classification: an
            // external server that claimed either name would be handed the operator's profile writes.
            CovenantToolNames.ProposeCovenant,

            CovenantToolNames.RetireCovenant,
        };

    internal readonly record struct GlobalDedupResult(

        Dictionary<string, LoadedMcpToolRow> FirstByToolName,

        IReadOnlyList<AITool> SurfaceTools);

    /// <summary>
    /// First-seen dedup of global-partition tool rows by <see cref="AITool.Name"/> (<see cref="StringComparer.Ordinal"/>).
    /// </summary>
    internal static GlobalDedupResult DedupeGlobalTaggedTools(
        IReadOnlyList<LoadedMcpToolRow> globalTagged,
        ILogger? collisionLogger = null)
    {

        Dictionary<string, LoadedMcpToolRow> byName = new(StringComparer.Ordinal);

        List<AITool> surface = [];

        foreach (LoadedMcpToolRow row in globalTagged)
        {

            if (IsReservedInternalName(row.Tool.Name))
            {

                LogExternalCollision(collisionLogger, row.Tool.Name, "global");

                continue;

            }

            if (byName.TryAdd(row.Tool.Name, row))
            {

                surface.Add(row.Tool);

            }

        }

        return new GlobalDedupResult(byName, surface);

    }

    /// <summary>
    /// Merges internal in-process tools, global profile tools, and workspace-local tools into one surface.
    /// Local rows win on name collision; differing server registrations produce a fallback <see cref="McpBridgeTool"/>.
    /// </summary>
    internal static IReadOnlyList<AITool> MergeWorkspaceSurface(

        IReadOnlyList<LoadedMcpToolRow> internalTagged,

        IReadOnlyDictionary<string, LoadedMcpToolRow> globalFirstByToolName,

        IReadOnlyList<LoadedMcpToolRow> workspaceLocalTagged,

        ILogger? bridgeFallbackLogger = null)
    {

        List<AITool> surface = [];

        Dictionary<string, LoadedMcpToolRow> mergedByName = new(StringComparer.Ordinal);

        foreach (LoadedMcpToolRow row in internalTagged)
        {

            if (mergedByName.TryAdd(row.Tool.Name, row))
            {

                surface.Add(row.Tool);

            }

        }

        foreach (KeyValuePair<string, LoadedMcpToolRow> kv in globalFirstByToolName)
        {

            if (IsReservedInternalName(kv.Key))
            {

                LogExternalCollision(bridgeFallbackLogger, kv.Key, "global");

                continue;

            }

            if (mergedByName.TryAdd(kv.Key, kv.Value))
            {

                surface.Add(kv.Value.Tool);

            }

        }

        if (workspaceLocalTagged.Count == 0)
        {

            return surface;

        }

        return ApplyLocalOverrides(surface, mergedByName, workspaceLocalTagged, bridgeFallbackLogger);

    }

    private static IReadOnlyList<AITool> ApplyLocalOverrides(

        IReadOnlyList<AITool> globalSurface,

        IReadOnlyDictionary<string, LoadedMcpToolRow> globalByName,

        IReadOnlyList<LoadedMcpToolRow> localTagged,

        ILogger? bridgeFallbackLogger)
    {

        List<AITool> merged = new(globalSurface.Count + localTagged.Count);

        Dictionary<string, int> indexByName = new(StringComparer.Ordinal);

        foreach (AITool t in globalSurface)
        {

            if (t is not AIFunction fn)
            {

                merged.Add(t);

                continue;

            }

            string name = fn.Name;

            if (!indexByName.TryAdd(name, merged.Count))
            {

                continue;

            }

            merged.Add(t);

        }

        foreach (LoadedMcpToolRow localRow in localTagged)
        {

            string name = localRow.Tool.Name;

            if (IsReservedInternalName(name))
            {

                LogExternalCollision(bridgeFallbackLogger, name, "workspace");

                continue;

            }

            if (!indexByName.TryGetValue(name, out int idx))
            {

                indexByName[name] = merged.Count;

                merged.Add(localRow.Tool);

                continue;

            }

            if (!globalByName.TryGetValue(name, out LoadedMcpToolRow globalRow)

                || McpServerRegistrationComparer.Equals(globalRow.Config, localRow.Config))
            {

                merged[idx] = localRow.Tool;

                continue;

            }

            McpBridgeTool replacement = new(

                localRow.Tool.Name,

                localRow.Tool.Description,

                localRow.Tool.JsonSchema,

                localRow.Client,

                localRow.Tool.ToolOutputCapBytes,

                fallbackClient: globalRow.Client,

                fallbackLogger: bridgeFallbackLogger);

            merged[idx] = replacement;

        }

        return merged;

    }

    private static bool IsReservedInternalName(string name) =>
        ReservedInternalToolNames.Contains(name);

    private static void LogExternalCollision(
        ILogger? logger,
        string toolName,
        string source) =>
        logger?.LogWarning(
            "Omitting {Source} MCP tool {ToolName} because the name is reserved for an internal Arcanum tool.",
            source,
            toolName);

}
