using RetroDownfall.Arcanum.Api.Streaming;

namespace RetroDownfall.Arcanum.Tests.Api.Streaming;

public sealed class LiveEventBufferTests
{
    [Fact]
    public void Writes_within_capacity_drop_nothing()
    {
        LiveEventBuffer<int> buffer = new(capacity: 3);

        Assert.False(buffer.Write(1));
        Assert.False(buffer.Write(2));
        Assert.False(buffer.Write(3));

        Assert.Equal(0, buffer.TakeDropped());
        Assert.Equal([1, 2, 3], Drain(buffer));
    }

    [Fact]
    public void A_write_into_a_full_buffer_discards_the_oldest_and_counts_it()
    {
        LiveEventBuffer<int> buffer = new(capacity: 3);

        for (int i = 1; i <= 5; i++)
        {
            _ = buffer.Write(i);
        }

        Assert.Equal(2, buffer.TakeDropped());
        Assert.Equal([3, 4, 5], Drain(buffer));
    }

    [Fact]
    public void Only_the_first_discard_of_an_episode_asks_to_be_reported_until_the_count_is_taken()
    {
        LiveEventBuffer<int> buffer = new(capacity: 2);

        _ = buffer.Write(1);
        _ = buffer.Write(2);

        Assert.True(buffer.Write(3));
        Assert.False(buffer.Write(4));
        Assert.False(buffer.Write(5));

        Assert.Equal(3, buffer.TakeDropped());
        Assert.Equal(0, buffer.TakeDropped());

        // The count was taken, so the next discard opens a new episode.
        Assert.True(buffer.Write(6));
        Assert.Equal(1, buffer.TakeDropped());
    }

    [Fact]
    public void Completing_the_buffer_ends_the_reader_after_the_retained_items()
    {
        LiveEventBuffer<int> buffer = new(capacity: 2);

        _ = buffer.Write(1);

        buffer.Complete();

        Assert.False(buffer.Write(2));

        Assert.Equal([1], Drain(buffer));
        Assert.True(buffer.Reader.Completion.IsCompleted);
    }

    private static List<int> Drain(LiveEventBuffer<int> buffer)
    {
        List<int> items = [];

        while (buffer.Reader.TryRead(out int item))
        {
            items.Add(item);
        }

        return items;
    }
}
