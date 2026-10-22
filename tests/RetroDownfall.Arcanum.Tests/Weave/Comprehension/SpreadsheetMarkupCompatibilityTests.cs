using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.ExcelWorkbookFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Markup compatibility is a preprocessing layer: whatever a part writes inside <c>mc:AlternateContent</c> counts as if the
/// selected branch (the first <c>mc:Choice</c> whose <c>Requires</c> namespaces are all understood, otherwise the
/// <c>mc:Fallback</c>; never both) stood where the wrapper does. Hiding markup (hidden columns, merged ranges, a hidden
/// workbook window, a sheet state, a hidden row) must be honoured wherever the wrapper sits, or the cells it hides leak.
/// </summary>
public sealed class SpreadsheetMarkupCompatibilityTests
{
    private const string McNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private const string X14ac = "http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac";

    private const string Declarations = $"xmlns:mc=\"{McNamespace}\" xmlns:x14ac=\"{X14ac}\" xmlns:unknown=\"urn:example:not-understood\"";

    private const string HideB = "<cols><col min=\"2\" max=\"2\" hidden=\"1\"/></cols>";

    private const string HideC = "<cols><col min=\"3\" max=\"3\" hidden=\"1\"/></cols>";

    private const string HideBColumn = "<col min=\"2\" max=\"2\" hidden=\"1\"/>";

    private const string HideCColumn = "<col min=\"3\" max=\"3\" hidden=\"1\"/>";

