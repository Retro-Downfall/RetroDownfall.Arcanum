using System.Runtime.CompilerServices;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// A parse that ends disposes its own cancellation source, while its abandoned caller may still ask that source to cancel
/// (<see cref="SessionAttachmentDocumentExtractionHost.RequestCancellation"/>). Cancelling a disposed source does not throw:
/// it returns a faulted task. Dropping that task would surface as an unobserved task exception, which the served host logs
/// at Error level, long after and far away from its cause.
/// </summary>
public sealed class SessionAttachmentDocumentExtractionHostCancellationRaceTests
{
    private const string SourceTypeName = "System.Threading.CancellationTokenSource";

    private static readonly byte[] Document = "ARCCOMP1\nDOCUMENT"u8.ToArray();

    [Fact]
    public async Task RequestCancellation_SourceTheEndingParseAlreadyDisposed_RaisesNoUnobservedTaskException()
    {
        List<Exception> unobserved = [];

        void Capture(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
            {
                unobserved.AddRange(e.Exception.InnerExceptions);
            }
        }

        TaskScheduler.UnobservedTaskException += Capture;

        try
        {
            CancelDisposedSources(count: 200);

            await CollectUnobservedAsync();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Capture;
        }

        lock (unobserved)
        {
            Assert.DoesNotContain(unobserved, static exception => exception is ObjectDisposedException { ObjectName: SourceTypeName });
        }
    }

    [Fact]
    public async Task ParseInProcessAsync_CallerCancelledAsTheParseEnds_RaisesNoUnobservedTaskException()
    {
        List<Exception> unobserved = [];

        void Capture(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
            {
                unobserved.AddRange(e.Exception.InnerExceptions);
            }
        }

        TaskScheduler.UnobservedTaskException += Capture;

        try
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                await CancelCallerAsTheParseEndsAsync();
            }

            await CollectUnobservedAsync();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Capture;
        }

        lock (unobserved)
        {
            Assert.DoesNotContain(unobserved, static exception => exception is ObjectDisposedException { ObjectName: SourceTypeName });
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CancelDisposedSources(int count)
    {
        for (int index = 0; index < count; index++)
        {
            CancellationTokenSource source = new();

            source.Dispose();

            SessionAttachmentDocumentExtractionHost.RequestCancellation(source);
        }
    }

    /// <summary>
    /// Runs a parse whose caller is cancelled after the parse has ended and disposed its own source, so the parse is
    /// abandoned on a source that is already disposed. The extractor cancels the caller from the thread pool, and the
    /// callbacks of a cancelled source run last registered first: the callback registered here runs before the one that
    /// ends the caller's wait, and holds it back until the parse's source is disposed.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CancelCallerAsTheParseEndsAsync()
    {
        using CancellationTokenSource caller = new();

        SessionAttachmentDocumentExtractionHost host = new(
            (bytes, output, parserCancellation) =>
            {
                // The caller's wait registers its cancellation as the parse starts; let it do so first.
                Thread.Sleep(50);

                _ = caller.Token.UnsafeRegister(static state => WaitForDisposal((CancellationToken)state!), parserCancellation);

                _ = caller.CancelAsync();

                output.TryAppend("DOCUMENT");

                return ValueTask.CompletedTask;
            },
            TimeSpan.FromSeconds(30));

        using SessionAttachmentDocumentSnapshot snapshot = await host.ReadSnapshotAsync(
            _ => Task.FromResult<ReadOnlyMemory<byte>>(Document), Document.Length, 1024, CancellationToken.None, waitForSlot: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.ParseInProcessAsync(snapshot, caller.Token).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static void WaitForDisposal(CancellationToken parserCancellation)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < giveUp)
        {
            try
            {
                _ = parserCancellation.WaitHandle;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Thread.Yield();
        }
    }

    private static async Task CollectUnobservedAsync()
    {
        // The parse's own continuations and any cancellation callbacks settle on the thread pool first.
        await Task.Delay(100);

        GC.Collect();

        GC.WaitForPendingFinalizers();

        GC.Collect();

        GC.WaitForPendingFinalizers();
    }
}
