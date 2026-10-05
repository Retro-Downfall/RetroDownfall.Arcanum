using System.Text;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Pins the reader the CLI's line-framed streams decode through: it hands characters over as soon as
/// one stream read produced any, decodes a character split across reads, and keeps the stream's
/// ownership with the caller.
/// </summary>
public sealed class StreamingTextReaderTests
{
    private const string Emoji = "\U0001F600";

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// The read that fills the byte buffer exactly is the one a <see cref="StreamReader"/> goes back to
    /// the stream after; this reader answers from what that read produced, whatever the buffer length.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(StreamingTextReader.BufferSize)]
    [InlineData(StreamingTextReader.BufferSize * 2)]
    public async Task A_read_returns_what_the_stream_delivered_without_waiting_for_more(int length)
    {
        using QuietAfterBytesStream stream = new(Encoding.UTF8.GetBytes(new string('a', length)));

        using StreamingTextReader reader = new(stream, Encoding.UTF8);

        char[] buffer = new char[StreamingTextReader.BufferSize * 4];

        int total = 0;

        while (total < length)
        {
            int read = await reader
                .ReadAsync(buffer.AsMemory(total), CancellationToken.None)
                .AsTask()
                .WaitAsync(HangGuard);

            Assert.True(read > 0);

            total += read;
        }

        Assert.Equal(length, total);
    }

    [Fact]
    public async Task A_four_byte_character_split_across_reads_is_decoded_whole()
    {
        using OneByteReadStream stream = new(Encoding.UTF8.GetBytes("a" + Emoji + "b"));

        using StreamingTextReader reader = new(stream, Encoding.UTF8);

        Assert.Equal("a" + Emoji + "b", await ReadToEndAsync(reader));

        Assert.Equal(1, stream.LargestRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_leading_utf8_byte_order_mark_is_dropped_even_when_it_is_split_across_reads(bool oneByteAtATime)
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. "ok"u8];

        using Stream stream = oneByteAtATime
            ? new OneByteReadStream(bytes)
            : new MemoryStream(bytes);

        using StreamingTextReader reader = new(stream, Encoding.UTF8);

        Assert.Equal("ok", await ReadToEndAsync(reader));
    }

    [Fact]
    public async Task A_byte_order_mark_that_is_not_at_the_start_is_kept()
    {
        using MemoryStream stream = new(
            [.. "a"u8, .. Encoding.UTF8.GetPreamble(), .. "b"u8]);

        using StreamingTextReader reader = new(stream, Encoding.UTF8);

        Assert.Equal("a﻿b", await ReadToEndAsync(reader));
    }

    /// <summary>
    /// A caller may hand over a buffer smaller than what one stream read decoded to; the rest waits for
    /// the next call and nothing is lost or repeated.
    /// </summary>
    [Fact]
    public async Task A_small_buffer_receives_the_decoded_characters_in_order_across_calls()
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("abc" + Emoji + "def"));

        using StreamingTextReader reader = new(stream, Encoding.UTF8);

        char[] buffer = new char[2];

        StringBuilder text = new();

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None);

            if (read == 0)
            {
                break;
            }

            Assert.InRange(read, 1, 2);

            text.Append(buffer, 0, read);
        }

        Assert.Equal("abc" + Emoji + "def", text.ToString());
    }

    [Fact]
    public async Task An_invalid_byte_follows_the_encodings_fallback()
    {
        byte[] bytes = [.. "a"u8, 0xFF, .. "b"u8];

        using (MemoryStream replaced = new(bytes))
        using (StreamingTextReader reader = new(replaced, Encoding.UTF8))
        {
            Assert.Equal("a�b", await ReadToEndAsync(reader));
        }

        using MemoryStream strict = new(bytes);

        using StreamingTextReader strictReader = new(strict, StrictUtf8);

        _ = await Assert.ThrowsAsync<DecoderFallbackException>(
            () => ReadToEndAsync(strictReader));
    }

    /// <summary>
    /// A stream that ends inside a character ends: the incomplete tail is dropped rather than raised,
    /// because the framing layer reports a stream that stops mid-line as a disconnect on its own.
    /// </summary>
    [Fact]
    public async Task A_stream_that_ends_inside_a_character_drops_the_incomplete_tail()
    {
        byte[] emoji = Encoding.UTF8.GetBytes(Emoji);

        using MemoryStream stream = new([.. "ab"u8, .. emoji[..2]]);

        using StreamingTextReader reader = new(stream, StrictUtf8);

        Assert.Equal("ab", await ReadToEndAsync(reader));
    }

    [Fact]
    public async Task A_pending_read_is_cancelled_with_its_token()
    {
        using QuietAfterBytesStream stream = new([]);

        using StreamingTextReader reader = new(stream, Encoding.UTF8);

        using CancellationTokenSource cancellation = new();

        char[] buffer = new char[16];

        Task<int> pending = reader
            .ReadAsync(buffer.AsMemory(), cancellation.Token)
            .AsTask();

        Assert.False(pending.IsCompleted);

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(HangGuard));
    }

    [Fact]
    public async Task Disposing_the_reader_leaves_the_stream_open_for_its_owner()
    {
        using MemoryStream stream = new("x"u8.ToArray());

        using (StreamingTextReader reader = new(stream, Encoding.UTF8))
        {
            Assert.Equal("x", await ReadToEndAsync(reader));
        }

        Assert.True(stream.CanRead);
    }

    [Fact]
    public void A_synchronous_read_is_refused()
    {
        using MemoryStream stream = new("x"u8.ToArray());

        using StreamingTextReader reader = new(stream, Encoding.UTF8);

        _ = Assert.Throws<NotSupportedException>(() => reader.Read());

        _ = Assert.Throws<NotSupportedException>(() => reader.Peek());

        _ = Assert.Throws<NotSupportedException>(() => reader.Read(new char[1], 0, 1));

        _ = Assert.Throws<NotSupportedException>(() => reader.Read(new char[1].AsSpan()));
    }

    private static async Task<string> ReadToEndAsync(StreamingTextReader reader)
    {
        StringBuilder text = new();

        char[] buffer = new char[256];

        while (true)
        {
            int read = await reader
                .ReadAsync(buffer.AsMemory(), CancellationToken.None)
                .AsTask()
                .WaitAsync(HangGuard);

            if (read == 0)
            {
                return text.ToString();
            }

            text.Append(buffer, 0, read);
        }
    }
}
