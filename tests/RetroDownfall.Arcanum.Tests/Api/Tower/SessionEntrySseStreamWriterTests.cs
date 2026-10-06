using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// One writer, one buffer, for the life of a session stream connection.
/// </summary>
public sealed class SessionEntrySseStreamWriterTests
{
    [Fact]
    public async Task Each_entry_is_one_data_frame_of_its_EntryDto_json()
    {
        DefaultHttpContext context = new();

        MemoryStream body = new();

        context.Response.Body = body;

        SessionEntrySseStreamWriter writer = new(context);

        Entry first = CreateEntry("first", 1);

        Entry second = CreateEntry(new string('x', 4096), 2);

        Entry third = CreateEntry("third", 3);

        await writer.WriteEntryAsync(first, CancellationToken.None);

        await writer.WriteEntryAsync(second, CancellationToken.None);

        await writer.WriteEntryAsync(third, CancellationToken.None);

        string text = Encoding.UTF8.GetString(body.ToArray());

        string[] frames = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, frames.Length);

        Entry[] entries = [first, second, third];

        for (int index = 0; index < frames.Length; index++)
        {
            Assert.StartsWith("data: ", frames[index], StringComparison.Ordinal);

            Assert.Equal(
                JsonSerializer.Serialize(SessionMapping.ToEntryDto(entries[index]), ArcanumJsonContext.Default.EntryDto),
                frames[index]["data: ".Length..]);
        }
    }

    /// <summary>
    /// One unusually large entry does not leave its buffer behind for the rest of an hours-long connection.
    /// </summary>
    /// <remarks>
    /// The buffer the large entry grew has to become unreachable while the connection sits idle, before any
    /// further entry: keep-alives go straight to the response, not through this writer, so an idle session
    /// never gives a later frame the chance to let go of it. Once the write has returned, nothing but the
    /// writer's own fields can reach that buffer, so a collection that leaves it alive means one of them
    /// still holds it.
    /// </remarks>
    [Fact]
    public async Task A_large_entry_does_not_pin_its_buffer_for_the_life_of_the_connection()
    {
        DefaultHttpContext context = new();

        MemoryStream body = new();

        context.Response.Body = body;

        SessionEntrySseStreamWriter writer = new(context);

        WeakReference bufferTheLargeEntryGrows = WeakReferenceToRetainedBuffer(writer);

        await writer.WriteEntryAsync(CreateEntry(new string('x', 4 * 1024 * 1024), 1), CancellationToken.None);

        Assert.True(
            writer.RetainedBuffer.Capacity <= SessionEntrySseStreamWriter.MaxRetainedBufferBytes,
            $"The writer kept a {writer.RetainedBuffer.Capacity}-byte buffer after a large entry.");

        GC.Collect();

        GC.WaitForPendingFinalizers();

        GC.Collect();

        Assert.False(
            bufferTheLargeEntryGrows.IsAlive,
            "The buffer the large entry grew is still reachable from the idle connection's writer.");

        Entry small = CreateEntry("small", 2);

        await writer.WriteEntryAsync(small, CancellationToken.None);

        string[] frames = Encoding.UTF8.GetString(body.ToArray()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, frames.Length);

        Assert.Equal(
            JsonSerializer.Serialize(SessionMapping.ToEntryDto(small), ArcanumJsonContext.Default.EntryDto),
            frames[1]["data: ".Length..]);

        Assert.True(writer.RetainedBuffer.Capacity <= SessionEntrySseStreamWriter.MaxRetainedBufferBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WeakReferenceToRetainedBuffer(SessionEntrySseStreamWriter writer) =>
        new(writer.RetainedBuffer);

    private static Entry CreateEntry(string content, int sequence) =>
        new()
        {
            SessionId = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = content,
            CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            Sequence = sequence,
        };
}
