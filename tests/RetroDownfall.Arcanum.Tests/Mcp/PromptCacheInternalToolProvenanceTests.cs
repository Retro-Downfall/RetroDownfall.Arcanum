using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class PromptCacheInternalToolProvenanceTests
{
    [Fact]
    public async Task Internal_owner_issues_only_audited_constant_definition_proofs_before_merge()
    {
        await using TempWorkspace workspace = new();

        await workspace.InitializeAsync();

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        var tools = await manager.GetAvailableToolsAsync(workspace.Root);

        Assert.True(PromptCacheToolProvenance.IsCurrent(Assert.Single(tools, t => t.Name == "read_file_chunk")));

        Assert.True(PromptCacheToolProvenance.IsCurrent(Assert.Single(tools, t => t.Name == "ask_human")));

        Assert.False(PromptCacheToolProvenance.IsCurrent(Assert.Single(tools, t => t.Name == "search_workspace")));

        Assert.False(PromptCacheToolProvenance.AllCurrent(tools.ToList()));
    }
}
