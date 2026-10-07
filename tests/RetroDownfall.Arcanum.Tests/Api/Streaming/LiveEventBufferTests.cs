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

        Assert.Equal([(1, 0L), (2, 0L), (3, 0L)], DrainWithCounts(buffer));
    }

    [Fact]
    public void A_write_into_a_full_buffer_discards_the_oldest_and_counts_it_in_front_of_the_next_read()
    {
        LiveEventBuffer<int> buffer = new(capacity: 3);

        for (int i = 1; i <= 5; i++)
        {
            _ = buffer.Write(i);
        }

        Assert.Equal([(3, 2L), (4, 0L), (5, 0L)], DrainWithCounts(buffer));
    }

    [Fact]
    public void Only_the_first_discard_of_an_episode_asks_to_be_reported_until_a_read_takes_the_count()
    {
        LiveEventBuffer<int> buffer = new(capacity: 2);

        _ = buffer.Write(1);
        _ = buffer.Write(2);

        Assert.True(buffer.Write(3));
        Assert.False(buffer.Write(4));
        Assert.False(buffer.Write(5));

        Assert.True(buffer.Reader.TryRead(out int first));
        Assert.Equal(4, first);
        Assert.Equal(3, buffer.TakeDropped());
        Assert.Equal(0, buffer.TakeDropped());

        // The read took the count, so the next discard opens a new episode.
        Assert.False(buffer.Write(6));
        Assert.True(buffer.Write(7));

        Assert.True(buffer.Reader.TryRead(out int next));
        Assert.Equal(6, next);
        Assert.Equal(1, buffer.TakeDropped());
    }

    /// <summary>
    /// A discard made while the reader still holds the item it last read belongs in front of the next item,
    /// because that is where the gap is; reporting it in front of the held item put the marker one frame early.
    /// </summary>
    [Fact]
    public void A_discard_after_a_read_is_reported_in_front_of_the_next_item_not_the_one_already_read()
    {
        LiveEventBuffer<int> buffer = new(capacity: 2);

        _ = buffer.Write(1);
        _ = buffer.Write(2);

        Assert.True(buffer.Reader.TryRead(out int held));
        Assert.Equal(1, held);

        _ = buffer.Write(3);
        _ = buffer.Write(4);

        // Item 1 is still being written: nothing was lost in front of it.
        Assert.Equal(0, buffer.TakeDropped());

        Assert.True(buffer.Reader.TryRead(out int next));
        Assert.Equal(3, next);
        Assert.Equal(1, buffer.TakeDropped());
    }

    /// <summary>
    /// The session stream's buffered drain reads an Entry it already replayed and skips it without writing a
    /// frame, so it never takes the count moved to that read. The next read must not overwrite the count
    /// still waiting to be taken: the entries lost in that gap were never reported, and the Warning that
    /// carries the count is the only place the operator learns how many a client missed.
    /// </summary>
    [Fact]
    public void A_count_not_taken_before_the_next_read_is_carried_to_that_read_rather_than_lost()
    {
        LiveEventBuffer<int> buffer = new(capacity: 2);

        _ = buffer.Write(1);
        _ = buffer.Write(2);

        Assert.True(buffer.Write(3));
        Assert.False(buffer.Write(4));

        // Items 1 and 2 were discarded in front of 3; the reader skips 3 without taking the count.
        Assert.True(buffer.Reader.TryRead(out int skipped));
        Assert.Equal(3, skipped);

        Assert.True(buffer.Reader.TryRead(out int next));
        Assert.Equal(4, next);
        Assert.Equal(2, buffer.TakeDropped());
        Assert.Equal(0, buffer.TakeDropped());

        // Moving the count ended the episode at the first read, so the next discard is reported again.
        _ = buffer.Write(5);
        _ = buffer.Write(6);
        Assert.True(buffer.Write(7));
    }

    /// <summary>
    /// Under a writer and a reader racing each other, every gap in what the reader sees is exactly the count
    /// handed to it with the item after the gap, nothing is lost without being counted, and an episode is
    /// reported once.
    /// </summary>
    [Fact]
    public async Task A_racing_writer_and_reader_see_every_gap_counted_exactly_where_it_is()
    {
        const int total = 200_000;

        LiveEventBuffer<int> buffer = new(capacity: 4);

        int warnings = 0;

        Task writer = Task.Run(() =>
        {
            for (int i = 1; i <= total; i++)
            {
                if (buffer.Write(i))
                {
                    warnings++;
                }
            }

            buffer.Complete();
        });

        int previous = 0;

        long delivered = 0;

        long droppedSeen = 0;

        long gapsReported = 0;

        await foreach (int item in buffer.Reader.ReadAllAsync())
        {
            long dropped = buffer.TakeDropped();

            Assert.Equal(item - previous - 1, dropped);

            if (dropped > 0)
            {
                gapsReported++;
            }

            droppedSeen += dropped;

            delivered++;

            previous = item;
        }

        await writer;

        Assert.Equal(total, previous);

        Assert.Equal(total, delivered + droppedSeen);

        Assert.True(warnings <= gapsReported, $"{warnings} episodes were reported for {gapsReported} gaps.");
    }

    [Fact]
    public void Completing_the_buffer_ends_the_reader_after_the_retained_items()
    {
        LiveEventBuffer<int> buffer = new(capacity: 2);

        _ = buffer.Write(1);

        buffer.Complete();

        Assert.False(buffer.Write(2));

        Assert.Equal([(1, 0L)], DrainWithCounts(buffer));
        Assert.True(buffer.Reader.Completion.IsCompleted);
    }

    private static List<(int Item, long DroppedBefore)> DrainWithCounts(LiveEventBuffer<int> buffer)
    {
        List<(int, long)> items = [];

        while (buffer.Reader.TryRead(out int item))
        {
            items.Add((item, buffer.TakeDropped()));
        }

        return items;
    }
}
