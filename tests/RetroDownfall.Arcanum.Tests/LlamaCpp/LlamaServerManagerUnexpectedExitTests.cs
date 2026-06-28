using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

namespace RetroDownfall.Arcanum.Tests.LlamaCpp;

public sealed class LlamaServerManagerUnexpectedExitTests
{

    // W2.4 Fix 3: when the llama-server process exits unexpectedly, OnExited
    // must detach+dispose the Process (and its handlers) exactly once. A later
    // graceful StopAsync must be a no-op (no double-dispose, no throw).
    //
    // This drives the smallest seam directly (ManagedLlamaServer) with a real,
    // benign helper process (NOT llama-server). The deterministic
    // OnExited+StopAsync *race* double-dispose case is noted as a follow-up;
    // this test proves the single-dispose contract on the unexpected-exit path.

    [Fact]
    public async Task OnExited_DisposesProcessAndLaterStopAsyncIsSafe()
    {

        LlamaServerManager.ManagedLlamaServer server = new(
            cacheKey: "test-cache-key",
            maxConcurrent: 1,
            shutdownTimeoutSeconds: 5,
            eventBus: new FakeEventBus(),
            logger: NullLogger.Instance,
            onUnexpectedExit: _ => { });

        Process process = StartBenignHelperProcess();

        try
        {

            server.SetState(LlamaServerState.Running);

            server.AttachProcess(process, port: 0);

            try
            {

                process.Kill(entireProcessTree: true);

            }
            catch (Exception)
            {

                // Already exited (OnExited will still fire).

            }

            await WaitForStateAsync(server, LlamaServerState.Error, TimeSpan.FromSeconds(5));

            Assert.Equal(LlamaServerState.Error, server.State);

            // Fix 3: OnExited must null the Process ref after detaching+disposing.

            Assert.Null(server.Process);

            // A subsequent graceful StopAsync must not double-dispose or throw.

            await server.StopAsync(CancellationToken.None);

            Assert.Equal(LlamaServerState.Stopped, server.State);

        }
        finally
        {

            try
            {

                process.Kill(entireProcessTree: true);

            }
            catch (Exception)
            {

                // Best-effort cleanup; OnExited/StopAsync may have disposed already.

            }

            process.Dispose();

        }

    }

    private static Process StartBenignHelperProcess()
    {

        ProcessStartInfo psi = new()
        {

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,

        };

        if (OperatingSystem.IsWindows())
        {

            psi.FileName = "cmd.exe";

            psi.Arguments = "/c ping -n 31 127.0.0.1";

        }
        else
        {

            psi.FileName = "/bin/sleep";

            psi.Arguments = "30";

        }

        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start benign helper process.");

    }

    private static async Task WaitForStateAsync(
        LlamaServerManager.ManagedLlamaServer server,
        LlamaServerState expected,
        TimeSpan timeout)
    {

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {

            if (server.State == expected)
            {

                return;

            }

            await Task.Delay(50, CancellationToken.None);

        }

        Assert.Fail($"Timed out waiting for LlamaServerState.{expected}; current={server.State}.");

    }

    private sealed class FakeEventBus : IEventBus
    {

        public void Publish<T>(T @event) where T : notnull
        {

        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull =>
            AsyncEnumerable.Empty<T>();

    }

}
