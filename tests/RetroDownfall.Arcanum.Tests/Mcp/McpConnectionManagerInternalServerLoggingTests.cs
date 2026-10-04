using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConnectionManagerInternalServerLoggingTests
{
    [Fact]
    public async Task InternalServer_logs_tool_handler_failure_through_manager_logger()
    {
        await using TempWorkspace workspace = new();

        await workspace.InitializeAsync();

        CapturingLoggerFactory loggers = new();

        await using McpConnectionManager manager =
            McpConnectionManagerHarness.Create(loggerFactory: loggers);

        IReadOnlyList<AITool> tools = await manager.GetAvailableToolsAsync(workspace.Root);

        AIFunction readFileChunk = Assert.IsAssignableFrom<AIFunction>(
            tools.Single(static tool => string.Equals(tool.Name, "read_file_chunk", StringComparison.Ordinal)));

        // A number where the schema requires a string makes the server's argument deserialization
        // throw; the handler logs that fault and answers with a generic tool error.
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await readFileChunk.InvokeAsync(
                new AIFunctionArguments(
                    new Dictionary<string, object?>
                    {
                        ["relativePath"] = 12345,
                        ["startLine"] = 1,
                        ["endLine"] = 1,
                    }),
                CancellationToken.None));

        Assert.Contains("Invalid arguments for read_file_chunk", failure.Message, StringComparison.Ordinal);

        CapturingLoggerFactory.CapturedLogEntry entry = Assert.Single(
            loggers.Entries,
            static entry => entry.Level == LogLevel.Error
                && entry.Category.EndsWith(nameof(ArcanumInternalToolServer), StringComparison.Ordinal));

        Assert.Contains("read_file_chunk argument deserialization failed", entry.Message, StringComparison.Ordinal);

        Assert.IsAssignableFrom<System.Text.Json.JsonException>(entry.Exception);
    }

    [Fact]
    public async Task Manager_never_creates_a_logger_for_the_SDK_client_categories()
    {
        await using TempWorkspace workspace = new();

        await workspace.InitializeAsync();

        CapturingLoggerFactory loggers = new();

        await using McpConnectionManager manager =
            McpConnectionManagerHarness.Create(loggerFactory: loggers);

        _ = await manager.GetAvailableToolsAsync(workspace.Root);

        // The McpHttp client strips every logger because hosted endpoints embed bearer tokens in the
        // URL path; the manager's factory may therefore serve only the internal server's category.
        Assert.All(
            loggers.CreatedCategories,
            static category => Assert.EndsWith(
                nameof(ArcanumInternalToolServer),
                category,
                StringComparison.Ordinal));
    }
}
