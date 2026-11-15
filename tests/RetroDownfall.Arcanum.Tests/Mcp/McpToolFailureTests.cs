using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpToolFailureTests
{
    [Theory]
    [InlineData("{\"code\":1}")]
    [InlineData("{\"code\":\"1\"}")]
    [InlineData("{\"code\":999}")]
    [InlineData("{\"code\":\"unknown\"}")]
    [InlineData("{\"code\":\"externalToolError\"}")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Native_failure_payload_rejects_numeric_unknown_or_non_native_codes(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);

        Assert.Null(McpToolFailure.ReadNative(document.RootElement));
    }

    [Fact]
    public void Native_failure_payload_uses_only_code_owned_message_and_denial_classification()
    {
        using JsonDocument document = JsonDocument.Parse("{\"code\":\"notFound\",\"message\":\"PRIVATE_WIRE_TEXT\",\"denied\":true}");

        McpToolFailure result = Assert.IsType<McpToolFailure>(McpToolFailure.ReadNative(document.RootElement));

        Assert.Equal(ToolOutcomeCode.NotFound, result.Outcome.Code);

        Assert.False(result.Outcome.Denied);

        Assert.Contains("not found", result.Outcome.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("PRIVATE_WIRE_TEXT", result.Outcome.Message, StringComparison.Ordinal);
    }
}
