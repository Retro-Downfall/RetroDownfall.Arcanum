using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class CampaignLoggerQueueTests
{

    [Fact]
    public async Task TryQueue_and_ReadAllAsync_round_trip_conversation_ids()
    {

        CampaignLoggerQueue queue = new(NullLogger<CampaignLoggerQueue>.Instance);

        Guid conversationId = Guid.NewGuid();

        using CancellationTokenSource cts = new();

        Task<Guid> readTask = ReadOneAsync(queue, cts.Token);

        Assert.True(queue.TryQueue(conversationId));

        Guid received = await readTask;

        Assert.Equal(conversationId, received);

        Assert.Equal(0, queue.PendingCountForTesting);

    }

    [Fact]
    public void TryQueue_CoalescesDuplicateSessionIds()
    {

        CampaignLoggerQueue queue = new(NullLogger<CampaignLoggerQueue>.Instance);

        Guid id = Guid.NewGuid();

        Assert.True(queue.TryQueue(id));

        Assert.True(queue.TryQueue(id));

        Assert.Equal(1, queue.PendingCountForTesting);

    }

    [Fact]
    public void TryQueue_WhenChannelFull_ReturnsFalseAndClearsPending()
    {

        CampaignLoggerQueue queue = new(NullLogger<CampaignLoggerQueue>.Instance);

        for (int i = 0; i < 100; i++)
        {

            Assert.True(queue.TryWriteRawForTesting(Guid.NewGuid()));

        }

        Guid rejected = Guid.NewGuid();

        Assert.False(queue.TryQueue(rejected));

        Assert.Equal(0, queue.PendingCountForTesting);

    }

    private static async Task<Guid> ReadOneAsync(CampaignLoggerQueue queue, CancellationToken ct)
    {

        await foreach (Guid id in queue.ReadAllAsync(ct))
        {

            return id;

        }

        throw new InvalidOperationException("No queued conversation id was read.");

    }

}
