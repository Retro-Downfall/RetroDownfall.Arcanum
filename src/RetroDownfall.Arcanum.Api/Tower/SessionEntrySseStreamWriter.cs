using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Api.Tower;

/// <summary>
/// Reuses one buffer and one JSON writer for every frame of one session stream connection.
/// </summary>
/// <remarks>
/// The route used to build a new buffer and a new <see cref="Utf8JsonWriter"/> for each entry it wrote, which on
/// a busy session is an allocation per frame on a connection that lives for hours. A connection writes its
/// frames one at a time, so one of each is enough, as on the Chronicle stream.
/// </remarks>
internal sealed class SessionEntrySseStreamWriter(HttpContext httpContext)
{
    private const int InitialBufferBytes = 1024;

    /// <summary>
    /// The most buffer a connection keeps between frames. A buffer grows to fit the largest frame it has
    /// written, so one large entry would otherwise stay allocated for the rest of a connection that can live
    /// for hours; a frame past this size gets its buffer and is then let go.
    /// </summary>
    internal const int MaxRetainedBufferBytes = 64 * 1024;

    private static readonly byte[] SseDataPrefix = "data: "u8.ToArray();

    private static readonly byte[] SseLineBreak = "\n\n"u8.ToArray();

    private ArrayBufferWriter<byte> _buffer = new(InitialBufferBytes);

    private Utf8JsonWriter? _jsonWriter;

    /// <summary>
    /// The buffer this connection holds between frames.
    /// </summary>
    internal ArrayBufferWriter<byte> RetainedBuffer => _buffer;

    public async Task WriteEntryAsync(Entry entry, CancellationToken cancellationToken)
    {
        EntryDto dto = SessionMapping.ToEntryDto(entry);

        _buffer.Clear();

        _buffer.Write(SseDataPrefix);

        if (_jsonWriter is null)
        {
            _jsonWriter = new Utf8JsonWriter(_buffer);
        }
        else
        {
            _jsonWriter.Reset(_buffer);
        }

        JsonSerializer.Serialize(_jsonWriter, dto, ArcanumJsonContext.Default.EntryDto);

        _jsonWriter.Flush();

        _buffer.Write(SseLineBreak);

        try
        {
            await httpContext.Response.Body.WriteAsync(_buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

            await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_buffer.Capacity > MaxRetainedBufferBytes)
            {
                _buffer = new ArrayBufferWriter<byte>(InitialBufferBytes);

                // A Utf8JsonWriter keeps the buffer it writes to after Flush and lets go of it only on Reset or
                // Dispose. Waiting for the next frame's Reset would leave the large buffer reachable for as long
                // as the session stays idle, and keep-alives do not come through this writer, so it is
                // re-pointed now.
                _jsonWriter?.Reset(_buffer);
            }
        }
    }
}
