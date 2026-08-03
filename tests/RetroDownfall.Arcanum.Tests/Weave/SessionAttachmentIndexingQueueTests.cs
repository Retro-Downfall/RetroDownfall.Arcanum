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
