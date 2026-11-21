using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class GuildWorkspaceReadSchemaTests
{
    [Theory]
    [InlineData("read_file", "continuation", "relativePath,startLine,endLine")]
    [InlineData("list_directory", "continuation", "relativePath")]
    [InlineData("search_workspace", "cursor", "pattern,mode,caseSensitive")]
    public void Model_can_request_the_first_and_subsequent_page_using_only_workspace_arguments(
        string tool, string continuation, string required)
    {
        JsonElement schema = ArcanumInternalToolServer.BuildTaskToolSchema(tool);

        Assert.Equal(required.Split(',').Order(), schema.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).Order());

        JsonElement properties = schema.GetProperty("properties");

        Assert.Equal("string", properties.GetProperty(continuation).GetProperty("type").GetString());

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

        Assert.DoesNotContain(properties.EnumerateObject(), p => p.Name is "signetId" or "questId" or "workspaceId");
    }
}
