using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpSecurityLimitsTests
{

    private const int DefaultMaxJsonRpcLineBytes = 2_097_152;

    [Fact]
    public void ExceedsMaxLineUtf8Bytes_detects_oversized_lines()
    {

        string small = new('a', 16);

        string huge = new('a', DefaultMaxJsonRpcLineBytes + 1);

        Assert.False(McpSecurityLimits.ExceedsMaxLineUtf8Bytes(small, DefaultMaxJsonRpcLineBytes));

        Assert.True(McpSecurityLimits.ExceedsMaxLineUtf8Bytes(huge, DefaultMaxJsonRpcLineBytes));

    }

    [Fact]
    public void BoundToolDescription_truncates_long_descriptions()
    {

        string longDescription = new('x', McpSecurityLimits.MaxMcpToolDescriptionUtf8Bytes + 64);

        string bounded = McpSecurityLimits.BoundToolDescription(longDescription);

        Assert.True(Encoding.UTF8.GetByteCount(bounded) <= McpSecurityLimits.MaxMcpToolDescriptionUtf8Bytes + 64);

        Assert.Contains("truncated", bounded, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void BoundToolInputSchema_replaces_oversized_schema_with_empty_object()
    {

        string padding = new('y', McpSecurityLimits.MaxMcpToolInputSchemaUtf8Bytes);

        string hugeSchema = $"{{\"x\":\"{padding}\"}}";

        JsonElement schema = JsonDocument.Parse(hugeSchema).RootElement;

        JsonElement bounded = McpSecurityLimits.BoundToolInputSchema(schema, McpJsonSerializerContext.Default);

        Assert.Equal(JsonValueKind.Object, bounded.ValueKind);

        Assert.Equal("{}", bounded.GetRawText());

    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("LD_PRELOAD")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("HTTP_PROXY")]
    public void IsBlockedEnvironmentVariable_blocks_dangerous_keys(string key)
    {

        Assert.True(McpSecurityLimits.IsBlockedEnvironmentVariable(key));

    }

    [Fact]
    public void TruncateUtf8_preserves_valid_prefix_for_multibyte_characters()
    {

        string text = "ascii" + new string('é', 20);

        string truncated = McpSecurityLimits.TruncateUtf8(text, 12);

        Assert.True(Encoding.UTF8.GetByteCount(truncated) <= 12 + 64);

        Assert.StartsWith("ascii", truncated, StringComparison.Ordinal);

    }

}
