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
