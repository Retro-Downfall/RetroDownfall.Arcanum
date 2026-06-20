using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ToolAttunementTests
{

    [Fact]
    public void ApplyAttunement_FiltersToDeclaredTools()
    {
        List<AITool> mcpTools =
        [
            CreateTool("read_file_chunk"),
            CreateTool("execute_command"),
            CreateTool("write_file"),
        ];

        AttunementResult result = ArtifactAttunement.ApplyAttunement(mcpTools, ["read_file_chunk", "write_file"]);

        Assert.Equal(2, result.Allowed.Count);

        Assert.Equal(["execute_command"], result.Excluded);

        Assert.Contains(result.Allowed, static t => t is AIFunction fn && fn.Name == "read_file_chunk");

        Assert.Contains(result.Allowed, static t => t is AIFunction fn && fn.Name == "write_file");
    }

    [Fact]
    public void ApplyAttunement_EmptyOrNullDeclaredTools_ReturnsAllMcpTools()
    {
        List<AITool> mcpTools = [CreateTool("read_file_chunk")];

        AttunementResult empty = ArtifactAttunement.ApplyAttunement(mcpTools, []);

        AttunementResult nullResult = ArtifactAttunement.ApplyAttunement(mcpTools, null);

        Assert.Single(empty.Allowed);

        Assert.Empty(empty.Excluded);

        Assert.Single(nullResult.Allowed);

        Assert.Empty(nullResult.Excluded);
    }

    [Fact]
    public void ApplyAttunement_BogusDeclaredTool_ExcludesWithoutThrow()
    {
        List<AITool> mcpTools = [CreateTool("read_file_chunk")];

        AttunementResult result = ArtifactAttunement.ApplyAttunement(mcpTools, ["nonexistent_tool"]);

        Assert.Empty(result.Allowed);

        Assert.Single(result.Excluded);

        Assert.Equal("read_file_chunk", result.Excluded[0]);
    }

    private static AIFunction CreateTool(string name)
    {
        return AIFunctionFactory.Create(() => "ok", name);
    }

}
