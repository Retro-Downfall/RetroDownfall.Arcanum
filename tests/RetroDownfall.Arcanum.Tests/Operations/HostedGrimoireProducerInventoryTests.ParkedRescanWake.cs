using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // Parked Rescan wake. The Long Rest worker wakes on a queue hint or on a clock timer. The checker proves a wait only when
    // every awaitable it starts is awaited at the line that starts it, so no timer-backed or channel task outlives its caller:
    // the wake is one awaited read of the bounded hint channel on a token that a TimeProvider-backed source cancels. The shapes
    // that keep a read pending across wakes, or race it against a delay, leave a task alive after the call and stay refused.
    private const string ParkedRescanQueue = """
        sealed class WakeQueue
        {
            private readonly System.Threading.Channels.Channel<Guid> _pending = System.Threading.Channels.Channel.CreateBounded<Guid>(new System.Threading.Channels.BoundedChannelOptions(1)
            {
                SingleReader = true,

                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
            });

            internal void Signal(Guid operation) => _pending.Writer.TryWrite(operation);

            internal ValueTask<Guid> ReadAsync(CancellationToken ct) => _pending.Reader.ReadAsync(ct);
        }
        """;

    private static string ParkedRescanWaiter(string shape) => shape switch
    {
        "timer-cancelled-read" => """
            static class Waiter
            {
                internal static async Task WaitAsync(WakeQueue queue, TimeSpan delay, TimeProvider clock, CancellationToken stoppingToken)
                {
                    using CancellationTokenSource rescan = new(delay, clock);

                    using CancellationTokenSource wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, rescan.Token);

                    try
                    {
                        _ = await queue.ReadAsync(wake.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (rescan.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                    {
                    }
                }
            }
            """,
        "retained-read-raced-with-delay" => """
            static class Waiter
            {
                private static Task<Guid>? hint;

                internal static async Task WaitAsync(WakeQueue queue, TimeSpan delay, TimeProvider clock, CancellationToken stoppingToken)
                {
                    hint ??= queue.ReadAsync(stoppingToken).AsTask();

                    using CancellationTokenSource rescan = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                    Task timer = Task.Delay(delay, clock, rescan.Token);

                    Task woke = await Task.WhenAny(hint, timer).ConfigureAwait(false);

                    await rescan.CancelAsync().ConfigureAwait(false);

                    if (woke == hint)
                    {
                        _ = await hint.ConfigureAwait(false);

                        hint = null;
                    }
                }
            }
            """,
        "read-started-and-dropped" => """
            static class Waiter
            {
                internal static async Task WaitAsync(WakeQueue queue, TimeSpan delay, TimeProvider clock, CancellationToken stoppingToken)
                {
                    using CancellationTokenSource rescan = new(delay, clock);

                    using CancellationTokenSource wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, rescan.Token);

                    _ = queue.ReadAsync(wake.Token).AsTask();

                    await Task.CompletedTask;
                }
            }
            """,
        "delay-started-and-dropped" => """
            static class Waiter
            {
                internal static async Task WaitAsync(WakeQueue queue, TimeSpan delay, TimeProvider clock, CancellationToken stoppingToken)
                {
                    _ = Task.Delay(delay, clock, stoppingToken);

                    _ = await queue.ReadAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private HostedProducerDiscovery<HostedProducerSite> RunParkedRescanWake(string shape)
    {
        string source = R2Source("await Waiter.WaitAsync(new WakeQueue(), TimeSpan.FromSeconds(60), TimeProvider.System, token).ConfigureAwait(false);",
            ParkedRescanQueue + "\n" + ParkedRescanWaiter(shape));

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSitesUnderPremises(
            HostedGrimoireProducerInventory.ProductionPremises, [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"PARKED_RESCAN_WAKE_DIAGNOSTIC shape={shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        return result;
    }

    [Fact]
    public void ParkedRescanWakeAsOneAwaitedReadOnATimerCancelledTokenIsProvenWithNoSites()
    {
        HostedProducerDiscovery<HostedProducerSite> result = RunParkedRescanWake("timer-cancelled-read");

        Assert.Empty(result.Diagnostics);

        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("retained-read-raced-with-delay", "System.Threading.Tasks.Task.Delay")]
    [InlineData("retained-read-raced-with-delay", "System.Threading.Channels.ChannelReader`1.ReadAsync")]
    [InlineData("delay-started-and-dropped", "System.Threading.Tasks.Task.Delay")]
    [InlineData("read-started-and-dropped", "System.Threading.Channels.ChannelReader`1.ReadAsync")]
    public void AWaitThatLeavesATimerOrChannelTaskAliveAfterItsCallerStaysRefused(string shape, string callee)
    {
        HostedProducerDiscovery<HostedProducerSite> result = RunParkedRescanWake(shape);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Detail.StartsWith(callee + ";", StringComparison.Ordinal));
    }
}
