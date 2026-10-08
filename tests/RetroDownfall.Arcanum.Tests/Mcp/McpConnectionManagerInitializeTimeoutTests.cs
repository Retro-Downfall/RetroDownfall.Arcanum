using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConnectionManagerInitializeTimeoutTests
{
    [Fact]
    public async Task A_server_that_hangs_during_initialize_is_a_failed_start_with_backoff_and_does_not_abort_global_initialization()
    {
        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        manager.InitializationTimeoutForTests = TimeSpan.FromMilliseconds(300);

        // Two hung servers, because the registry enumerates in hash order and that order differs from one
        // process to the next. With one hung server and one fast one, the abort this guards against only
        // shows when the hung server happens to come first; with two, whichever is first aborts the loop
        // and the other is never attempted, so every ordering exposes it.
        await manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["hung-initialize-a"] = HungServer(),
                    ["hung-initialize-b"] = HungServer(),
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        // The handshake deadline used to surface as OperationCanceledException, which aborted this
        // call and every server after the first hung one.
        await manager.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));

        foreach (string name in new[] { "hung-initialize-a", "hung-initialize-b" })
        {
            ManagedMcpServerEntry hung = Assert.IsType<ManagedMcpServerEntry>(
                manager.GetManagedEntryForTests(name, workingDirectory: null));

            Assert.Equal(McpServerState.Error, hung.State);

            Assert.Contains("initialize handshake", hung.ErrorMessage, StringComparison.Ordinal);

            DateTimeOffset retryAfter = Assert.NotNull(hung.RestartAfterUtc);

            Assert.True(retryAfter > DateTimeOffset.UtcNow, $"{name} must be backed off, not retried on the next turn.");
        }
    }

    private static McpServerConfig HungServer() =>
        OperatingSystem.IsWindows()
            ? new McpServerConfig
            {
                Command = Path.Combine(
                    global::System.Environment.SystemDirectory,
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"),
                Args = ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
                Env = new Dictionary<string, string>
                {
                    ["SystemRoot"] = global::System.Environment.GetEnvironmentVariable("SystemRoot")!,
                },
            }
            : new McpServerConfig
            {
                Command = "/bin/sleep",
                Args = ["30"],
            };
}
