using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class CampaignLoggerQueueTests
{

    [Fact]
    public async Task QueueAsync_and_ReadAllAsync_round_trip_conversation_ids()
    {

        CampaignLoggerQueue queue = new(NullLogger<CampaignLoggerQueue>.Instance);

        Guid conversationId = Guid.NewGuid();

        using CancellationTokenSource cts = new();

        Task<Guid> readTask = ReadOneAsync(queue, cts.Token);

        await queue.QueueAsync(conversationId);

        Guid received = await readTask;

        Assert.Equal(conversationId, received);

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
