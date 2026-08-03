using System.Buffers;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

internal static class WebResearchBounds
{
    public static string TruncateUtf8(
        string? value,
        int maxBytes,
        out bool truncated,
        string marker = WebResearchConstants.TruncationMarker)
    {
        value ??= string.Empty;

        if (maxBytes <= 0)
        {
            truncated = value.Length > 0;
            return string.Empty;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);

        if (byteCount <= maxBytes)
        {
            truncated = false;
            return value;
        }

        truncated = true;

        int markerBytes = Encoding.UTF8.GetByteCount(marker);
        int contentBudget = markerBytes < maxBytes ? maxBytes - markerBytes : maxBytes;
        int chars = FindUtf8PrefixLength(value, contentBudget);
        string prefix = value[..chars];

        return markerBytes < maxBytes ? prefix + marker : prefix;
    }

    public static string TruncateUtf8(string? value, int maxBytes) =>
        TruncateUtf8(value, maxBytes, out _);

    public static async Task<CappedBody> ReadCappedAsync(
        Stream stream,
        int maxBytes,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            idleTimeout,
            TimeSpan.Zero);

        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Min(16 * 1024, maxBytes + 1));

        try
        {
            using MemoryStream destination = new(Math.Min(maxBytes, 64 * 1024));
            int total = 0;

            while (total <= maxBytes)
            {
                int available = Math.Min(rented.Length, maxBytes + 1 - total);
                int read = await ReadWithIdleTimeoutAsync(
                        stream,
                        rented.AsMemory(0, available),
                        idleTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    return new CappedBody(destination.ToArray(), false);
                }

                int accepted = Math.Min(read, maxBytes - total);

                if (accepted > 0)
                {
                    destination.Write(rented, 0, accepted);
                    total += accepted;
                }

                if (accepted < read || total == maxBytes)
                {
                    int probe = accepted < read
                        ? 1
                        : await ReadWithIdleTimeoutAsync(
                                stream,
                                rented.AsMemory(0, 1),
                                idleTimeout,
                                cancellationToken)
                            .ConfigureAwait(false);

                    return new CappedBody(destination.ToArray(), probe > 0);
                }
            }

            return new CappedBody(destination.ToArray(), true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async ValueTask<int> ReadWithIdleTimeoutAsync(
        Stream stream,
        Memory<byte> buffer,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource idle =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        idle.CancelAfter(idleTimeout);

        return await stream
            .ReadAsync(buffer, idle.Token)
            .ConfigureAwait(false);
    }

    private static int FindUtf8PrefixLength(string value, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return 0;
        }

        int low = 0;
        int high = value.Length;

        while (low < high)
        {
            int candidate = low + ((high - low + 1) / 2);
            int bytes = Encoding.UTF8.GetByteCount(value.AsSpan(0, candidate));

            if (bytes <= maxBytes)
            {
                low = candidate;
            }
            else
            {
                high = candidate - 1;
            }
        }

        if (low < value.Length
            && low > 0
            && char.IsHighSurrogate(value[low - 1])
            && char.IsLowSurrogate(value[low]))
        {
            low--;
        }

        return low;
    }

    internal readonly record struct CappedBody(byte[] Bytes, bool Truncated);
}
