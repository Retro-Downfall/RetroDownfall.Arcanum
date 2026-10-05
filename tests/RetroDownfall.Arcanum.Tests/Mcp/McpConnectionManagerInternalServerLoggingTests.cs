using System.Net;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

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
    public async Task Starting_an_HTTP_server_never_creates_a_logger_for_the_SDK_transport_categories()
    {
        CapturingLoggerFactory loggers = new();

        CountingHandler http = new();

        await using McpConnectionManager manager =
            McpConnectionManagerHarness.Create(loggerFactory: loggers, httpHandler: http);

        // A public address literal passes the egress policy without any DNS lookup, and the stub answers
        // the SDK's initialize request with a failure, so the start reaches CreateHttpMcpClient and the
        // SDK transport without a real server.
        await manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["remote"] = new() { Url = "https://93.184.216.34/mcp" },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        Result started = await manager.StartAsync("remote", workingDirectory: null);

        Assert.True(started.IsFailure);

        Assert.Equal("Mcp.StartFailed", started.Error.Code);

        // Without this the test would pass vacuously: the SDK transport really did build and reach the wire.
        Assert.True(http.Requests > 0, "The start never reached the SDK's HTTP transport.");

        // A logger factory handed to HttpClientTransport (or to the SDK client) would create the SDK's
        // own categories, and those logs can carry the endpoint URL, which hosted MCP servers use to
        // embed a bearer token. The factory may serve only the internal server's category.
        Assert.All(
            loggers.CreatedCategories,
            static category => Assert.EndsWith(
                nameof(ArcanumInternalToolServer),
                category,
                StringComparison.Ordinal));
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

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _requests);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
