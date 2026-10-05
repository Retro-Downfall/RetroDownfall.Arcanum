using System.Buffers;
using System.Diagnostics.CodeAnalysis;
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
[ExcludeFromCodeCoverage] // Reason: HTTP SSE streaming glue; exercised via the session stream integration routes.
internal sealed class SessionEntrySseStreamWriter(HttpContext httpContext)
{
    private static readonly byte[] SseDataPrefix = "data: "u8.ToArray();

    private static readonly byte[] SseLineBreak = "\n\n"u8.ToArray();

    private readonly ArrayBufferWriter<byte> _buffer = new(1024);

    private Utf8JsonWriter? _jsonWriter;

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

        await httpContext.Response.Body.WriteAsync(_buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

        await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
