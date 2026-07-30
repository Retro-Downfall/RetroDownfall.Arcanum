using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Mcp;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Materializes the native + MCP tools used by the <c>tools: true</c> Mana diagnostic. Token
/// accounting remains exclusively owned by <c>IModelTokenEstimator</c>.
/// </summary>
internal static class ManaToolCatalog
{
    public static async Task<List<AITool>> CollectAsync(
        IMcpConnectionManager mcp,
        string? workingDirectory,
        ArcanumWebSearchTool? webSearchTool,
        ArcanumReadUrlTool? readUrlTool,
        CancellationToken cancellationToken)
    {
        List<AITool> tools = [new ArcanumLocalTimeTool(), new ArcanumSystemInfoTool()];

        if (webSearchTool is not null)
        {
            tools.Add(webSearchTool);
        }

        if (readUrlTool is not null)
        {
            tools.Add(readUrlTool);
        }

        IReadOnlyList<AITool> mcpTools = await mcp
            .GetAvailableToolsAsync(workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        tools.AddRange(mcpTools);
        return tools;
    }
}
