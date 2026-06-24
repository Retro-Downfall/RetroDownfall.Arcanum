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

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        byte[] buffer = new byte[4096];

        long totalRead = 0L;

        while (true)
        {

            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {

                break;

            }

            totalRead += read;

            if (totalRead >= maxBytes)
            {

                break;

            }

        }

    }

}

