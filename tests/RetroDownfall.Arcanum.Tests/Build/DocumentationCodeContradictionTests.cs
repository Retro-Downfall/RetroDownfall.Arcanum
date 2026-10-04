using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// A sentence in a reference document does not say the opposite of the source it describes.
/// </summary>
/// <remarks>
/// <para>Each test pairs a claim the documents make with the line of source that makes it true or
/// false, so a failure is a contradiction between two files and not a spelling rule over one. They
/// exist because the failure they prevent is silent: a "nothing publishes" or "no route is mapped"
/// sentence stays readable and plausible after the code beside it changes, and a reader who trusts
/// it concludes the surface is unreachable or the evidence complete.</para>
/// <para>The assertions are deliberately narrow. Each names the stale sentence by a distinctive
/// fragment and the true one by the term it has to carry, so rewording the surrounding prose does
/// not fail them.</para>
/// </remarks>
public sealed class DocumentationCodeContradictionTests
{
    [Fact]
    public void A_committed_entry_erase_is_documented_as_republishing_canonical_mutation()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        string section = DocumentSection(
            design,
            "#### 21.15.7 Covenant entry erasure",
            "#### 21.15.8 Lifecycle and restore");

        string service = ReadSource("Infrastructure", "Covenant", "CovenantEntryErasureService.cs");

        // The claim is true only while the service republishes after its COMMIT.
        Assert.Contains("CovenantHealthTransition.CanonicalMutation", service, StringComparison.Ordinal);

        Assert.DoesNotContain("publishes no availability", section, StringComparison.Ordinal);

        Assert.DoesNotContain("publishes no availability", service, StringComparison.Ordinal);

        Assert.Contains("republishes `CanonicalMutation`", section, StringComparison.Ordinal);
    }

    private static string ReadDocument(string fileName) =>
        File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", fileName))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ReadSource(params string[] relative) =>
        File
            .ReadAllText(
                Path.Combine(
                    [
                        TestRepositoryPaths.RepositoryRoot(),
                        "src",
                        $"RetroDownfall.Arcanum.{relative[0]}",
                        .. relative[1..],
                    ]))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string DocumentSection(
        string document,
        string startHeading,
        string endHeading)
    {
        int start = document.IndexOf(startHeading, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Missing documentation heading: {startHeading}");

        int end = document.IndexOf(endHeading, start, StringComparison.Ordinal);

        Assert.True(end > start, $"Missing documentation heading after {startHeading}: {endHeading}");

        return document[start..end];
    }
}
