using Microsoft.Extensions.AI;

using Microsoft.ML.Tokenizers;

using RetroDownfall.Arcanum.Api.Intelligence.Tools;

using RetroDownfall.Arcanum.Core.Mcp;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Approximates the Mana (token) overhead of exposing tool schemas to the model, for the
/// <c>tools: true</c> diagnostic on <c>POST /api/intelligence/mana</c>.
/// </summary>
/// <remarks>
/// This is intentionally an approximation, not a byte-for-byte reproduction of any single
/// provider's function/tool wire format: it tokenizes each tool's name, description, and JSON
/// schema, and adds <c>perToolOverheadTokens</c> per tool (mirroring the same per-message chat
/// template overhead convention <see cref="ManaPreflight"/> already applies per message). It
/// covers the two built-in tools (<see cref="ArcanumLocalTimeTool"/>, <see cref="ArcanumSystemInfoTool"/>)
/// plus whatever MCP tools are currently connected. It does not include workspace/spell-scoped
/// tools (for example the spell-script tool) that only exist during a live inference turn with a
/// resolved spell — this endpoint performs no spell resolution, by design (read-only diagnostic,
/// no Grimoire/inference side effects).
/// </remarks>
internal static class ToolSchemaManaEstimator
{

    public static async Task<int> EstimateAsync(
        IMcpConnectionManager mcp,
        Tokenizer tokenizer,
        int perToolOverheadTokens,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        List<AITool> tools = [new ArcanumLocalTimeTool(), new ArcanumSystemInfoTool()];

        IReadOnlyList<AITool> mcpTools = await mcp
            .GetAvailableToolsAsync(workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        tools.AddRange(mcpTools);

        long total = 0L;

        foreach (AITool tool in tools)
        {

            total += perToolOverheadTokens;

            if (tool is not AIFunction function)
            {

                continue;

            }

            total += tokenizer.CountTokens(function.Name);

            total += tokenizer.CountTokens(function.Description);

            total += tokenizer.CountTokens(function.JsonSchema.GetRawText());

        }

        return total > int.MaxValue ? int.MaxValue : (int)total;

    }

}
