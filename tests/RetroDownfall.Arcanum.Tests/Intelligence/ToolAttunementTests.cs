using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

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

    [Fact]
    public void ApplyAttunement_AllowsDeclaredWorkspaceSearchAndExcludesOtherCodingTools()
    {
        List<AITool> mcpTools =
        [
            CreateTool("search_workspace"),
            CreateTool("apply_patch"),
            CreateTool("workspace_check"),
        ];

        AttunementResult result = ArtifactAttunement.ApplyAttunement(
            mcpTools,
            ["search_workspace"]);

        AIFunction allowed = Assert.IsAssignableFrom<AIFunction>(Assert.Single(result.Allowed));

        Assert.Equal("search_workspace", allowed.Name);
        Assert.Equal(["apply_patch", "workspace_check"], result.Excluded);
    }

    [Fact]
    public void ApplyAttunement_declared_execute_command_includes_its_read_continuation()
    {

        List<AITool> mcpTools =
        [
            CreateTool(ToolRiskClassifier.ExecuteCommandToolName),
            CreateTool(ToolRiskClassifier.ReadCommandOutputToolName),
            CreateTool("write_file"),
        ];

        AttunementResult result = ArtifactAttunement.ApplyAttunement(
            mcpTools,
            [ToolRiskClassifier.ExecuteCommandToolName]);

        Assert.Equal(
            [
                ToolRiskClassifier.ExecuteCommandToolName,
                ToolRiskClassifier.ReadCommandOutputToolName,
            ],
            result.Allowed
                .OfType<AIFunction>()
                .Select(static tool => tool.Name));

        Assert.Equal(["write_file"], result.Excluded);

    }

    [Fact]
    public void BuiltInToolCatalog_DefinesExactlyThreeAttunementExemptions()
    {
        Assert.True(
            ArcanumBuiltInToolNames.IsAttunementExempt(
                ArcanumBuiltInToolNames.GetLocalSystemTime));
        Assert.True(
            ArcanumBuiltInToolNames.IsAttunementExempt(
                ArcanumBuiltInToolNames.GetArcanumSystemInfo));
        Assert.True(
            ArcanumBuiltInToolNames.IsAttunementExempt(
                ArcanumBuiltInToolNames.RunSpellScript));

        Assert.False(
            ArcanumBuiltInToolNames.IsAttunementExempt(
                ArcanumBuiltInToolNames.BrowseWeb));
        Assert.True(
            ArcanumBuiltInToolNames.IsKnown(
                ArcanumBuiltInToolNames.BrowseWeb));
        Assert.False(
            ArcanumBuiltInToolNames.IsAttunementExempt(
                "some_other_tool"));
    }

    private static AIFunction CreateTool(string name)
    {
        return AIFunctionFactory.Create(() => "ok", name);
    }

}
