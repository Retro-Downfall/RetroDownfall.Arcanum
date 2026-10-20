using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantDigestCorpusTests
{
    [Fact]
    public void Reflection_free_corpus_executes_the_complete_task_4_literal_set()
    {
        CovenantDigestCorpusResult result = CovenantDigestCorpus.Run();

        Assert.True(result.Succeeded, result.FirstFailureCaseId);
        Assert.Equal(250, result.TotalCaseCount);
        Assert.Equal(36, result.CategoryCounts.DomainVectors);
        Assert.Equal(6, result.CategoryCounts.SectionCases);
        Assert.Equal(35, result.CategoryCounts.ProviderCases);
        Assert.Equal(17, result.CategoryCounts.OptionalCases);
        Assert.Equal(19, result.CategoryCounts.OrderingCases);
        Assert.Equal(10, result.CategoryCounts.ChainCases);
        Assert.Equal(16, result.CategoryCounts.WriterCases);
        Assert.Equal(16, result.CategoryCounts.DisclosureCases);
        Assert.Equal(95, result.CategoryCounts.RefusalCases);
        Assert.Equal(Digest("B9DCBD46B99C3C66DAA0C4D731E34AE221F5AA95CD5021340B89A4DAED8F88DE"), result.CaseManifestDigest);
        Assert.Equal(Digest("0867B5597CC65A844BF42F4CF61DB989F9DCC876B80918E9AAE9AA8BD8036723"), result.ResultAggregateDigest);
        Assert.Equal(Digest("775640D28CC579AE63ED8C13A55840366BE6B46E525645E1AF2B228E1AD05ACF"), result.Aggregate);
    }

    [Fact]
    public void Corpus_has_one_parameterless_public_execution_entry_point()
    {
        Assert.Equal(typeof(CovenantDigestCorpus), typeof(CovenantDigestCorpus).GetMethod(nameof(CovenantDigestCorpus.Run))!.DeclaringType);
        Assert.Empty(typeof(CovenantDigestCorpus).GetConstructors());
    }

    private static CovenantDigest Digest(string hexadecimal) =>
        new(Convert.FromHexString(hexadecimal));
}
