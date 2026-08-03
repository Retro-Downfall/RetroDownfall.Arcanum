using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class AttachmentEmbeddingSettingsTests
{

    [Fact]

    public void ResolveEmbeddings_UsesAutomaticAttachmentMechanics()
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
                    Provider = "embedding-provider",
                },

            },

        };

        EmbeddingSettings embeddings = settings.ResolveEmbeddings();

        Assert.True(embeddings.Enabled);

        Assert.True(embeddings.AttachmentRetrievalEnabled);

        Assert.Equal(
            ArcanumRuntimeDefaults.Embeddings.Attachments,
            embeddings.Attachments);

    }

    [Theory]

    [InlineData(0, 1)]

    [InlineData(8, 8)]

    [InlineData(500, 100)]

    public void RetrievedAttachmentCount_ClampsToSafeRange(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.EmbeddingsAttachmentMaxRetrievedAttachments(value));

    }

    [Theory]

    [InlineData(0, 1_024)]

    [InlineData(262_144, 262_144)]

    [InlineData(20_000_000, 16_777_216)]

    public void RetrievedAttachmentBytes_ClampToSafeRange(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.EmbeddingsAttachmentMaxRetrievedBytes(value));

    }

    [Theory]

    [InlineData(0, 128)]

    [InlineData(32_768, 32_768)]

    [InlineData(2_000_000, 1_048_576)]

    public void RetrievedAttachmentTokens_ClampToSafeRange(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.EmbeddingsAttachmentMaxRetrievedTokens(value));

    }

    [Fact]

    public void AttachmentOverlap_IsAlwaysLessThanEffectiveChunkSize()
    {

        int chunkSize = ArcanumSettingClamps.EmbeddingsAttachmentChunkSizeCharacters(128);

        int overlap = ArcanumSettingClamps.EmbeddingsAttachmentChunkOverlapForChunkSize(10_000, chunkSize);

        Assert.Equal(chunkSize - 1, overlap);

    }

}
