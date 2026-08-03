using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class SagaExtractionConstraintReductionTests
{

    [Theory]

    [InlineData("MaxMemoriesPerSession")]

    [InlineData("MaxMemoriesTotal")]

    [InlineData("ExtractionMaxTokens")]

    [InlineData("ExtractionIntervalMinutes")]

    [InlineData("ExtractionWindowEntries")]

    public void Saga_settings_omit_obsolete_fixed_caps_and_windows(string propertyName)
    {

        Assert.Null(typeof(SagaEmbeddingSettings).GetProperty(propertyName));

    }

    [Theory]

    [InlineData("EmbeddingsSagaMaxMemoriesPerSession")]

    [InlineData("EmbeddingsSagaMaxMemoriesTotal")]

    [InlineData("EmbeddingsSagaExtractionMaxTokens")]

    [InlineData("EmbeddingsSagaExtractionIntervalMinutes")]

    [InlineData("EmbeddingsSagaExtractionWindowEntries")]

    public void Saga_clamps_omit_obsolete_fixed_caps_and_windows(string methodName)
    {

        Assert.Null(typeof(ArcanumSettingClamps).GetMethod(methodName));

    }

}
