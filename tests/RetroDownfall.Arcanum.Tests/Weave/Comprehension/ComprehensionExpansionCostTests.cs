using System.Diagnostics;

using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The cost of a small hostile package is bounded by the package itself. A workbook that points many
/// sheets at one part cannot inflate and parse that part far beyond two hundred times its own size, and a
/// part is inflated into one buffer of its declared size rather than a doubling series.
/// </summary>
public sealed class ComprehensionExpansionCostTests
{
    private const int Mebibyte = 1024 * 1024;

    [Fact]
    public void Spreadsheet_ManySheetsReReadingOneSubFloorPart_IsRefusedCheaply()
    {
        // 4096 sheets all pointing at one 1 MiB worksheet of hidden rows (exactly the per-entry ratio floor,
        // so no per-entry ratio applies): about 24 KB of package.
        byte[] package = SameTargetWorkbook(
            4096,
            Filler($"<worksheet xmlns=\"{ContentionWorkbooks.Main}\"><sheetData>", "<row hidden=\"1\"/>", "</sheetData></worksheet>", Mebibyte),
            padToRatio: false);

        Assert.True(package.Length < 32 * 1024, $"The recipe is {package.Length} bytes.");

        _ = Run(package);

        (Outcome outcome, TimeSpan elapsed, long allocated) = Run(package);

        Assert.True(outcome.Refused || outcome.Status != SessionAttachmentExtractionStatus.Extracted, $"Outcome: {outcome}.");

        Assert.True(allocated < 16L * Mebibyte, $"A {package.Length}-byte package allocated {allocated / (double)Mebibyte:F1} MiB.");

        Assert.True(elapsed < TimeSpan.FromMilliseconds(500), $"A {package.Length}-byte package took {elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public void Spreadsheet_TwoSheetsReReadingOneMaximumSizePart_IsRefusedWithOneBufferOfAllocation()
    {
        // Two sheets pointing at one part of (32 MiB - 4 KiB) of unknown elements, its compressed area padded to
        // exactly the per-entry 200:1 ratio: about 165 KB of package that names 64 MiB of parsing.
        byte[] package = SameTargetWorkbook(
            2,
            Filler($"<worksheet xmlns=\"{ContentionWorkbooks.Main}\"><sheetData>", "<x/>", "</sheetData></worksheet>", (32 * Mebibyte) - 4096),
            padToRatio: true);

        Assert.True(package.Length < 200 * 1024, $"The recipe is {package.Length} bytes.");

        _ = Run(package);

        (Outcome outcome, _, long allocated) = Run(package);

        // The package bound refuses the second read today; a rule refusing a sheet that repeats an already
        // read target may refuse it earlier. Either way it is refused, never parsed twice.
        Assert.True(outcome.Refused, $"Outcome: {outcome}.");

        Assert.True(allocated < 40L * Mebibyte, $"A {package.Length}-byte package allocated {allocated / (double)Mebibyte:F1} MiB.");
    }

    private static (Outcome Outcome, TimeSpan Elapsed, long Allocated) Run(byte[] package)
    {
        BoundedDocumentTextSink sink = new(262_144, CancellationToken.None);

        long before = GC.GetAllocatedBytesForCurrentThread();

        long started = Stopwatch.GetTimestamp();

        Outcome outcome;

        try
        {
            ValueTask pending = new SpreadsheetDocumentExtractor().ExtractAsync(package, sink, CancellationToken.None);

            // The measurement is per thread, so the extractor must finish on this one.
            Assert.True(pending.IsCompleted, "The spreadsheet extractor must complete synchronously.");

            outcome = pending.IsCompletedSuccessfully
                ? new Outcome(sink.Complete().Status, null)
                : new Outcome(null, Assert.IsType<DocumentPackageRejectedException>(pending.AsTask().Exception?.InnerException).Rejection);
        }
        catch (DocumentPackageRejectedException rejected)
        {
            outcome = new Outcome(null, rejected.Rejection);
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        return (outcome, elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static byte[] SameTargetWorkbook(int sheets, byte[] worksheet, bool padToRatio)
    {
        StringBuilder sheetElements = new();

        for (int index = 0; index < sheets; index++)
        {
            sheetElements.Append($"<sheet name=\"S{index}\" sheetId=\"{index + 1}\" r:id=\"rIdS1\"/>");
        }

        RawZipPackage raw = new();

        raw.Entries.Add(RawZipEntry.Deflated(
            "_rels/.rels",
            ContentionWorkbooks.Rels($"<Relationship Id=\"rId1\" Type=\"{ContentionWorkbooks.Relationships}/officeDocument\" Target=\"xl/workbook.xml\"/>")));

        raw.Entries.Add(RawZipEntry.Deflated(
            "xl/workbook.xml",
            $"<workbook xmlns=\"{ContentionWorkbooks.Main}\" xmlns:r=\"{ContentionWorkbooks.Relationships}\"><sheets>{sheetElements}</sheets></workbook>"));

        raw.Entries.Add(RawZipEntry.Deflated(
            "xl/_rels/workbook.xml.rels",
            ContentionWorkbooks.Rels($"<Relationship Id=\"rIdS1\" Type=\"{ContentionWorkbooks.Relationships}/worksheet\" Target=\"worksheets/sheet1.xml\"/>")));

        byte[] deflated = ComprehensionPackageBuilder.Deflate(worksheet);

        long ratioFloor = padToRatio ? (worksheet.Length + BoundedZipPackage.MaximumCompressionRatio - 1) / BoundedZipPackage.MaximumCompressionRatio : 0;

        byte[] area = deflated.Length >= ratioFloor ? deflated : [.. deflated, .. new byte[ratioFloor - deflated.Length]];

        raw.Entries.Add(new RawZipEntry(ComprehensionPackageBuilder.Utf8Bytes("xl/worksheets/sheet1.xml"), worksheet, 8)
        {
            DataAreaBytes = area,
        });

        return raw.ToBytes();
    }

    private static byte[] Filler(string prefix, string unit, string suffix, int targetBytes)
    {
        StringBuilder builder = new(targetBytes + 64);

        builder.Append(prefix);

        while (builder.Length + unit.Length + suffix.Length <= targetBytes)
        {
            builder.Append(unit);
        }

        builder.Append(suffix);

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private readonly record struct Outcome(SessionAttachmentExtractionStatus? Status, DocumentPackageRejection? Rejection)
    {
        public bool Refused => Rejection is not null;
    }
}
