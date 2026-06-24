namespace RetroDownfall.Arcanum.Infrastructure.CommLink;

internal static class HttpResponseBodyDrainer
{

    internal const int DefaultMaxBytes = 65_536;

    internal static async Task DrainAsync(HttpContent? content, CancellationToken cancellationToken)
    {

        await DrainAsync(content, DefaultMaxBytes, cancellationToken).ConfigureAwait(false);

    }

    internal static async Task DrainAsync(HttpContent? content, int maxBytes, CancellationToken cancellationToken)
    {

        if (content is null)
        {

            return;

        }

        long? contentLength = content.Headers.ContentLength;

        if (contentLength.HasValue && contentLength.Value > maxBytes)
        {

            return;

        }

        await using Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        byte[] buffer = new byte[4096];

        long totalRead = 0L;

        while (totalRead < maxBytes)
        {

            using CancellationTokenSource readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            readCts.CancelAfter(TimeSpan.FromSeconds(5));

            int read;

            try
            {

                read = await stream
                    .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - totalRead)), readCts.Token)
                    .ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (readCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {

                break;

            }

            if (read <= 0)
            {

                break;

            }

            totalRead += read;

        }

    }

}
