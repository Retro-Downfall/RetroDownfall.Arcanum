using System.Diagnostics;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpProcessTransportChildExitTests
{

    // W2.5 Fix 5: when the child process exits, the Exited handler must dispose
    // the Process handle exactly once (so an undisposed Process + Exited handler
    // do not accumulate under a crash/restart loop). An idempotency guard
    // (_processDisposed via Interlocked.CompareExchange) shared with DisposeAsync
    // makes a later DisposeAsync safe (no double-dispose, no throw). Mirrors the
    // W2.4 Fix 3 LlamaServerManagerUnexpectedExitTests pattern: a real, benign
    // helper process (NOT an MCP server) is spawned, killed, and the Exited path
    // is observed. The deterministic Exited-vs-DisposeAsync *race* double-dispose
    // case is covered by the shared guard; this test proves the single-dispose
    // contract on the child-exit path and that a subsequent DisposeAsync is a no-op.

    [Fact]

    public async Task ExitedHandler_DisposesProcess_AndLaterDisposeAsyncIsSafe()
    {

        McpProcessTransport transport = new(
            fileName: OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sleep",
            arguments: OperatingSystem.IsWindows() ? "/c ping -n 31 127.0.0.1" : "30",
            maxJsonRpcLineBytes: 65_536);

        try
        {

            await transport.StartAsync(CancellationToken.None);

            Process? proc = transport.ProcessForTesting;

            Assert.NotNull(proc);

            try
            {

                proc!.Kill(entireProcessTree: true);

            }

            catch (Exception)
            {

                // Already exited (Exited will still fire).

            }

            bool released = await WaitForAsync(
                () => transport.ProcessForTesting is null,
                TimeSpan.FromSeconds(5));

            Assert.True(released, "Process handle was not released after child exit.");

            Assert.Null(transport.ProcessForTesting);

            // A later DisposeAsync must be safe (no double-dispose, no throw).

            await transport.DisposeAsync();

        }

        finally
        {

            try
            {

                await transport.DisposeAsync();

            }

            catch (Exception)
            {

                // Best-effort cleanup.

            }

        }

    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {

            if (condition())
            {

                return true;

            }

            await Task.Delay(25, CancellationToken.None);

        }

        return condition();

    }

}
