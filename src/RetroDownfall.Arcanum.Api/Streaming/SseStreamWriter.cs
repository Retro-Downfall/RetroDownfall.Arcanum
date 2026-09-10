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

    /// <summary>
    /// Writes one SSE stream, ending it at a complete frame boundary when maintenance revokes it.
    /// </summary>
    /// <remarks>
    /// <paramref name="quiescence"/> is a required parameter rather than an optional one, because a
    /// stream that silently inherited "never quiesces" is exactly the stream that holds a transition
    /// open until it times out. Every caller states which it is.
    ///
    /// <para>The two tokens are deliberately different and must stay that way. Revocation is linked
    /// only into the token the producer enumerates on, so the producer stops; the frame writer and
    /// the keep-alive keep <paramref name="cancellationToken"/>, so a write already under way runs to
    /// its terminating blank line. A frame cancelled part way leaves bytes on the wire that no client
    /// can parse and no later frame can repair, and those bytes cannot be withdrawn — so the only
    /// safe place to stop is between frames.</para>
    ///
    /// <para>The terminal <c>[DONE]</c> is written here, on the quiesced path only, rather than by
    /// each of the five routes. It is what both first-party parsers read as a deliberate end; without
    /// it the CLI reports a disconnect that did not happen, and reporting an architectural refusal as
    /// a network fault is the confusion the parent epic exists to remove. An ordinary end of stream
    /// still writes nothing, because each route's own cancellation arm already owns that case.</para>
    /// </remarks>
    public static async Task StreamAsync<T>(
        HttpContext httpContext,
        IAsyncEnumerable<T> source,
        Func<T, CancellationToken, Task> writeFrameAsync,
        TimeSpan heartbeatInterval,
        GrimoireStreamQuiescence quiescence,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(quiescence);

        // Owned by this writer so a disconnect or a revocation can unwind the producer and let the
        // outstanding MoveNextAsync complete before the enumerator is disposed (see
        // QuiesceAndDisposeAsync). Revocation belongs here and nowhere else.
        //
        // The link is skipped when there is nothing to add to the caller's own token — no revocation
        // to carry and no heartbeat needing a source of its own — because the *identity* of the
        // enumeration token is load-bearing on that path. A compiler-generated iterator combines the
        // token it captured with the one it is enumerated on only when the two differ, so enumerating
        // a source built over `RequestAborted` on a linked token makes a client disconnect's
        // OperationCanceledException carry a combined token instead of `RequestAborted` itself — and
        // ClientDisconnect compares by reference, so the route would stop classifying that disconnect
        // and would write a terminal frame at a socket it has already been told is gone.
        using CancellationTokenSource? producerCts =
            quiescence.Revocation.CanBeCanceled || heartbeatInterval > TimeSpan.Zero
                ? quiescence.LinkProducer(cancellationToken)
                : null;

        CancellationToken producerToken = producerCts?.Token ?? cancellationToken;

        IAsyncEnumerator<T> enumerator = source.GetAsyncEnumerator(producerToken);

        // Keep a single pending MoveNextAsync for the lifetime of each item wait.
        // Heartbeats must WhenAny against the same move task — never start a second
        // MoveNextAsync while one is outstanding (IAsyncEnumerator is not concurrent-safe).
        Task<bool>? pendingMove = null;

        bool clientGone = false;

        try
        {

            while (true)
            {

                // Tested before the move rather than after it, so a revocation that arrived while the
                // previous frame was being written starts no next one — including the very first,
                // which is the path a route falls through on after stopping its own replay.
                if (quiescence.IsQuiescing)
                {

                    break;

                }

                pendingMove ??= enumerator.MoveNextAsync().AsTask();

                if (heartbeatInterval > TimeSpan.Zero)
                {

                    // Per-iteration linked source so a frame that wins the race releases the heartbeat
                    // timer and its registration immediately instead of leaving one TimerQueueTimer per
                    // delivered frame alive for the whole interval (matches the OpenAI /v1 writer).
                    using CancellationTokenSource delayCts =
                        CancellationTokenSource.CreateLinkedTokenSource(producerToken);

                    Task delay = Task.Delay(heartbeatInterval, delayCts.Token);

                    Task completed = await Task.WhenAny(pendingMove, delay).ConfigureAwait(false);

                    if (completed == delay)
                    {

                        // A delay that completed because the producer source was revoked is not a
                        // heartbeat interval elapsing; writing a keep-alive there would start a frame
                        // after the stream was told to stop.
                        if (quiescence.IsQuiescing)
                        {

                            break;

                        }

                        try
                        {

                            await WriteKeepAliveAsync(httpContext, cancellationToken).ConfigureAwait(false);

                        }
                        catch (Exception ex) when (ClientDisconnect.IsClientDisconnect(ex, httpContext))
                        {

                            clientGone = true;

                            break;

                        }

                        continue;

                    }

                    delayCts.Cancel();

                }

                bool hasNext;

                try
                {

                    hasNext = await pendingMove.ConfigureAwait(false);

                }
                catch (OperationCanceledException) when (quiescence.IsQuiescing)
                {

                    // The producer unwound because maintenance revoked it. That is this method's own
                    // signal rather than a fault, and the move it came from has already completed.
                    pendingMove = null;

                    break;

                }

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

                    // W3.4 Group A (S10): client disconnected mid-stream. Stop writing
                    // silently — no error or DONE frame to a dead socket. The caller's
                    // `using`/`finally` disposes the linked CTS, cancelling the producer
                    // (event bus subscription / chronicle pump) promptly.
                    clientGone = true;

                    break;

                }

            }

        }
        finally
        {

            await QuiesceAndDisposeAsync(enumerator, pendingMove, producerCts).ConfigureAwait(false);

        }

        if (quiescence.IsQuiescing && !clientGone)
        {

            await WriteDoneAsync(httpContext).ConfigureAwait(false);

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
