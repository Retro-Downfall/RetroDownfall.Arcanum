using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.ExcelWorkbookFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The spreadsheet extractor against packages real producers wrote (openpyxl, xlsxwriter; see
/// <c>TestData/Comprehension/Excel/README.md</c>), so the extractor is proven on bytes it did not build itself.
/// </summary>
public sealed class SpreadsheetRealProducerTests
{
    [Fact]
    public void Extract_OpenpyxlWorkbook_ProjectsVisibleCellsAndNothingHidden()
    {
        string text = ExtractText(ReadFixture("Excel", "openpyxl-basic.xlsx"));

        // Column C and row 3 are hidden; B4 is a formula with no cached value (nothing); the date is its serial number;
        // the multi-line cell keeps its line break; the hidden and veryHidden sheets are absent.
        Assert.Equal(
            "Sheet: Visible\nName\tScore\tNotes\nAlpha\t42\tkept\nTotal\t45306\nFlag\tTRUE\nMulti\nline\nSheet: Second\nBeta\tgap\n",
            text);

        foreach (string marker in new[] { "HIDDENCOLUMNTEXT", "HIDDENROWTEXT", "HIDDENSHEETTEXT", "VERYHIDDENSHEETTEXT", "COMMENTTEXT", "B2+B3" })
        {
            Assert.DoesNotContain(marker, text);
        }
    }

    [Fact]
    public void Extract_XlsxwriterWorkbook_ProjectsSharedStringsRichTextAndCachedFormulaValues()
    {
        string text = ExtractText(ReadFixture("Excel", "xlsxwriter-basic.xlsx"));

        // A1 and the rich string in B1 come from sharedStrings.xml (four runs concatenated); C is hidden; row 5 is hidden;
        // B2 projects the cached value of =A2*2; the error cell projects its cached error text; the second sheet is hidden.
        Assert.Equal("Sheet: Shared\nPlain\tRich bold and red\tafter\n3.5\t7\nTRUE\tFALSE\n#DIV/0!\n", text);

        foreach (string marker in new[] { "HIDDENCOLUMNTEXT", "HIDDENROWTEXT", "HIDDENSHEETTEXT", "COMMENTTEXT", "A2*2", "1/0" })
        {
            Assert.DoesNotContain(marker, text);
        }
    }

    [Fact]
    public void Extract_XlsxwriterMacroEnabledWorkbook_ProjectsCellsWithoutReadingTheMacroPart()
    {
        Assert.Equal("Sheet: Macros\nmacro-enabled\t12\n", ExtractText(ReadFixture("Excel", "xlsxwriter-macro.xlsm")));
    }

    [Fact]
    public void Extract_TextutilWordDocument_IsFormatMismatch()
    {
        // A real .docx (shared fixture from the container tests) fed to the Excel extractor: the main part is not a workbook.
        ExtractRejected(DocumentPackageRejection.FormatMismatch, ReadFixture("textutil-basic.docx"));
    }

    [Fact]
    public void Extract_TextutilOpenDocumentText_IsMissingPartBecauseItIsNotAnOpcPackage()
    {
        // An ODF package has no _rels/.rels, so there is no officeDocument relationship to follow.
        ExtractRejected(DocumentPackageRejection.MissingPart, ReadFixture("textutil-basic.odt"));
    }

    private static byte[] ReadFixture(params string[] pathParts) =>
        File.ReadAllBytes(Path.Combine([AppContext.BaseDirectory, "TestData", "Comprehension", .. pathParts]));
}
