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

        await manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["hung-initialize"] = HungServer(),
                    ["never-spawns"] = new()
                    {
                        Command = Path.Combine(Path.GetTempPath(), "arcanum-tests-no-such-mcp-server"),
                    },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        // The handshake deadline used to surface as OperationCanceledException, which aborted this
        // call and every server after the hung one.
        await manager.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));

        ManagedMcpServerEntry hung = Assert.IsType<ManagedMcpServerEntry>(
            manager.GetManagedEntryForTests("hung-initialize", workingDirectory: null));

        Assert.Equal(McpServerState.Error, hung.State);

        Assert.Contains("initialize handshake", hung.ErrorMessage, StringComparison.Ordinal);

        DateTimeOffset retryAfter = Assert.NotNull(hung.RestartAfterUtc);

        Assert.True(retryAfter > DateTimeOffset.UtcNow, "A hung server must be backed off, not retried on the next turn.");

        // The loop reached the other server too, whichever order the registry enumerated them in.
        ManagedMcpServerEntry other = Assert.IsType<ManagedMcpServerEntry>(
            manager.GetManagedEntryForTests("never-spawns", workingDirectory: null));

        Assert.Equal(McpServerState.Error, other.State);

        Assert.NotNull(other.RestartAfterUtc);
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