    private static string Alternate(string choice, string? fallback = null, string? requires = "x14ac") =>
        $"<mc:AlternateContent {Declarations}><mc:Choice{(requires is null ? string.Empty : $" Requires=\"{requires}\"")}>{choice}</mc:Choice>"
        + $"{(fallback is null ? string.Empty : $"<mc:Fallback>{fallback}</mc:Fallback>")}</mc:AlternateContent>";

    private static string Merge(string reference) => $"<mergeCells count=\"1\"><mergeCell ref=\"{reference}\"/></mergeCells>";

    private static string ThreeColumns() => Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c"));

    private static string WorksheetPart(string body) =>
        $"<worksheet xmlns=\"{Main}\" xmlns:r=\"{Rel}\" xmlns:mc=\"{McNamespace}\" xmlns:x14ac=\"{X14ac}\" mc:Ignorable=\"x14ac\">{body}</worksheet>";

    // ------------------------------------------------------------ the reported leaks

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extract_HiddenColumnsInsideAlternateContent_AreHidden(bool withFallback)
    {
        // Sensitivity: resolving mc:AlternateContent in the cols pre-pass. Skipped as an unknown element, the hiding is lost.
        string rows = Row(Text("A1", "shown"), Text("B1", "HIDDENCOLUMNLEAK"));

        byte[] package = Xlsx(new Sheet("S", rows, Cols: Alternate(HideB, withFallback ? HideB : null)));

        Assert.Equal("Sheet: S\nshown\n", ExtractText(package));
    }

    [Fact]
    public void Extract_MergedRangesInsideAlternateContent_HideTheMergedAwayCells()
    {
        string rows = Row(Text("A1", "anchor"), Text("B1", "MERGEDAWAYLEAK"));

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Alternate(Merge("A1:B1"))));

        Assert.Equal("Sheet: S\nanchor\n", ExtractText(package));
    }

    [Fact]
    public void Extract_HiddenWorkbookWindowInsideAlternateContent_ProjectsNothing()
    {
        byte[] package = Xlsx(
            [new Sheet("S", Row(Text("A1", "HIDDENWINDOWLEAK")))],
            sharedStringItems: null,
            extraWorkbookXml: Alternate("<bookViews><workbookView visibility=\"hidden\"/></bookViews>"));

        SessionAttachmentExtractionResult result = Extract(package);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Empty(result.Text);
    }

    [Fact]
    public void Extract_WorksheetThatDeclaresItsNamespacesOnTheRoot_ResolvesTheChoiceThere()
    {
        // The shape Excel writes: mc and x14ac are declared once on the root, mc:Ignorable names x14ac.
        string part = WorksheetPart(
            "<mc:AlternateContent><mc:Choice Requires=\"x14ac\">" + HideB + "</mc:Choice></mc:AlternateContent>"
            + "<sheetData>" + Row(Text("A1", "shown"), Text("B1", "HIDDENCOLUMNLEAK")) + "</sheetData>");

        Assert.Equal("Sheet: S\nshown\n", ExtractText(Xlsx(new Sheet("S", string.Empty, Part: part))));
    }

    // ------------------------------------------------------------ which branch is taken

    [Fact]
    public void Extract_ChoiceTheExtractorUnderstands_IsTakenAndTheFallbackIsNot()
    {
        byte[] package = Xlsx(new Sheet("S", ThreeColumns(), Cols: Alternate(HideB, HideC)));

        Assert.Equal("Sheet: S\na\tc\n", ExtractText(package));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("x14ac unknown")]
    [InlineData("unknown x14ac")]
    [InlineData("undeclared")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Extract_ChoiceThatCannotBeTaken_FallsBackToTheFallbackAlone(string? requires)
    {
        // An unknown, unbound, empty or missing Requires means the choice cannot be taken: its hiding is not applied, and
        // the fallback's is.
        byte[] package = Xlsx(new Sheet("S", ThreeColumns(), Cols: Alternate(HideB, HideC, requires)));

        Assert.Equal("Sheet: S\na\tb\n", ExtractText(package));
    }

    [Fact]
    public void Extract_ChoiceThatCannotBeTakenAndNoFallback_ContributesNothing()
    {
        byte[] package = Xlsx(new Sheet("S", ThreeColumns(), Cols: Alternate(HideB, requires: "unknown")));

        Assert.Equal("Sheet: S\na\tb\tc\n", ExtractText(package));
    }

    [Theory]
    [InlineData(16, "Sheet: S\na\tc\n")]
    [InlineData(17, "Sheet: S\na\tb\n")]
    public void Extract_RequiresListOfMoreThanSixteenPrefixes_CannotBeTaken(int prefixes, string expected)
    {
        string requires = string.Join(' ', Enumerable.Repeat("x14ac", prefixes));

        byte[] package = Xlsx(new Sheet("S", ThreeColumns(), Cols: Alternate(HideB, HideC, requires)));

        Assert.Equal(expected, ExtractText(package));
    }

    [Fact]
    public void Extract_FirstUnderstoodChoice_IsTakenAmongSeveral()
    {
        string alternate = $"<mc:AlternateContent {Declarations}>"
            + "<mc:Choice Requires=\"unknown\"><cols><col min=\"1\" max=\"1\" hidden=\"1\"/></cols></mc:Choice>"
            + $"<mc:Choice Requires=\"x14ac\">{HideB}</mc:Choice>"
            + $"<mc:Choice Requires=\"x14ac\">{HideC}</mc:Choice>"
            + "<mc:Fallback><cols><col min=\"4\" max=\"4\" hidden=\"1\"/></cols></mc:Fallback></mc:AlternateContent>";

        string rows = Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c"), Text("D1", "d"));

        Assert.Equal("Sheet: S\na\tc\td\n", ExtractText(Xlsx(new Sheet("S", rows, Cols: alternate))));
    }

    [Fact]
    public void Extract_SelectedChoiceWithNoContent_IsTakenAndTheFallbackIsNot()
    {
        string alternate = $"<mc:AlternateContent {Declarations}><mc:Choice Requires=\"x14ac\"/><mc:Fallback>{HideB}</mc:Fallback></mc:AlternateContent>";

        Assert.Equal("Sheet: S\na\tb\tc\n", ExtractText(Xlsx(new Sheet("S", ThreeColumns(), Cols: alternate))));
    }

    [Fact]
    public void Extract_NestedAlternateContent_ResolvesEachLevel()
    {
        string inChoice = Alternate(Alternate(HideB, HideC), "<cols><col min=\"1\" max=\"1\" hidden=\"1\"/></cols>");

        string inFallback = Alternate("<cols><col min=\"1\" max=\"1\" hidden=\"1\"/></cols>", Alternate(HideB, HideC, requires: "unknown"), requires: "unknown");

        Assert.Equal("Sheet: S\na\tc\n", ExtractText(Xlsx(new Sheet("S", ThreeColumns(), Cols: inChoice))));

        Assert.Equal("Sheet: S\na\tb\n", ExtractText(Xlsx(new Sheet("S", ThreeColumns(), Cols: inFallback))));
    }

    [Fact]
    public void Extract_ChoiceAfterTheFallback_IsRefused()
    {
        string alternate = $"<mc:AlternateContent {Declarations}><mc:Fallback>{HideC}</mc:Fallback><mc:Choice Requires=\"x14ac\">{HideB}</mc:Choice></mc:AlternateContent>";

        ExtractRejected(DocumentPackageRejection.FormatMismatch, Xlsx(new Sheet("S", ThreeColumns(), Cols: alternate)));
    }

    [Fact]
    public void Extract_SecondFallback_IsRefused()
    {
        string alternate = $"<mc:AlternateContent {Declarations}><mc:Choice Requires=\"x14ac\">{HideB}</mc:Choice><mc:Fallback>{HideC}</mc:Fallback><mc:Fallback>{HideC}</mc:Fallback></mc:AlternateContent>";

        ExtractRejected(DocumentPackageRejection.FormatMismatch, Xlsx(new Sheet("S", ThreeColumns(), Cols: alternate)));
    }

    [Fact]
    public void Extract_ChoiceOutsideAnyAlternateContent_IsAnUnknownElement()
    {
        string stray = $"<mc:Choice xmlns:mc=\"{McNamespace}\" Requires=\"x14ac\">{HideB}</mc:Choice>";

        Assert.Equal("Sheet: S\na\tb\tc\n", ExtractText(Xlsx(new Sheet("S", ThreeColumns(), Cols: stray))));
    }

    // ------------------------------------------------------------ the understood namespaces

    [Theory]
    [InlineData("http://schemas.openxmlformats.org/spreadsheetml/2006/main")]
    [InlineData("http://purl.oclc.org/ooxml/spreadsheetml/main")]
    [InlineData("http://schemas.microsoft.com/office/spreadsheetml/2009/9/main")]
    [InlineData("http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac")]
    [InlineData("http://schemas.microsoft.com/office/spreadsheetml/2010/11/main")]
    [InlineData("http://schemas.microsoft.com/office/spreadsheetml/2010/11/ac")]
    [InlineData("http://schemas.microsoft.com/office/spreadsheetml/2011/1/ac")]
    public void Extract_ChoiceRequiringAnUnderstoodNamespace_IsTaken(string namespaceUri)
    {
        string alternate = $"<mc:AlternateContent xmlns:mc=\"{McNamespace}\" xmlns:p=\"{namespaceUri}\"><mc:Choice Requires=\"p\">{HideB}</mc:Choice><mc:Fallback>{HideC}</mc:Fallback></mc:AlternateContent>";

        Assert.Equal("Sheet: S\na\tc\n", ExtractText(Xlsx(new Sheet("S", ThreeColumns(), Cols: alternate))));
    }

    [Theory]
    [InlineData("http://schemas.openxmlformats.org/wordprocessingml/2006/main")]
    [InlineData("http://schemas.microsoft.com/office/drawing/2010/main")]
    [InlineData("http://schemas.microsoft.com/office/spreadsheetml/2014/revision")]
    [InlineData("http://schemas.microsoft.com/office/spreadsheetml/2099/9/main")]
    [InlineData("http://schemas.openxmlformats.org/markup-compatibility/2006")]
    [InlineData("urn:example:not-understood")]
    public void Extract_ChoiceRequiringANamespaceItDoesNotUnderstand_FallsBack(string namespaceUri)
    {
        string alternate = $"<mc:AlternateContent xmlns:mc=\"{McNamespace}\" xmlns:p=\"{namespaceUri}\"><mc:Choice Requires=\"p\">{HideB}</mc:Choice><mc:Fallback>{HideC}</mc:Fallback></mc:AlternateContent>";

        Assert.Equal("Sheet: S\na\tb\n", ExtractText(Xlsx(new Sheet("S", ThreeColumns(), Cols: alternate))));
    }

    // ------------------------------------------------------------ worksheet children

    [Fact]
    public void Extract_ColElementsInsideAlternateContent_AreHidden()
    {
        string cols = $"<cols>{Alternate(HideBColumn, HideCColumn)}</cols>";

        Assert.Equal("Sheet: S\na\tc\n", ExtractText(Xlsx(new Sheet("S", ThreeColumns(), Cols: cols))));
    }

    [Fact]
    public void Extract_ColElementsOfAFallback_AreHiddenWhenTheChoiceCannotBeTaken()
    {
        string cols = $"<cols>{Alternate(HideBColumn, HideCColumn, requires: "unknown")}</cols>";

        Assert.Equal("Sheet: S\na\tb\n", ExtractText(Xlsx(new Sheet("S", ThreeColumns(), Cols: cols))));
    }

    [Fact]
    public void Extract_MergeCellElementsInsideAlternateContent_AreMergedAway()
    {
        string merges = $"<mergeCells count=\"1\">{Alternate("<mergeCell ref=\"A1:B1\"/>", "<mergeCell ref=\"B1:C1\"/>")}</mergeCells>";

        Assert.Equal("Sheet: S\na\tc\n", ExtractText(Xlsx(new Sheet("S", ThreeColumns(), AfterSheetData: merges))));
    }

    [Fact]
    public void Extract_MergedRangesOfAFallback_AreMergedAwayWhenTheChoiceCannotBeTaken()
    {
        string merges = Alternate(Merge("A1:B1"), Merge("B1:C1"), requires: "unknown");

        Assert.Equal("Sheet: S\na\tb\n", ExtractText(Xlsx(new Sheet("S", ThreeColumns(), AfterSheetData: merges))));
    }

    [Fact]
    public void Extract_HidingMarkupOfOneChoice_IsAppliedOnceAcrossBothPasses()
    {
        // The cols and the merges are read in the pre-pass, the cells in the second pass: all three resolve the same branch.
        string rows = Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c"), Text("D1", "d"));

        string alternate = $"<mc:AlternateContent {Declarations}><mc:Choice Requires=\"x14ac\">{HideB}{Merge("C1:D1")}</mc:Choice><mc:Fallback>{Merge("A1:D1")}</mc:Fallback></mc:AlternateContent>";

        Assert.Equal("Sheet: S\na\tc\n", ExtractText(Xlsx(new Sheet("S", rows, Cols: alternate))));
    }

    [Fact]
    public void Extract_SheetDataInsideAlternateContent_IsProjected()
    {
        string part = WorksheetPart(Alternate("<sheetData>" + Row(Text("A1", "choice")) + "</sheetData>", "<sheetData>" + Row(Text("A1", "fallback")) + "</sheetData>"));

        Assert.Equal("Sheet: S\nchoice\n", ExtractText(Xlsx(new Sheet("S", string.Empty, Part: part))));
    }

    [Fact]
    public void Extract_SheetDataOfAFallback_IsProjectedWhenTheChoiceCannotBeTaken()
    {
        string part = WorksheetPart(Alternate("<sheetData>" + Row(Text("A1", "choice")) + "</sheetData>", "<sheetData>" + Row(Text("A1", "fallback")) + "</sheetData>", requires: "unknown"));

        Assert.Equal("Sheet: S\nfallback\n", ExtractText(Xlsx(new Sheet("S", string.Empty, Part: part))));
    }

    // ------------------------------------------------------------ rows, cells and their content

    [Fact]
    public void Extract_RowsInsideAlternateContent_AreProjectedFromTheSelectedBranch()
    {
        string rows = Row(Text("A1", "before")) + Alternate(Row(Text("A2", "choice")), Row(Text("A2", "fallback"))) + Row(Text("A3", "after"));

        Assert.Equal("Sheet: S\nbefore\nchoice\nafter\n", ExtractText(Xlsx(new Sheet("S", rows))));

        string unknown = Row(Text("A1", "before")) + Alternate(Row(Text("A2", "choice")), Row(Text("A2", "fallback")), requires: "unknown") + Row(Text("A3", "after"));

        Assert.Equal("Sheet: S\nbefore\nfallback\nafter\n", ExtractText(Xlsx(new Sheet("S", unknown))));
    }

    [Fact]
    public void Extract_HiddenRowInsideAlternateContent_IsHidden()
    {
        string hidden = "<row r=\"2\" hidden=\"1\">" + Text("A2", "HIDDENROWLEAK") + "</row>";

        string visible = "<row r=\"2\">" + Text("A2", "second") + "</row>";

        string rows = Row(Text("A1", "first")) + Alternate(hidden, visible) + Row(Text("A3", "third"));

        Assert.Equal("Sheet: S\nfirst\nthird\n", ExtractText(Xlsx(new Sheet("S", rows))));

        string fallbackHidden = Row(Text("A1", "first")) + Alternate(visible, hidden, requires: "unknown") + Row(Text("A3", "third"));

        Assert.Equal("Sheet: S\nfirst\nthird\n", ExtractText(Xlsx(new Sheet("S", fallbackHidden))));

        string fallbackVisible = Row(Text("A1", "first")) + Alternate(hidden, visible, requires: "unknown") + Row(Text("A3", "third"));

        Assert.Equal("Sheet: S\nfirst\nsecond\nthird\n", ExtractText(Xlsx(new Sheet("S", fallbackVisible))));
    }

    [Fact]
    public void Extract_CellsInsideAlternateContent_AreProjectedFromTheSelectedBranch()
    {
        string rows = $"<row>{Text("A1", "a")}{Alternate(Text("B1", "choice"), Text("B1", "fallback"))}{Text("C1", "c")}</row>";

        Assert.Equal("Sheet: S\na\tchoice\tc\n", ExtractText(Xlsx(new Sheet("S", rows))));

        string unknown = $"<row>{Text("A1", "a")}{Alternate(Text("B1", "choice"), Text("B1", "fallback"), requires: "unknown")}{Text("C1", "c")}</row>";

        Assert.Equal("Sheet: S\na\tfallback\tc\n", ExtractText(Xlsx(new Sheet("S", unknown))));
    }

    [Fact]
    public void Extract_CellInsideAlternateContent_StillObeysAHiddenColumn()
    {
        string rows = $"<row>{Text("A1", "a")}{Alternate(Text("B1", "HIDDENCOLUMNLEAK"))}{Text("C1", "c")}</row>";

        Assert.Equal("Sheet: S\na\tc\n", ExtractText(Xlsx(new Sheet("S", rows, Cols: HideB))));
    }

    [Fact]
    public void Extract_CellContentInsideAlternateContent_IsReadFromTheSelectedBranch()
    {
        string inline = Row($"<c r=\"A1\" t=\"inlineStr\">{Alternate("<is><t>choice</t></is>", "<is><t>fallback</t></is>")}</c>");

        Assert.Equal("Sheet: S\nchoice\n", ExtractText(Xlsx(new Sheet("S", inline))));

        string value = Row($"<c r=\"A1\">{Alternate("<v>1</v>", "<v>2</v>")}</c>");

        Assert.Equal("Sheet: S\n1\n", ExtractText(Xlsx(new Sheet("S", value))));

        string unknown = Row($"<c r=\"A1\">{Alternate("<v>1</v>", "<v>2</v>", requires: "unknown")}</c>");

        Assert.Equal("Sheet: S\n2\n", ExtractText(Xlsx(new Sheet("S", unknown))));
    }

    [Fact]
    public void Extract_RichTextRunsInsideAlternateContent_AreReadFromTheSelectedBranch()
    {
        string rows = Row($"<c r=\"A1\" t=\"inlineStr\"><is><t>one </t>{Alternate("<r><t>choice</t></r>", "<r><t>fallback</t></r>")}</is></c>");

        Assert.Equal("Sheet: S\none choice\n", ExtractText(Xlsx(new Sheet("S", rows))));
    }

    [Fact]
    public void Extract_RunTextInsideAlternateContent_IsReadFromTheSelectedBranch()
    {
        string rows = Row($"<c r=\"A1\" t=\"inlineStr\"><is><r><rPr/>{Alternate("<t>choice</t>", "<t>fallback</t>")}</r></is></c>");

        Assert.Equal("Sheet: S\nchoice\n", ExtractText(Xlsx(new Sheet("S", rows))));
    }

    [Fact]
    public void Extract_SharedStringItemsInsideAlternateContent_AreReadFromTheSelectedBranch()
    {
        string shared = Alternate(Si("choice"), Si("fallback")) + Si("second") + $"<si>{Alternate("<t>rich choice</t>", "<t>rich fallback</t>")}</si>";

        string rows = Row(Shared("A1", 0)) + Row(Shared("A2", 1)) + Row(Shared("A3", 2));

        Assert.Equal("Sheet: S\nchoice\nsecond\nrich choice\n", ExtractText(Xlsx([new Sheet("S", rows)], shared)));
    }

    // ------------------------------------------------------------ workbook children

    [Fact]
    public void Extract_WorkbookViewsInsideAlternateContent_AreJudgedFromTheSelectedBranch()
    {
        string hidden = "<bookViews>" + Alternate("<workbookView visibility=\"hidden\"/>", "<workbookView/>") + "</bookViews>";

        string visible = "<bookViews>" + Alternate("<workbookView visibility=\"hidden\"/>", "<workbookView/>", requires: "unknown") + "</bookViews>";

        Assert.Empty(Extract(Xlsx([new Sheet("S", Row(Text("A1", "HIDDENWINDOWLEAK")))], null, extraWorkbookXml: hidden)).Text);

        Assert.Equal("Sheet: S\nshown\n", ExtractText(Xlsx([new Sheet("S", Row(Text("A1", "shown")))], null, extraWorkbookXml: visible)));
    }

    [Fact]
    public void Extract_BookViewsOfAFallback_HideTheWorkbookWhenTheChoiceCannotBeTaken()
    {
        string bookViews = Alternate("<bookViews><workbookView/></bookViews>", "<bookViews><workbookView visibility=\"veryHidden\"/></bookViews>", requires: "unknown");

        Assert.Empty(Extract(Xlsx([new Sheet("S", Row(Text("A1", "HIDDENWINDOWLEAK")))], null, extraWorkbookXml: bookViews)).Text);
    }

    [Fact]
    public void Extract_BookViewsAfterTheSheetsInsideAlternateContent_StillHideTheWorkbook()
    {
        string bookViews = Alternate("<bookViews><workbookView visibility=\"hidden\"/></bookViews>");

        Assert.Empty(Extract(Xlsx([new Sheet("S", Row(Text("A1", "HIDDENWINDOWLEAK")))], null, extraWorkbookXmlAfterSheets: bookViews)).Text);
    }

    [Fact]
    public void Extract_SheetsInsideAlternateContent_AreProjected()
    {
        byte[] listed = Xlsx(new Sheet("Wrapped", Row(Text("A1", "shown")), WrapListing: element => Alternate(element)));

        byte[] wholeElement = Xlsx([new Sheet("Whole", Row(Text("A1", "also shown")))], null, wrapSheets: sheets => Alternate(sheets));

        Assert.Equal("Sheet: Wrapped\nshown\n", ExtractText(listed));

        Assert.Equal("Sheet: Whole\nalso shown\n", ExtractText(wholeElement));
    }

    [Fact]
    public void Extract_SheetStateInsideAlternateContent_IsJudgedFromTheSelectedBranch()
    {
        static string Wrap(string element, bool hiddenChoice, string requires) =>
            Alternate(hiddenChoice ? element.Replace("<sheet ", "<sheet state=\"hidden\" ") : element, hiddenChoice ? element : element.Replace("<sheet ", "<sheet state=\"hidden\" "), requires);

        byte[] package = Xlsx(
            new Sheet("Plain", Row(Text("A1", "plain"))),
            new Sheet("Choice hides", Row(Text("A1", "CHOICEHIDDENLEAK")), WrapListing: element => Wrap(element, hiddenChoice: true, "x14ac")),
            new Sheet("Choice shows", Row(Text("A1", "choice shown")), WrapListing: element => Wrap(element, hiddenChoice: false, "x14ac")),
            new Sheet("Fallback hides", Row(Text("A1", "FALLBACKHIDDENLEAK")), WrapListing: element => Wrap(element, hiddenChoice: false, "unknown")),
            new Sheet("Fallback shows", Row(Text("A1", "fallback shown")), WrapListing: element => Wrap(element, hiddenChoice: true, "unknown")));

        Assert.Equal("Sheet: Plain\nplain\nSheet: Choice shows\nchoice shown\nSheet: Fallback shows\nfallback shown\n", ExtractText(package));
    }

    // ------------------------------------------------------------ the enumerator itself

    private static BoundedXmlReader Worksheet(string body)
    {
        BoundedXmlReader xml = BoundedXmlReader.Create(
            Encoding.UTF8.GetBytes($"<worksheet xmlns=\"{Main}\" xmlns:mc=\"{McNamespace}\" xmlns:x14ac=\"{X14ac}\">{body}</worksheet>"),
            CancellationToken.None);

        SpreadsheetXml.EnterRoot(xml, "worksheet");

        return xml;
    }

    [Fact]
    public void MoveNext_DescendantsOfAnElementTheCallerLeftUnread_ArePassedOver()
    {
        // The caller steps into each element and never consumes it; the next call must not mistake a descendant, or the end
        // tag of a branch, for a child.
        using BoundedXmlReader xml = Worksheet(
            "<a><x><y/></x></a>"
            + "<mc:AlternateContent><mc:Choice Requires=\"x14ac\"><b><z><w/></z></b><mc:AlternateContent><mc:Choice Requires=\"x14ac\"><e><v/></e></mc:Choice></mc:AlternateContent><c/></mc:Choice><mc:Fallback><n/></mc:Fallback></mc:AlternateContent>"
            + "<d/>");

        Assert.True(SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children));

        List<string> names = [];

        while (children.MoveNext())
        {
            names.Add(xml.Reader.LocalName);

            xml.Read();
        }

        Assert.Equal(["a", "b", "e", "c", "d"], names);

        Assert.False(children.MoveNext());
    }

    [Fact]
    public void MoveNext_EmptyContainer_YieldsNothing()
    {
        using BoundedXmlReader xml = BoundedXmlReader.Create(Encoding.UTF8.GetBytes($"<worksheet xmlns=\"{Main}\"/>"), CancellationToken.None);

        SpreadsheetXml.EnterRoot(xml, "worksheet");

        Assert.False(SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children));

        Assert.False(children.MoveNext());
    }

    [Fact]
    public void MoveNext_AlternateContentWithNoChildren_YieldsNothingAndLeavesTheSiblings()
    {
        using BoundedXmlReader xml = Worksheet("<mc:AlternateContent/><a/><mc:AlternateContent></mc:AlternateContent><b/>");

        Assert.True(SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children));

        List<string> names = [];

        while (children.MoveNext())
        {
            names.Add(xml.Reader.LocalName);

            xml.SkipElement();
        }

        Assert.Equal(["a", "b"], names);
    }
}
