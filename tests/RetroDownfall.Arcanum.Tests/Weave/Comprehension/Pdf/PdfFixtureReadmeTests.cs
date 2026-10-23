using System.Text.RegularExpressions;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

/// <summary>Keeps <c>TestData/Comprehension/Pdf/README.md</c> true: it is the only place that records where each fixture came from.</summary>
public sealed partial class PdfFixtureReadmeTests
{
    private const long IntakeCeilingBytes = 1_048_576;

    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "Pdf");

    [GeneratedRegex(@"`([a-z0-9-]+\.pdf)`")]
    private static partial Regex PdfName();

    private static string[] CommittedPdfs() => [.. System.IO.Directory.GetFiles(Directory, "*.pdf").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    private static string Readme() => File.ReadAllText(Path.Combine(Directory, "README.md"));

    [Fact]
    public void EveryCommittedPdf_IsNamedInTheReadme()
    {
        string readme = Readme();

        string[] undocumented = [.. CommittedPdfs().Where(name => !readme.Contains($"`{name}`", StringComparison.Ordinal))];

        Assert.Empty(undocumented);
    }

    [Fact]
    public void EveryPdfTheReadmeNamesOutsideTheLargeList_IsCommitted()
    {
        string readme = Readme();

        string withoutLarge = readme[..readme.IndexOf("The `--large` generator output", StringComparison.Ordinal)];

        string[] committed = CommittedPdfs();

        string[] missing = [.. PdfName().Matches(withoutLarge).Select(match => match.Groups[1].Value).Distinct().Where(name => !committed.Contains(name))];

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryCommittedPdf_FitsTheAttachmentIntakeCeiling()
    {
        string[] oversize = [.. CommittedPdfs().Where(name => new FileInfo(Path.Combine(Directory, name)).Length > IntakeCeilingBytes)];

        Assert.Empty(oversize);
    }

    /// <summary>The fixtures only the worker's process limits can survive: the Flate bombs and the form fan-out.</summary>
    private static bool IsWorkerOnly(string name) =>
        name.StartsWith("flate-", StringComparison.Ordinal) || string.Equals(name, "xobject-fan-out.pdf", StringComparison.Ordinal);

    [Fact]
    public void TheWorkerOnlyFixtures_AreExactlyTheFlateOnesAndTheFormFanOut()
    {
        string readme = Readme();

        string section = readme[readme.IndexOf("## Worker-only", StringComparison.Ordinal)..readme.IndexOf("The `--large` generator output", StringComparison.Ordinal)];

        string[] listed = [.. PdfName().Matches(section).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal)];

        Assert.Equal(CommittedPdfs().Where(IsWorkerOnly), listed);

        Assert.Equal(5, listed.Length);
    }
}
