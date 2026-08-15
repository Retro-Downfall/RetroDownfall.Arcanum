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
        Assert.Equal(Digest("BD08F0D98C4292952C2081965BBA413A28E17892D6CB6FED85EDB44FB3D285B3"), result.ResultAggregateDigest);
        Assert.Equal(Digest("A7A19D6FA16D036B69E64FCDC6C971EB0790F0766FDDE83A4E1D8CF00D54CD93"), result.Aggregate);
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
