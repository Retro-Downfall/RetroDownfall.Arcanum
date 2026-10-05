using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Logging;

public sealed class InMemoryLogRingBufferTests
{
    [Fact]
    public void Write_and_GetSnapshot_preserve_insertion_order()
    {
        InMemoryLogRingBuffer buffer = new();

        buffer.Write(MakeEntry("first"));

        buffer.Write(MakeEntry("second"));

        IReadOnlyList<LogEntry> snapshot = buffer.GetSnapshot();

        Assert.Equal(2, snapshot.Count);

        Assert.Equal("first", snapshot[0].Message);

        Assert.Equal("second", snapshot[1].Message);
    }

    [Fact]
    public void Write_evicts_oldest_when_capacity_exceeded()
    {
        int capacity = ArcanumSettingClamps.LogRingBufferCapacity(
            ArcanumRuntimeDefaults.Logs.RingBufferCapacity);
        InMemoryLogRingBuffer buffer = new();

        for (int i = 0; i < capacity; i++)
        {
            buffer.Write(MakeEntry($"msg-{i}"));
        }

        buffer.Write(MakeEntry("overflow"));

        IReadOnlyList<LogEntry> snapshot = buffer.GetSnapshot();

        Assert.Equal(capacity, snapshot.Count);

        Assert.Equal("msg-1", snapshot[0].Message);

        Assert.Equal("overflow", snapshot[^1].Message);
    }

    [Fact]
    public async Task StreamAsync_receives_new_entries()
    {
        InMemoryLogRingBuffer buffer = new();

        using CancellationTokenSource cts = new();

        Task<LogEntry?> readTask = ReadOneAsync(buffer, cts.Token);

        buffer.Write(MakeEntry("streamed"));

        LogEntry? received = await readTask;

        Assert.NotNull(received);

        Assert.Equal("streamed", received!.Message);
    }

    [Fact]
    public async Task Write_ConcurrentWriters_SnapshotAndStreamAreSequenceOrdered()
    {
        const int writerCount = 8;

        const int writesPerWriter = 5_000;

        InMemoryLogRingBuffer buffer = new();

        List<long> streamed = [];

        TaskCompletionSource subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task consumer = Task.Run(async () =>
        {
            await foreach (LogEntry item in buffer.StreamAsync(CancellationToken.None))
            {
                if (item.Message == "probe")
                {
                    subscribed.TrySetResult();

                    continue;
                }

                if (item.Message == "end")
                {
                    return;
                }

                streamed.Add(item.Sequence);
            }
        });

        while (!subscribed.Task.IsCompleted)
        {
            buffer.Write(MakeEntry("probe"));

            await Task.Delay(1);
        }

        using Barrier start = new(writerCount);

        Task[] writers = [.. Enumerable.Range(0, writerCount).Select(_ => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait();

                for (int i = 0; i < writesPerWriter; i++)
                {
                    buffer.Write(MakeEntry("work"));
                }
            },
            TaskCreationOptions.LongRunning))];

        await Task.WhenAll(writers);

        buffer.Write(MakeEntry("end"));

        await consumer.WaitAsync(TimeSpan.FromSeconds(30));

        IReadOnlyList<LogEntry> snapshot = buffer.GetSnapshot();

        for (int i = 1; i < snapshot.Count; i++)
        {
            Assert.Equal(snapshot[i - 1].Sequence + 1, snapshot[i].Sequence);
        }

        Assert.NotEmpty(streamed);

        for (int i = 1; i < streamed.Count; i++)
        {
            Assert.True(
                streamed[i - 1] < streamed[i],
                $"Stream delivered sequence {streamed[i]} after {streamed[i - 1]}.");
        }
    }

    private static LogEntry MakeEntry(string message) =>
        new(0, DateTimeOffset.UtcNow, Core.Logging.LogLevel.Information, "test", message, null, null, null, []);

    private static async Task<LogEntry?> ReadOneAsync(InMemoryLogRingBuffer buffer, CancellationToken ct)
    {
        await foreach (LogEntry item in buffer.StreamAsync(ct))
        {
            return item;
        }

        return null;
    }
}
