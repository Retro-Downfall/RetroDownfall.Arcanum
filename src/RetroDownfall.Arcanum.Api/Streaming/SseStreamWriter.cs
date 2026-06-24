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

        httpContext.Response.Headers.CacheControl = "no-cache";

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

                await writeFrameAsync(item, cancellationToken).ConfigureAwait(false);

            }

            return;

        }

        await using IAsyncEnumerator<T> enumerator = source.GetAsyncEnumerator(cancellationToken);

        while (true)
        {

            Task<bool> move = enumerator.MoveNextAsync().AsTask();

            Task delay = Task.Delay(heartbeatInterval, cancellationToken);

            Task completed = await Task.WhenAny(move, delay).ConfigureAwait(false);

            if (completed == delay)
            {

                await WriteKeepAliveAsync(httpContext, cancellationToken).ConfigureAwait(false);

                continue;

            }

            if (!await move.ConfigureAwait(false))
            {

                break;

            }

            await writeFrameAsync(enumerator.Current, cancellationToken).ConfigureAwait(false);

        }

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
