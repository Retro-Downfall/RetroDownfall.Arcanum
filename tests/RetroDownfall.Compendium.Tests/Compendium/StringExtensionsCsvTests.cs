using RetroDownfall.Compendium.Ux.ViewModels;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

public sealed class StringExtensionsCsvTests
{

    [Fact]
    public void JoinCsv_deduplicates_case_insensitive_preserving_first()
    {

        string joined = new[]
        {
            "http://localhost:5001",
            "http://127.0.0.1:5001",
            "http://localhost:5001",
            "HTTP://127.0.0.1:5001",
            "http://localhost:3000",
        }.JoinCsv();

        Assert.Equal(
            "http://localhost:5001, http://127.0.0.1:5001, http://localhost:3000",
            joined);

    }

    [Fact]
    public void SplitCsv_deduplicates_case_insensitive_preserving_first()
    {

        string[] split =
            "http://localhost:5001, http://127.0.0.1:5001, http://localhost:5001, HTTP://127.0.0.1:5001"
                .SplitCsv();

        Assert.Equal(
            [
                "http://localhost:5001",
                "http://127.0.0.1:5001",
            ],
            split);

    }

}
