using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// The streaming assistant/reasoning buffer caps transcript growth. DESIGN §16.7 requires every
/// Command Center transcript clamp to be surrogate-safe, so neither the cut applied to the text
/// already buffered nor the cut applied to the incoming chunk may land between the two UTF-16
/// halves of an astral-plane glyph.
/// </summary>
public sealed class BoundedStreamingTextBufferTests
{

    private const string Marker = "\n… [truncated]";

    private static void AssertWellFormed(string text)
    {

        for (int i = 0; i < text.Length; i++)
        {

            if (char.IsHighSurrogate(text[i]))
            {

                Assert.True(
                    i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]),
                    $"lone high surrogate at index {i} of {text.Length}.");

                i++;

                continue;

            }

            Assert.False(
                char.IsLowSurrogate(text[i]),
                $"lone low surrogate at index {i} of {text.Length}.");

        }

    }

    /// <summary>
    /// The buffered text may legitimately grow past the content limit before the cap trips, so the
    /// rewind back to it must not slice an emoji in half.
    /// </summary>
    [Fact]
    public void Rewinding_the_buffered_text_keeps_a_surrogate_pair_whole()
    {

        BoundedStreamingTextBuffer buffer = new(24, Marker);

        // Content limit is 24 - 14 = 10, so the rocket straddles indices 9/10 — exactly the cut.
        buffer.Append(new string('a', 9) + "\U0001F680" + new string('b', 13));

        buffer.Append("c");

        string snapshot = buffer.Snapshot();

        AssertWellFormed(snapshot);

        Assert.EndsWith(Marker, snapshot, StringComparison.Ordinal);

    }

    /// <summary>The slice taken from the arriving chunk has the same obligation.</summary>
    [Fact]
    public void Slicing_the_incoming_chunk_keeps_a_surrogate_pair_whole()
    {

        BoundedStreamingTextBuffer buffer = new(24, Marker);

        buffer.Append(new string('a', 9));

        // Only one code unit of headroom remains, and the chunk opens on a high surrogate.
        buffer.Append("\U0001F680" + new string('b', 20));

        string snapshot = buffer.Snapshot();

        AssertWellFormed(snapshot);

        Assert.EndsWith(Marker, snapshot, StringComparison.Ordinal);

    }

    [Fact]
    public void Plain_text_still_fills_the_content_limit_before_the_marker()
    {

        BoundedStreamingTextBuffer buffer = new(24, Marker);

        buffer.Append(new string('a', 6));

        buffer.Append(new string('b', 40));

        Assert.Equal(new string('a', 6) + new string('b', 4) + Marker, buffer.Snapshot());

    }

    [Fact]
    public void Appends_stop_once_the_buffer_is_truncated()
    {

        BoundedStreamingTextBuffer buffer = new(24, Marker);

        buffer.Append(new string('a', 40));

        string truncated = buffer.Snapshot();

        buffer.Append("more");

        Assert.Equal(truncated, buffer.Snapshot());

    }

}
