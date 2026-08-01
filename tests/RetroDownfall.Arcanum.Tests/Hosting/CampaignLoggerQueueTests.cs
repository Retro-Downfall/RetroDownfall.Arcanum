using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class CampaignLoggerQueueTests
{

    [Fact]

    public void BuildAttachmentConsultationReferences_IncludesMetadataWithoutRawContentOrPaths()
    {

        AttachmentMemoryProvenance provenance = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "security-review",
            7,
            "super-secret-hash",
            DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            "WorkspaceFile",
            AttachmentSourceAvailability.Available);

        string references = CampaignSummaryAttachmentPolicy.BuildConsultedReferences(
            [provenance]);

        Assert.Contains("security-review", references, StringComparison.Ordinal);

        Assert.Contains("version=7", references, StringComparison.Ordinal);

        Assert.DoesNotContain("super-secret-hash", references, StringComparison.Ordinal);

        Assert.DoesNotContain("/Users/", references, StringComparison.Ordinal);

        Assert.DoesNotContain("raw attachment content", references, StringComparison.Ordinal);

    }

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
