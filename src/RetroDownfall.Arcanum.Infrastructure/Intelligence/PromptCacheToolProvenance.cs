using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence;

/// <summary>Ephemeral proofs issued only by audited construction owners; tool metadata cannot grant provenance.</summary>
internal static class PromptCacheToolProvenance
{
    private sealed record Definition(string Name, string Description, string Schema, string? ReturnSchema);

    private static readonly ConditionalWeakTable<AITool, Definition> Proofs = new();

    internal static void Issue(AIFunctionDeclaration tool)
    {
        if (tool.AdditionalProperties is { Count: > 0 })
        {
            return;
        }

        Proofs.AddOrUpdate(tool, Read(tool));
    }

    internal static bool IsCurrent(AITool tool) =>
        tool is AIFunctionDeclaration function
        && tool.AdditionalProperties is not { Count: > 0 }
        && Proofs.TryGetValue(tool, out Definition? proof)
        && proof == Read(function);

    internal static bool AllCurrent(IList<AITool>? tools) =>
        tools is null || tools.All(IsCurrent);

    private static Definition Read(AIFunctionDeclaration tool) =>
        new(tool.Name, tool.Description, tool.JsonSchema.GetRawText(), tool.ReturnJsonSchema?.GetRawText());
}
