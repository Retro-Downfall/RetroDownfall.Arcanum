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

    public void TryEnqueue_AtCapacity_ReturnsFalseWithoutThrowing()
    {

        ArcanumSettings settings = new()
        {

            Features = new FeatureSettings { AttachmentRetrieval = true },

            Integrations = new IntegrationSettings
            {

                Embeddings = new EmbeddingIntegrationSettings
                {

                    AttachmentIndexing = new AttachmentIndexingIntegrationSettings
                    {

                        QueueCapacity = 1,

                    },

                },

            },

        };

        IServiceScopeFactory scopes = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        SessionAttachmentIndexingService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<SessionAttachmentIndexingService>.Instance);

        Assert.True(service.TryEnqueue(new SessionAttachmentIndexRequest(Guid.NewGuid(), Guid.NewGuid())));

        Assert.False(service.TryEnqueue(new SessionAttachmentIndexRequest(Guid.NewGuid(), Guid.NewGuid())));

    }

}
