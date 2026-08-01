using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class AttachmentEmbeddingSettingsTests
{

    [Fact]

    public void ResolveEmbeddings_ProjectsAttachmentRetrievalFeatureAndBounds()
    {

        ArcanumSettings settings = new()
        {

            Features = new FeatureSettings
            {

                AttachmentRetrieval = true,

            },

            Integrations = new IntegrationSettings
            {

                Embeddings = new EmbeddingIntegrationSettings
                {

                    AttachmentIndexing = new AttachmentIndexingIntegrationSettings
                    {

                        MaxAttachmentBytes = 2_000_000,

                        MaxExtractedCharacters = 120_000,

                        ChunkSizeCharacters = 900,

                        ChunkOverlapCharacters = 90,

                        MaxChunksPerAttachment = 75,

                        MaxAttachmentsPerBatch = 6,

                        QueueCapacity = 80,

                        MaxRetries = 4,

                        RetryDelaySeconds = 7,

                        ProcessingTimeoutSeconds = 45,

                        MaxRetrievedChunks = 8,

                    },

                },

            },

        };

        EmbeddingSettings embeddings = settings.ResolveEmbeddings();

        Assert.True(embeddings.Enabled);

        Assert.True(embeddings.AttachmentRetrievalEnabled);

        Assert.Equal(2_000_000, embeddings.Attachments.MaxAttachmentBytes);

        Assert.Equal(120_000, embeddings.Attachments.MaxExtractedCharacters);

        Assert.Equal(900, embeddings.Attachments.ChunkSizeCharacters);

        Assert.Equal(90, embeddings.Attachments.ChunkOverlapCharacters);

        Assert.Equal(75, embeddings.Attachments.MaxChunksPerAttachment);

        Assert.Equal(6, embeddings.Attachments.MaxAttachmentsPerBatch);

        Assert.Equal(80, embeddings.Attachments.QueueCapacity);

        Assert.Equal(4, embeddings.Attachments.MaxRetries);

        Assert.Equal(7, embeddings.Attachments.RetryDelaySeconds);

        Assert.Equal(45, embeddings.Attachments.ProcessingTimeoutSeconds);

        Assert.Equal(8, embeddings.Attachments.MaxRetrievedChunks);

    }

    [Theory]

    [InlineData(0, 1_024)]

    [InlineData(2_000_000, 2_000_000)]

    [InlineData(100_000_000, 20_971_520)]

    public void AttachmentMaximumBytes_ClampsToSafeRange(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.EmbeddingsAttachmentMaxBytes(value));

    }

    [Fact]

    public void AttachmentOverlap_IsAlwaysLessThanEffectiveChunkSize()
    {

        int chunkSize = ArcanumSettingClamps.EmbeddingsAttachmentChunkSizeCharacters(128);

        int overlap = ArcanumSettingClamps.EmbeddingsAttachmentChunkOverlapForChunkSize(10_000, chunkSize);

        Assert.Equal(chunkSize - 1, overlap);

    }

}
