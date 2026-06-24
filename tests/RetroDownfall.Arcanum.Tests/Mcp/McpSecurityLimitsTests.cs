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

    [Fact]
    public async Task ReadLineUtf8CappedAsync_reads_line_without_per_char_io()
    {

        await using MemoryStream stream = new(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}\n"));

        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        McpStdioLineReader lineReader = new(reader);

        string? line = await lineReader.ReadLineUtf8CappedAsync(DefaultMaxJsonRpcLineBytes);

        Assert.Equal("{\"jsonrpc\":\"2.0\"}", line);

        string? eof = await lineReader.ReadLineUtf8CappedAsync(DefaultMaxJsonRpcLineBytes);

        Assert.Null(eof);

    }

    [Fact]
    public async Task ReadLineUtf8CappedAsync_skips_carriage_return_before_newline()
    {

        await using MemoryStream stream = new(Encoding.UTF8.GetBytes("alpha\r\nbeta\n"));

        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        McpStdioLineReader lineReader = new(reader);

        string? first = await lineReader.ReadLineUtf8CappedAsync(DefaultMaxJsonRpcLineBytes);

        string? second = await lineReader.ReadLineUtf8CappedAsync(DefaultMaxJsonRpcLineBytes);

        Assert.Equal("alpha", first);

        Assert.Equal("beta", second);

    }

    [Fact]
    public async Task ReadLineUtf8CappedAsync_throws_when_line_exceeds_utf8_cap()
    {

        string oversized = new('x', 32);

        await using MemoryStream stream = new(Encoding.UTF8.GetBytes($"{oversized}\ntrailing\n"));

        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        McpStdioLineReader lineReader = new(reader);

        await Assert.ThrowsAsync<JsonException>(
            () => lineReader.ReadLineUtf8CappedAsync(16));

        string? next = await lineReader.ReadLineUtf8CappedAsync(DefaultMaxJsonRpcLineBytes);

        Assert.Equal("trailing", next);

    }

}
