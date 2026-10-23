using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The per-format fixtures every integration path leans on must themselves be sound: each one extracts to its
/// visible sentinel and never its hidden one, covers the registry exactly, and keeps today's behaviour with the
/// feature off.
/// </summary>
public sealed class ComprehensionFormatFixturesTests
{
    public static TheoryData<string> Formats => ComprehensionFormatFixtures.MimeTypes;

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task ExtractDocument_EveryFormat_ProjectsTheVisibleSentinelAndNeverTheHiddenOne(string mimeType)
    {
        ComprehensionFormatFixture fixture = ComprehensionFormatFixtures.For(mimeType);

        int reads = 0;

        NoOpSessionAttachmentStore store = new(readBytes: (_, _) =>
        {
            reads++;

            return Task.FromResult<ReadOnlyMemory<byte>>(fixture.Package);
        });

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            store,
            Record(fixture),
            Settings(comprehension: true),
            1024 * 1024,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.False(result.WasTruncated);

        Assert.Null(result.FailureReason);

        fixture.AssertSurfaces(result.Text);

        Assert.Equal(1, reads);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task ExtractDocument_EveryFormatWithComprehensionOff_StaysNotEligibleAndNeverReadsTheDocument(string mimeType)
    {
        // Sensitivity: the IsEligibleDocument gate in the extractor facade. With the feature off the document is
        // never read, so today's behaviour (an unindexed binary) holds for all eleven formats.
        ComprehensionFormatFixture fixture = ComprehensionFormatFixtures.For(mimeType);

        NoOpSessionAttachmentStore store = new(
            readBytes: (_, _) => throw new InvalidOperationException("A document must not be read while Comprehension is off."),
            openRead: (_, _) => throw new InvalidOperationException("A document must not be streamed while Comprehension is off."));

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            store,
            Record(fixture),
            Settings(comprehension: false),
            1024 * 1024,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Empty(result.Text);

        Assert.False(result.WasTruncated);

        Assert.Null(result.FailureReason);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Fixture_FileNameDetectsToItsRegistryMimeAndCarriesTheRegistryLabel(string mimeType)
    {
        ComprehensionFormatFixture fixture = ComprehensionFormatFixtures.For(mimeType);

        Assert.Equal(mimeType, AttachmentMimeDetector.Detect(fixture.Package, fixture.FileName));

        Assert.True(SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out SessionAttachmentComprehensionFormat? format));

        Assert.Equal(format.FormatName, fixture.Label);

        Assert.Contains(fixture.Label, fixture.DataFraming, StringComparison.Ordinal);

        Assert.StartsWith(".", fixture.Extension, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixtures_CoverEveryRegistryFormatButTheClosedContainer()
    {
        // PDF is registered but parsed only out of process, so these in-process package fixtures do not cover it.
        string[] expected = [.. SessionAttachmentComprehensionPolicy.SupportedMimeTypes
            .Where(mimeType => mimeType != SessionAttachmentComprehensionPolicy.MimeType
                && mimeType != SessionAttachmentComprehensionPolicy.PdfMimeType)
            .Order(StringComparer.Ordinal)];

        string[] actual = [.. ComprehensionFormatFixtures.All.Select(fixture => fixture.MimeType).Order(StringComparer.Ordinal)];

        Assert.Equal(11, actual.Length);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Fixtures_NeverShareASentinelOrAFileExtension()
    {
        string[] sentinels = [.. ComprehensionFormatFixtures.All.SelectMany(fixture => new[] { fixture.VisibleSentinel, fixture.HiddenSentinel })];

        Assert.Equal(sentinels.Length, sentinels.Distinct(StringComparer.Ordinal).Count());

        foreach (string sentinel in sentinels)
        {
            Assert.Single(sentinels, other => other.Contains(sentinel, StringComparison.Ordinal));
        }

        string[] extensions = [.. ComprehensionFormatFixtures.All.Select(fixture => fixture.Extension)];

        Assert.Equal(extensions.Length, extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Fixture_PackageCarriesTheHiddenSentinelSoTheExclusionIsReal(string mimeType)
    {
        // Sensitivity: a hidden sentinel that never reached the package would make "absent from the output" vacuous.
        // The packages are deflated, so inflate each readable entry and look for the sentinel inside. The deliberately
        // uninflatable macro parts of a Visio package declare a bomb-sized length and are skipped by the size bound.
        ComprehensionFormatFixture fixture = ComprehensionFormatFixtures.For(mimeType);

        using MemoryStream stream = new(fixture.Package, writable: false);

        using System.IO.Compression.ZipArchive archive = new(stream, System.IO.Compression.ZipArchiveMode.Read);

        bool found = false;

        foreach (System.IO.Compression.ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.Length == 0 || entry.Length > 1024 * 1024)
            {
                continue;
            }

            using Stream content = entry.Open();

            using StreamReader reader = new(content, System.Text.Encoding.UTF8);

            if (reader.ReadToEnd().Contains(fixture.HiddenSentinel, StringComparison.Ordinal))
            {
                found = true;
            }
        }

        Assert.True(found, $"No part of the {fixture.FileName} fixture carries its hidden sentinel.");
    }

    private static SessionAttachmentRecord Record(ComprehensionFormatFixture fixture) =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
            "document", fixture.FileName, 1, "untrusted/path", "hash", fixture.MimeType, fixture.Package.Length,
            SessionAttachmentKind.Binary, DateTimeOffset.UtcNow);

    private static ArcanumSettings Settings(bool comprehension) => new()
    {
        Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = comprehension },
    };
}
