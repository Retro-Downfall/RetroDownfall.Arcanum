using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Api.TheForge;

/// <summary>
/// Reuses one buffer per apprentice chronicle SSE connection.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: HTTP SSE streaming glue; exercised via apprentice chronicle integration routes.
internal sealed class ChronicleSseStreamWriter(HttpContext httpContext)
{

    private static readonly byte[] SseDataPrefix = "data: "u8.ToArray();

    private static readonly byte[] SseLineBreak = "\n\n"u8.ToArray();

    private readonly ArrayBufferWriter<byte> _buffer = new(1024);

    public async Task WriteEventAsync(ApprenticeEvent @event, CancellationToken cancellationToken)
    {

        _buffer.Clear();

        _buffer.Write(SseDataPrefix);

        await using (Utf8JsonWriter writer = new(_buffer, new JsonWriterOptions { Indented = false }))
        {

            if (@event.WizardEvent is IntelligenceEvent wizard)
            {

                ChronicleSseWriter.WritePassThroughEvent(writer, @event, wizard);

            }
            else
            {

                ChronicleSseWriter.WriteLifecycleEvent(writer, @event);

            }

        }

        _buffer.Write(SseLineBreak);

        await httpContext.Response.Body.WriteAsync(_buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

        await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

    }

}
