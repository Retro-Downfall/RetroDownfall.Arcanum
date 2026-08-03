using System.Buffers;

using System.Runtime.CompilerServices;

using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

internal sealed record ReverseJsonlRecord<T>(
    T Value,
    long NextBoundaryOffset);

/// <summary>
/// Scans JSONL record boundaries from the end of a seekable stream with a fixed-size buffer. Each
/// record is deserialized through a bounded stream segment, so the surrounding file is never
/// materialized as a string array.
/// </summary>
internal static class ReverseJsonlFileReader
{

    internal const int ScanBufferBytes = 64 * 1024;

    internal static async IAsyncEnumerable<ReverseJsonlRecord<T>> ReadAsync<T>(
        Stream stream,
        long exclusiveEndOffset,
        JsonTypeInfo<T> jsonTypeInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(stream);

        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        if (!stream.CanRead || !stream.CanSeek)
        {

            throw new ArgumentException("Reverse JSONL reading requires a readable, seekable stream.", nameof(stream));

        }

        long scanPosition = Math.Min(Math.Max(0, exclusiveEndOffset), stream.Length);

        long lineEnd = scanPosition;

        byte[] scanBuffer = ArrayPool<byte>.Shared.Rent(ScanBufferBytes);

        try
        {

            while (scanPosition > 0)
            {

                cancellationToken.ThrowIfCancellationRequested();

                long blockStart = Math.Max(0, scanPosition - ScanBufferBytes);

                int blockLength = checked((int)(scanPosition - blockStart));

                stream.Position = blockStart;

                await stream
                    .ReadExactlyAsync(scanBuffer.AsMemory(0, blockLength), cancellationToken)
                    .ConfigureAwait(false);

                for (int index = blockLength - 1; index >= 0; index--)
                {

                    if (scanBuffer[index] != (byte)'\n')
                    {

                        continue;

                    }

                    long newlineOffset = blockStart + index;

                    if (newlineOffset + 1 < lineEnd)
                    {

                        T? value = await DeserializeRangeAsync(
                            stream,
                            newlineOffset + 1,
                            lineEnd,
                            jsonTypeInfo,
                            cancellationToken).ConfigureAwait(false);

                        if (value is not null)
                        {

                            yield return new ReverseJsonlRecord<T>(value, newlineOffset + 1);

                        }

                    }

                    lineEnd = newlineOffset;

                }

                scanPosition = blockStart;

            }

            if (lineEnd > 0)
            {

                T? value = await DeserializeRangeAsync(
                    stream,
                    0,
                    lineEnd,
                    jsonTypeInfo,
                    cancellationToken).ConfigureAwait(false);

                if (value is not null)
                {

                    yield return new ReverseJsonlRecord<T>(value, 0);

                }

            }

        }
        finally
        {

            ArrayPool<byte>.Shared.Return(scanBuffer);

        }

    }

    private static async Task<T?> DeserializeRangeAsync<T>(
        Stream stream,
        long startOffset,
        long endOffset,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {

        long length = endOffset - startOffset;

        if (length <= 0)
        {

            return default;

        }

        stream.Position = startOffset;

        using Stream segment = new ReadSegmentStream(stream, length);

        try
        {

            return await JsonSerializer
                .DeserializeAsync(segment, jsonTypeInfo, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (JsonException)
        {

            return default;

        }

    }

    private sealed class ReadSegmentStream : Stream
    {

        private readonly Stream _inner;

        private readonly long _length;

        private long _remaining;

        internal ReadSegmentStream(
            Stream inner,
            long length)
        {

            _inner = inner;

            _length = length;

            _remaining = length;

        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {

            get => _length - _remaining;

            set => throw new NotSupportedException();

        }

        public override void Flush()
        {

        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {

            int allowed = (int)Math.Min(count, _remaining);

            int read = allowed == 0
                ? 0
                : _inner.Read(buffer, offset, allowed);

            _remaining -= read;

            return read;

        }

        public override int Read(Span<byte> buffer)
        {

            int allowed = (int)Math.Min(buffer.Length, _remaining);

            int read = allowed == 0
                ? 0
                : _inner.Read(buffer[..allowed]);

            _remaining -= read;

            return read;

        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {

            int allowed = (int)Math.Min(buffer.Length, _remaining);

            int read = allowed == 0
                ? 0
                : await _inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);

            _remaining -= read;

            return read;

        }

        public override long Seek(
            long offset,
            SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) => throw new NotSupportedException();

    }

}
