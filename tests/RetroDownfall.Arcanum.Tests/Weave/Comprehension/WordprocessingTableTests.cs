using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Table projection: a row is its cells joined by a tab and ended by a newline, the paragraphs of one cell are joined by
/// single spaces, a nested table flattens into its outer cell, and tracked-deleted rows and cells are not text.
/// </summary>
public sealed class WordprocessingTableTests
{
    private const string Revision = "w:id=\"5\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"";

    private static string P(string text) => WordprocessingFixtures.P(text);

    private static string Cell(string content, string? properties = null) =>
        "<w:tc>" + (properties is null ? string.Empty : $"<w:tcPr>{properties}</w:tcPr>") + content + "</w:tc>";

    private static string Row(string cells, string? properties = null) =>
        "<w:tr>" + (properties is null ? string.Empty : $"<w:trPr>{properties}</w:trPr>") + cells + "</w:tr>";

    private static string Table(params string[] rows) =>
        "<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"1000\"/></w:tblGrid>" + string.Concat(rows) + "</w:tbl>";

    private static string Extract(string body, string? styles = null) =>
        WordprocessingFixtures.Extract(WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(body),
            stylesXml: styles is null ? null : WordprocessingFixtures.Styles(styles)));

    // ---------------------------------------------------------------- row shape

    [Fact]
    public void Extract_TableRows_AreTheirCellsJoinedByATabAndEndedByANewline()
    {
        string table = Table(
            Row(Cell(P("Name")) + Cell(P("Role")) + Cell(P("Score"))),
            Row(Cell(P("Alice")) + Cell(P("Engineer")) + Cell(P("91"))));

        Assert.Equal("Name\tRole\tScore\nAlice\tEngineer\t91\n", Extract(table));
    }

    [Fact]
    public void Extract_ParagraphAfterATable_StartsOnItsOwnLine()
    {
        Assert.Equal("a\tb\nafter\n", Extract(Table(Row(Cell(P("a")) + Cell(P("b")))) + P("after")));
    }

    [Fact]
    public void Extract_EmptyCells_KeepTheirTabs()
    {
        string table = Table(
            Row(Cell(P("a")) + Cell("<w:p/>") + Cell(P("c"))),
            Row(Cell("<w:p/>") + Cell(P("b")) + Cell("<w:p/>")));

        Assert.Equal("a\t\tc\n\tb\t\n", Extract(table));
    }

    [Fact]
    public void Extract_ARowWithoutCells_IsAnEmptyLine()
    {
        Assert.Equal("a\n\nb\n", Extract(Table(Row(Cell(P("a"))), "<w:tr/>", Row(Cell(P("b"))))));
    }

    [Fact]
    public void Extract_ParagraphsInOneCell_AreJoinedBySingleSpacesAndEmptyOnesAddNothing()
    {
        // Sensitivity: the separator is owed only between text; an empty paragraph (every cell ends with one) adds none.
        string cell = Cell(P("one") + P("two") + "<w:p/>" + P("three") + "<w:p/>");

        Assert.Equal("one two three\tnext\n", Extract(Table(Row(cell + Cell(P("next"))))));
    }

    [Fact]
    public void Extract_TabsAndBreaksInACell_AreFoldedToSpacesSoTheRowStaysOnOneLine()
    {
        string cell = Cell("<w:p><w:r><w:t>a</w:t><w:tab/><w:t>b</w:t><w:br/><w:t>c</w:t><w:cr/><w:t>d</w:t></w:r></w:p>");

        Assert.Equal("a b c d\tz\n", Extract(Table(Row(cell + Cell(P("z"))))));
    }

    [Fact]
    public void Extract_CellsBeneathAContentControl_AreStillCells()
    {
        string row = Row("<w:sdt><w:sdtPr/><w:sdtContent>" + Cell(P("a")) + "</w:sdtContent></w:sdt>" + Cell(P("b")));

        Assert.Equal("a\tb\n", Extract(Table(row)));
    }

    [Fact]
    public void Extract_CellOutsideAnyRow_IsProjectedAsOrdinaryParagraphs()
    {
        Assert.Equal("one\ntwo\n", Extract(Cell(P("one") + P("two"))));
    }

    [Fact]
    public void Extract_RowMarksInsideFieldCode_AreNotProjected()
    {
        string begin = "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>";

        string separate = "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>";

        string end = "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";

        string table = Table(Row(
            Cell("<w:p>" + WordprocessingFixtures.R("a") + begin + "</w:p>")
            + Cell("<w:p>" + WordprocessingFixtures.R("CODE") + separate + WordprocessingFixtures.R("res") + end + "</w:p>")));

        Assert.Equal("ares\n", Extract(table));
    }

    [Fact]
    public void Extract_ParagraphsHiddenByAStyle_AddNothingToACell()
    {
        string styles = "<w:style w:type=\"paragraph\" w:styleId=\"Secret\"><w:rPr><w:vanish/></w:rPr></w:style>";

        string hidden = "<w:p><w:pPr><w:pStyle w:val=\"Secret\"/></w:pPr>" + WordprocessingFixtures.R("HIDDEN") + "</w:p>";

        Assert.Equal("shown\tz\n", Extract(Table(Row(Cell(hidden + P("shown") + hidden) + Cell(P("z")))), styles));
    }

    // ---------------------------------------------------------------- nested tables

    [Fact]
    public void Extract_NestedTable_IsFlattenedIntoItsOuterCellWithSingleSpaces()
    {
        string inner = Table(Row(Cell(P("a")) + Cell(P("b"))), Row(Cell(P("c")) + Cell(P("d"))));

        string outer = Table(Row(Cell(P("outer") + inner + "<w:p/>") + Cell(P("right"))));

        Assert.Equal("outer a b c d\tright\n", Extract(outer));
    }

    [Fact]
    public void Extract_NestedTableAtTheStartOfACell_AddsNoLeadingSpace()
    {
        string inner = Table(Row(Cell(P("a")) + Cell(P("b"))));

        Assert.Equal("a b tail\n", Extract(Table(Row(Cell(inner + P("tail"))))));
    }

    [Fact]
    public void Extract_TableNestedTwoLevelsDeep_IsFlattenedToo()
    {
        string deepest = Table(Row(Cell(P("deep"))));

        string middle = Table(Row(Cell(P("mid") + deepest + P("mid end"))));

        Assert.Equal("top mid deep mid end\tside\n", Extract(Table(Row(Cell(P("top") + middle) + Cell(P("side"))))));
    }

    [Fact]
    public void Extract_TextBoxInACell_IsFlattenedIntoIt()
    {
        string box = "<w:r><w:pict><v:shape><v:textbox><w:txbxContent>" + P("b") + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

        string cell = Cell("<w:p>" + WordprocessingFixtures.R("a") + box + WordprocessingFixtures.R("c") + "</w:p>");

        Assert.Equal("a b c\n", Extract(Table(Row(cell))));
    }

    [Fact]
    public void Extract_TableInATextBox_KeepsItsRowShape()
    {
        string table = Table(Row(Cell(P("x")) + Cell(P("y"))));

        string box = "<w:r><w:pict><v:shape><v:textbox><w:txbxContent>" + table + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

        Assert.Equal("lead\nx\ty\n\n", Extract("<w:p>" + WordprocessingFixtures.R("lead") + box + "</w:p>"));
    }

    // ---------------------------------------------------------------- tracked deletions of rows and cells

    [Fact]
    public void Extract_TrackedDeletedRow_IsNotProjected()
    {
        string table = Table(
            Row(Cell(P("kept row"))),
            Row(Cell(P("DELETED_ROW_LEAK")), $"<w:del {Revision}/>"));

        Assert.Equal("kept row\n", Extract(table));
    }

    [Fact]
    public void Extract_TrackedDeletedRow_LeavesNoRowEndBehind()
    {
        string table = Table(Row(Cell(P("a")), $"<w:del {Revision}/>"), Row(Cell(P("b")), $"<w:del {Revision}/>"), Row(Cell(P("c"))));

        Assert.Equal("c\n", Extract(table));
    }

    [Fact]
    public void Extract_TrackedInsertedRow_IsProjected()
    {
        Assert.Equal("inserted\n", Extract(Table(Row(Cell(P("inserted")), $"<w:ins {Revision}/>"))));
    }

    [Fact]
    public void Extract_TrackedDeletedCell_IsNotProjectedAndLeavesNoTabBehind()
    {
        string row = Row(Cell(P("kept cell")) + Cell(P("DELETED_CELL_LEAK"), $"<w:cellDel {Revision}/>") + Cell(P("last")));

        Assert.Equal("kept cell\tlast\n", Extract(Table(row)));
    }

    [Fact]
    public void Extract_TrackedDeletedFirstCell_LeavesNoLeadingTab()
    {
        string row = Row(Cell(P("DELETED_FIRST"), $"<w:cellDel {Revision}/>") + Cell(P("b")));

        Assert.Equal("b\n", Extract(Table(row)));
    }

    [Fact]
    public void Extract_TrackedInsertedAndMergedCells_AreProjected()
    {
        string row = Row(Cell(P("inserted"), $"<w:cellIns {Revision}/>") + Cell(P("merged"), "<w:vMerge w:val=\"restart\"/><w:gridSpan w:val=\"2\"/>"));

        Assert.Equal("inserted\tmerged\n", Extract(Table(row)));
    }

    [Fact]
    public void Extract_RowWhoseCellsAreAllDeleted_IsJustTheRowEnd()
    {
        string row = Row(Cell(P("GONE_A"), $"<w:cellDel {Revision}/>") + Cell(P("GONE_B"), $"<w:cellDel {Revision}/>"));

        Assert.Equal("\nafter\n", Extract(Table(row) + P("after")));
    }

    [Fact]
    public void Extract_DeletionRecordedInAPropertiesChange_IsNotCurrent()
    {
        // The recorded previous properties of a tracked change are not the current ones.
        string row = Row(
            Cell(P("row shown"), $"<w:tcPrChange {Revision}><w:tcPr><w:cellDel {Revision}/></w:tcPr></w:tcPrChange>"),
            $"<w:trPrChange {Revision}><w:trPr><w:del {Revision}/></w:trPr></w:trPrChange>");

        Assert.Equal("row shown\n", Extract(Table(row)));
    }

    [Fact]
    public void Extract_DeletionInsideAlternateContentInRowAndCellProperties_IsHonoured()
    {
        string wrapped(string deletion) => $"<mc:AlternateContent><mc:Choice Requires=\"w14\">{deletion}</mc:Choice><mc:Fallback>{deletion}</mc:Fallback></mc:AlternateContent>";

        string table = Table(
            Row(Cell(P("kept")) + Cell(P("DELETED_CELL"), wrapped($"<w:cellDel {Revision}/>"))),
            Row(Cell(P("DELETED_ROW")), wrapped($"<w:del {Revision}/>")));

        Assert.Equal("kept\n", Extract(table));
    }

    [Fact]
    public void Extract_RangeMarkupBeforeTheRowProperties_DoesNotHideTheDeletion()
    {
        string row = "<w:tr><w:bookmarkStart w:id=\"1\" w:name=\"m\"/><w:trPr><w:del " + Revision + "/></w:trPr>" + Cell(P("DELETED")) + "</w:tr>";

        Assert.Equal("kept\n", Extract(Table(Row(Cell(P("kept"))), row)));
    }

    [Fact]
    public void Extract_RowOrCellPropertiesAfterTheContentTheyGovern_IsFormatMismatch()
    {
        // The deletion flag would be read after text may have been emitted, so the part is refused.
        string lateRow = "<w:tr>" + Cell(P("EMITTED_EARLY")) + "<w:trPr><w:del " + Revision + "/></w:trPr></w:tr>";

        string lateCell = "<w:tr><w:tc>" + P("EMITTED_EARLY") + "<w:tcPr><w:cellDel " + Revision + "/></w:tcPr></w:tc></w:tr>";

        string repeatedRow = "<w:tr><w:trPr/><w:trPr><w:del " + Revision + "/></w:trPr>" + Cell(P("x")) + "</w:tr>";

        string repeatedCell = "<w:tr><w:tc><w:tcPr/><w:tcPr><w:cellDel " + Revision + "/></w:tcPr>" + P("x") + "</w:tc></w:tr>";

        foreach (string row in new[] { lateRow, lateCell, repeatedRow, repeatedCell })
        {
            Assert.Equal(
                DocumentPackageRejection.FormatMismatch,
                WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx("<w:tbl><w:tblPr/>" + row + "</w:tbl>")));
        }
    }

    [Fact]
    public void Extract_DeletedRowHoldingATableOfItsOwn_ProjectsNeitherTheRowNorTheTable()
    {
        string inner = Table(Row(Cell(P("INNER_DELETED"))));

        string table = Table(Row(Cell(P("DELETED") + inner), $"<w:del {Revision}/>"), Row(Cell(P("kept"))));

        Assert.Equal("kept\n", Extract(table));
    }
}
