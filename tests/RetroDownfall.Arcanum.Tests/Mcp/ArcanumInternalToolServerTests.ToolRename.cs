using System.Text.Json;

using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed partial class ArcanumInternalToolServerTests
{
    [Theory]
    [InlineData("read_file_chunk", "read_file")]
    [InlineData("replace_text_block", "edit_file")]
    [InlineData("scribe_lexicon", "save_memory")]
    [InlineData("delete_lexicon", "delete_memory")]
    [InlineData("read_saga", "search_memory")]
    [InlineData("search_archives", "search_history")]
    [InlineData("adjust_initiative", "set_task_priority")]
    [InlineData("send_commlink_alert", "send_notification")]
    [InlineData("petition_dungeon_master", "request_intervention")]
    [InlineData("cast_sending", "create_agent_task")]
    [InlineData("dispatch_sending", "create_agent_task")]
    [InlineData("continue_sending", "send_agent_message")]
    public void Tool_cutover_registers_only_current_executable_names(string removed, string current)
    {
        Assert.Contains(current, ArcanumInternalToolServer.RegisteredToolNames);

        Assert.DoesNotContain(removed, ArcanumInternalToolServer.RegisteredToolNames);
    }

    [Fact]
    public async Task Renamed_file_read_preserves_bounded_slice_and_removed_name_cannot_read()
    {
        await using TestMcpSession session = await CreateSessionAsync();

        using JsonDocument arguments = JsonDocument.Parse("{\"relativePath\":\"notes/alpha.txt\",\"startLine\":2,\"endLine\":2}");

        McpToolsCallResultWire result = await session.CallToolAsync("read_file", arguments.RootElement);

        Assert.False(result.IsError);

        Assert.Contains("line two", result.Content[0].Text);

        Assert.DoesNotContain("line one", result.Content[0].Text);

        McpToolsCallResultWire removed = await session.CallToolAsync("read_file_chunk", arguments.RootElement);

        Assert.True(removed.IsError);

        Assert.DoesNotContain("line two", removed.Content[0].Text);
    }
}
