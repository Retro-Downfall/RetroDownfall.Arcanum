using System.Net;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Bounds how much of one JSON-RPC message a Streamable HTTP MCP server can make Arcanum read. The
/// official SDK transport deserializes and parses a response body, or an SSE event, whole, so without
/// this guard a hostile remote server could stream an arbitrarily large message into memory. The
/// in-process transport already enforces <c>MaxJsonRpcLineBytes</c> per frame; this applies the same
/// bound, plus <see cref="FramingAllowanceBytes"/> for SSE field framing, to the HTTP transport.
/// <para>
/// A <c>text/event-stream</c> response is long-lived and carries many messages, so it is bounded per
/// event (the bytes between blank lines), never in total. Any other response is one message and is
/// bounded in total, up front when it declares a <c>Content-Length</c> and while streaming otherwise.
/// An oversized message is never buffered. One whose declared length is over the bound fails the request
/// with an <see cref="HttpRequestException"/> before any body byte is read; any other fails the read with
/// an <see cref="IOException"/>. That costs the call (or SSE stream) it arrived on, not the session: the
/// SDK abandons an oversized server-to-client stream after its bounded reconnection attempts.
/// </para>
/// </summary>
internal sealed class McpHttpResponseBoundHandler(long maxFrameBytes) : DelegatingHandler
{
    /// <summary>
    /// Allowance added to the frame bound for the SSE <c>data:</c>/<c>event:</c>/<c>id:</c> field names
    /// and line terminators that surround a JSON-RPC message inside an event.
    /// </summary>
    internal const long FramingAllowanceBytes = 4_096L;

    private readonly long _boundBytes = maxFrameBytes > 0L
        ? maxFrameBytes + FramingAllowanceBytes
        : throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));

    /// <summary>The largest message (or SSE event) this handler lets through, framing allowance included.</summary>
    internal long BoundBytes => _boundBytes;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        HttpContent original = response.Content;

        bool eventStream = string.Equals(
            original.Headers.ContentType?.MediaType,
            "text/event-stream",
            StringComparison.OrdinalIgnoreCase);

        if (!eventStream && original.Headers.ContentLength is { } declared && declared > _boundBytes)
        {
            response.Dispose();

            throw new HttpRequestException(OversizedMessage(declared));
        }

        response.Content = new BoundedContent(original, _boundBytes, eventStream);

        return response;
    }

    private static string OversizedMessage(long observedBytes) =>
        $"The MCP server's HTTP response ({observedBytes} bytes) exceeds the JSON-RPC frame bound.";

    private sealed class BoundedContent : HttpContent
    {
        private readonly HttpContent _original;

        private readonly long _boundBytes;

        private readonly bool _eventStream;

        public BoundedContent(HttpContent original, long boundBytes, bool eventStream)
        {
            _original = original;

            _boundBytes = boundBytes;

            _eventStream = eventStream;

            foreach (KeyValuePair<string, IEnumerable<string>> header in original.Headers)
            {
                _ = Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new BoundedReadStream(
                await _original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                _boundBytes,
                _eventStream);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            await using Stream source = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);

            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            if (_original.Headers.ContentLength is { } declared)
            {
                length = declared;

                return true;
            }

            length = 0L;

            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _original.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class BoundedReadStream(Stream inner, long boundBytes, bool eventStream) : Stream
    {
        // Bytes of the message being read: the whole body, or the current SSE event.
        private long _messageBytes;

        // Bytes of the current SSE line, so a blank line (an event boundary) can be recognised.
        private long _lineBytes;

        private bool _previousWasCarriageReturn;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Never ask the inner stream for more than one byte past the bound, so an oversized message
            // is detected having read at most that one byte too many rather than a whole caller buffer.
            Memory<byte> window = buffer[..AllowedReadLength(buffer.Length)];

            int read = await inner.ReadAsync(window, cancellationToken).ConfigureAwait(false);

            Account(window.Span[..read]);

            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            Span<byte> window = buffer[..AllowedReadLength(buffer.Length)];

            int read = inner.Read(window);

            Account(window[..read]);

            return read;
        }

        private int AllowedReadLength(int requested) =>
            (int)Math.Min(requested, boundBytes - _messageBytes + 1L);

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask DisposeAsync() => inner.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Account(ReadOnlySpan<byte> bytes)
        {
            if (!eventStream)
            {
                _messageBytes += bytes.Length;

                if (_messageBytes > boundBytes)
                {
                    throw new IOException(OversizedMessage(_messageBytes));
                }

                return;
            }

            foreach (byte value in bytes)
            {
                _messageBytes++;

                if (_messageBytes > boundBytes)
                {
                    throw new IOException(OversizedMessage(_messageBytes));
                }

                // CR, LF and CRLF each end a line; the LF of a CRLF pair is the same line ending.
                if (value == (byte)'\n' && _previousWasCarriageReturn)
                {
                    _previousWasCarriageReturn = false;

                    continue;
                }

                _previousWasCarriageReturn = value == (byte)'\r';

                if (value is (byte)'\r' or (byte)'\n')
                {
                    if (_lineBytes == 0L)
                    {
                        // A blank line ends the event; the next one starts a fresh budget.
                        _messageBytes = 0L;
                    }

                    _lineBytes = 0L;
                }
                else
                {
                    _lineBytes++;
                }
            }
        }
    }
}
