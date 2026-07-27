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
