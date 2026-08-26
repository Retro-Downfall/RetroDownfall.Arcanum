using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantDigestCorpusTests
{
    [Fact]
    public void Reflection_free_corpus_executes_the_complete_task_4_literal_set()
    {
        CovenantDigestCorpusResult result = CovenantDigestCorpus.Run();

        Assert.True(result.Succeeded, result.FirstFailureCaseId);
        Assert.Equal(249, result.TotalCaseCount);
        Assert.Equal(35, result.CategoryCounts.DomainVectors);
        Assert.Equal(6, result.CategoryCounts.SectionCases);
        Assert.Equal(35, result.CategoryCounts.ProviderCases);
        Assert.Equal(17, result.CategoryCounts.OptionalCases);
        Assert.Equal(19, result.CategoryCounts.OrderingCases);
        Assert.Equal(10, result.CategoryCounts.ChainCases);
        Assert.Equal(16, result.CategoryCounts.WriterCases);
        Assert.Equal(16, result.CategoryCounts.DisclosureCases);
        Assert.Equal(95, result.CategoryCounts.RefusalCases);
        Assert.Equal(Digest("F6445FFA3C98B7247D8A250E01FBB9DD3D01653F3B925537D2A13230775EEE3F"), result.CaseManifestDigest);
        Assert.Equal(Digest("7AF01898CCE65599249BCB4EAB99CAD7E89ECB8EB2A9868B27F4D5563E4B88DD"), result.ResultAggregateDigest);
        Assert.Equal(Digest("231B661139A6DF328BE3440233B8C1B4E8CE42371C70E18D734BCBAC7617F4C6"), result.Aggregate);
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
