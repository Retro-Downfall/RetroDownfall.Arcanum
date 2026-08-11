using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed class SessionAttachmentIndexingQueueTests
{

    [Fact]

    public void TryEnqueue_WhenAutomaticQueueIsTemporarilyFull_ReturnsFalseWithoutThrowing()
    {

        ArcanumSettings settings = new()
        {

            Features = new FeatureSettings { AttachmentRetrieval = true },

        };

        IServiceScopeFactory scopes = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        SessionAttachmentIndexingService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<SessionAttachmentIndexingService>.Instance);

        for (int index = 0; index < ArcanumRuntimeDefaults.Embeddings.Attachments.QueueCapacity; index++)
        {

            Assert.True(service.TryEnqueue(new SessionAttachmentIndexRequest(Guid.NewGuid(), Guid.NewGuid())));

        }

        Assert.False(service.TryEnqueue(new SessionAttachmentIndexRequest(Guid.NewGuid(), Guid.NewGuid())));

    }

    [Theory]

    [InlineData(0, 1)]

    [InlineData(3, 4)]

    [InlineData(int.MaxValue, int.MaxValue)]

    public void AutomaticRetry_HasNoAttemptCeiling(int attempt, int expectedNextAttempt)
    {

        SessionAttachmentIndexOutcome outcome = new(
            SessionAttachmentIndexStatus.Failed,
            ShouldRetry: true);

        Assert.True(
            SessionAttachmentIndexingService.ShouldAutomaticallyRetry(
                outcome,
                CancellationToken.None));

        Assert.Equal(
            expectedNextAttempt,
            SessionAttachmentIndexingService.NextAttempt(attempt));

    }

    [Fact]

    public async Task WaitForWork_KeepsOneChannelWaiterAliveAcrossIdleReconciliationPeriods()
    {

        Channel<SessionAttachmentIndexRequest> channel = Channel.CreateBounded<SessionAttachmentIndexRequest>(
            new BoundedChannelOptions(8)
            {

                FullMode = BoundedChannelFullMode.Wait,

                SingleReader = true,

                SingleWriter = false,

            });

        SessionAttachmentIndexingService.QueueWait wait = new();

        Task<bool>? issued = null;

        for (int period = 0; period < 5; period++)
        {

            Assert.Equal(
                SessionAttachmentIndexingService.QueueSignal.ReconciliationDue,
                await SessionAttachmentIndexingService.WaitForWorkAsync(
                    channel.Reader,
                    wait,
                    TimeSpan.FromMilliseconds(1),
                    CancellationToken.None));

            issued ??= wait.PendingRead;

            // An abandoned WaitToReadAsync stays queued on the channel's waiting-reader list until a
            // write drains it — cancelling it does not unlink it — so issuing a fresh one every
            // reconciliation period grows that list without bound on a host nobody attaches files to.
            Assert.Same(issued, wait.PendingRead);

        }

        Assert.True(channel.Writer.TryWrite(new SessionAttachmentIndexRequest(Guid.NewGuid(), Guid.NewGuid())));

        Assert.Equal(
            SessionAttachmentIndexingService.QueueSignal.Work,
            await SessionAttachmentIndexingService.WaitForWorkAsync(
                channel.Reader,
                wait,
                TimeSpan.FromSeconds(30),
                CancellationToken.None));

        // The consumed waiter must not be reused: its next await would return the stale answer.
        Assert.Null(wait.PendingRead);

    }

    [Fact]

    public void AutomaticRetry_StopsWhenServiceIsCancelled()
    {

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        SessionAttachmentIndexOutcome outcome = new(
            SessionAttachmentIndexStatus.Failed,
            ShouldRetry: true);

        Assert.False(
            SessionAttachmentIndexingService.ShouldAutomaticallyRetry(
                outcome,
                cancellation.Token));

    }

}
