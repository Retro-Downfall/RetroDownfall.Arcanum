using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

public sealed class PdfExtractionLimitsTests
{
    [Fact]
    public void Default_HoldsTheCodeOwnedProductionBounds()
    {
        PdfExtractionLimits limits = PdfExtractionLimits.Default;

        Assert.Equal(2000, limits.MaxPages);

        Assert.Equal(100_000, limits.MaxObjects);

        Assert.Equal(250_000, limits.MaxLettersPerPage);

        Assert.Equal(2_000_000, limits.MaxLettersPerDocument);

        Assert.Equal(262_144, limits.MaxOutputCharacters);
    }

    [Theory]
    [InlineData(0, 1, 1, 1, 1, "maxPages")]
    [InlineData(1, 0, 1, 1, 1, "maxObjects")]
    [InlineData(1, 1, 0, 1, 1, "maxLettersPerPage")]
    [InlineData(1, 1, 1, 0, 1, "maxLettersPerDocument")]
    [InlineData(1, 1, 1, 1, 0, "maxOutputCharacters")]
    [InlineData(-1, 1, 1, 1, 1, "maxPages")]
    [InlineData(1, -1, 1, 1, 1, "maxObjects")]
    public void Constructor_RefusesABoundBelowOne(int maxPages, int maxObjects, int perPage, int perDocument, int output, string parameter)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PdfExtractionLimits(maxPages, maxObjects, perPage, perDocument, output));

        Assert.Equal(parameter, error.ParamName);
    }

    [Fact]
    public void Constructor_AcceptsTheSmallestBounds()
    {
        PdfExtractionLimits limits = new(1, 1, 1, 1, 1);

        Assert.Equal(1, limits.MaxPages);

        Assert.Equal(1, limits.MaxObjects);

        Assert.Equal(1, limits.MaxLettersPerPage);

        Assert.Equal(1, limits.MaxLettersPerDocument);

        Assert.Equal(1, limits.MaxOutputCharacters);
    }
}
