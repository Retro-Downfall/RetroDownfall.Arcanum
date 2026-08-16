using RetroDownfall.Arcanum.Api.Security;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;

namespace RetroDownfall.Arcanum.Api.Streaming;

[ExcludeFromCodeCoverage] // Reason: HTTP SSE heartbeat glue; exercised via integration routes.
internal static class SseStreamWriter
{

    private static readonly byte[] KeepAliveComment = ": keep-alive\n\n"u8.ToArray();

    private static readonly byte[] SseDone = "data: [DONE]\n\n"u8.ToArray();

    public static void PrepareResponse(HttpContext httpContext)
    {

        httpContext.Response.ContentType = "text/event-stream; charset=utf-8";

        // Never a bare assignment. A protected stream already carries "no-store, private", and
        // overwriting it here would weaken a response whose headers can no longer be corrected
        // once the first event has left (DESIGN §10.18).
        CovenantProtectedResponseHeaders.ApplyStreamingDefaultWithoutWeakening(httpContext);

        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

    }

    public static async Task StreamAsync<T>(
        HttpContext httpContext,
        IAsyncEnumerable<T> source,
        Func<T, CancellationToken, Task> writeFrameAsync,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken)
    {

        if (heartbeatInterval <= TimeSpan.Zero)
        {

            await foreach (T item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {

                try
                {

                    await writeFrameAsync(item, cancellationToken).ConfigureAwait(false);

                }

                catch (Exception ex) when (ClientDisconnect.IsClientDisconnect(ex, httpContext))
                {

                    // W3.4 Group A (S10): client disconnected mid-stream. Stop writing
                    // silently — no error or DONE frame to a dead socket. The caller's
                    // `using`/`finally` disposes the linked CTS, cancelling the producer
                    // (event bus subscription / chronicle pump) promptly.

                    return;

                }

            }

            return;

        }

        // Owned by this writer so a disconnect can unwind the producer and let the outstanding
        // MoveNextAsync complete before the enumerator is disposed (see QuiesceAndDisposeAsync).
        using CancellationTokenSource streamCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        IAsyncEnumerator<T> enumerator = source.GetAsyncEnumerator(streamCts.Token);

        // Keep a single pending MoveNextAsync for the lifetime of each item wait.
        // Heartbeats must WhenAny against the same move task — never start a second
        // MoveNextAsync while one is outstanding (IAsyncEnumerator is not concurrent-safe).
        Task<bool>? pendingMove = null;

        try
        {

            while (true)
            {

                pendingMove ??= enumerator.MoveNextAsync().AsTask();

                // Per-iteration linked source so a frame that wins the race releases the heartbeat
                // timer and its registration immediately instead of leaving one TimerQueueTimer per
                // delivered frame alive for the whole interval (matches the OpenAI /v1 writer).
                using CancellationTokenSource delayCts =
                    CancellationTokenSource.CreateLinkedTokenSource(streamCts.Token);

                Task delay = Task.Delay(heartbeatInterval, delayCts.Token);

                Task completed = await Task.WhenAny(pendingMove, delay).ConfigureAwait(false);

                if (completed == delay)
                {

                    try
                    {

                        await WriteKeepAliveAsync(httpContext, cancellationToken).ConfigureAwait(false);

                    }

                    catch (Exception ex) when (ClientDisconnect.IsClientDisconnect(ex, httpContext))
                    {

                        return;

                    }

                    continue;

                }

                delayCts.Cancel();

                bool hasNext = await pendingMove.ConfigureAwait(false);

                pendingMove = null;

                if (!hasNext)
                {

                    break;

                }

                try
                {

                    await writeFrameAsync(enumerator.Current, cancellationToken).ConfigureAwait(false);

                }

                catch (Exception ex) when (ClientDisconnect.IsClientDisconnect(ex, httpContext))
                {

                    return;

                }

            }

        }

        finally
        {

            await QuiesceAndDisposeAsync(enumerator, pendingMove, streamCts).ConfigureAwait(false);

        }

    }

    /// <summary>
    /// Disposes a streaming enumerator only once it is quiescent. A compiler-generated async
    /// iterator throws <see cref="NotSupportedException"/> from <c>DisposeAsync</c> while a
    /// <c>MoveNextAsync</c> is still in flight, so every SSE writer that abandons the pump on a
    /// client disconnect (or on a cancellation that surfaces from the keep-alive write) must first
    /// cancel the producer and observe the outstanding move.
    /// </summary>
    public static async ValueTask QuiesceAndDisposeAsync<T>(
        IAsyncEnumerator<T> enumerator,
        Task<bool>? pendingMove,
        CancellationTokenSource? unwindSignal)
    {

        ArgumentNullException.ThrowIfNull(enumerator);

        if (pendingMove is not null)
        {

            if (!pendingMove.IsCompleted && unwindSignal is not null)
            {

                try
                {

                    await unwindSignal.CancelAsync().ConfigureAwait(false);

                }

                catch (ObjectDisposedException)
                {

                    // The caller already tore the linked source down; the move will unwind with it.

                }

            }

            try
            {

                _ = await pendingMove.ConfigureAwait(false);

            }

            catch
            {

                // The abandoned move's outcome is irrelevant — observing it only keeps the task
                // from faulting unobserved and guarantees the iterator is parked before dispose.

            }

        }

        await enumerator.DisposeAsync().ConfigureAwait(false);

    }

    public static async Task WriteKeepAliveAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {

        await httpContext.Response.Body.WriteAsync(KeepAliveComment, cancellationToken).ConfigureAwait(false);

        await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

    }

    public static async Task WriteDoneAsync(HttpContext httpContext)
    {

        try
        {

            await httpContext.Response.Body.WriteAsync(SseDone, CancellationToken.None).ConfigureAwait(false);

            await httpContext.Response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        }

        catch
        {

            // Client disconnected before terminal frame could be written.

        }

    }

}
