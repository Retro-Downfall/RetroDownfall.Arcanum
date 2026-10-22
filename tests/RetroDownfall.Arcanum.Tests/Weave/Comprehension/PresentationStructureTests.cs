namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Where PowerPoint text may sit: tables, text bodies, paragraphs and runs. Text outside that structure, inside
/// extension lists, or in merged-away table cells is not something a viewer sees, so it is not projected.
/// </summary>
public sealed class PresentationStructureTests
{
    private static string Paragraph(string text) => PresentationDeck.Paragraph(text);

    private static string Shape(int id, string paragraphs, string properties = "") => PresentationDeck.Shape(id, paragraphs, properties);

    private static string Slide(string shapes) => PresentationDeck.Slide(shapes);

    private static string Cell(string paragraphs, string attributes = "") =>
        $"<a:tc{attributes}><a:txBody><a:bodyPr/><a:lstStyle/>{paragraphs}</a:txBody><a:tcPr/></a:tc>";

    private static string Row(params string[] cells) => $"<a:tr h=\"370840\">{string.Concat(cells)}</a:tr>";

    private static string Table(string rows, string properties = "") =>
        $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Table\"{properties}/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm/>"
        + $"<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr/><a:tblGrid/>{rows}</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";

    // ---------------------------------------------------------------- tables

    [Fact]
    public async Task Extract_Table_ProjectsEveryCellRowByRow()
    {
        string table = Table(
            Row(Cell(Paragraph("A1")), Cell(Paragraph("B1")))
            + Row(Cell(Paragraph("A2") + Paragraph("A2 second")), Cell("<a:p/>")));

        Assert.Equal(
            "Slide 1\nA1\nB1\nA2\nA2 second\n\n",
            await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(table))));
    }

    [Fact]
    public async Task Extract_TableNextToATextShape_KeepsDocumentOrder()
    {
        string shapes = Shape(2, Paragraph("Title")) + Table(Row(Cell(Paragraph("Cell")))) + Shape(5, Paragraph("Footer"));

        Assert.Equal("Slide 1\nTitle\nCell\nFooter\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_HiddenTable_IsExcluded()
    {
        string shapes = Shape(2, Paragraph("Before")) + Table(Row(Cell(Paragraph("HIDDENTABLETEXT"))), " hidden=\"1\"") + Shape(5, Paragraph("After"));

        Assert.Equal("Slide 1\nBefore\nAfter\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Theory]
    [InlineData(" hMerge=\"1\"")]
    [InlineData(" vMerge=\"1\"")]
    [InlineData(" hMerge=\"true\"")]
    [InlineData(" vMerge=\"TRUE\"")]
    public async Task Extract_MergedAwayTableCell_IsExcluded(string merge)
    {
        // A cell covered by a horizontal or vertical merge is not drawn, so text planted in it is not visible.
        string table = Table(
            Row(Cell(Paragraph("Spanning"), " gridSpan=\"2\""), Cell(Paragraph("COVEREDCELLTEXT"), merge))
            + Row(Cell(Paragraph("Left")), Cell(Paragraph("Right"))));

        Assert.Equal(
            "Slide 1\nSpanning\nLeft\nRight\n",
            await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(table))));
    }

    [Theory]
    [InlineData(" hMerge=\"0\"")]
    [InlineData(" vMerge=\"false\"")]
    public async Task Extract_TableCellExplicitlyNotMerged_IsIncluded(string merge)
    {
        string table = Table(Row(Cell(Paragraph("Kept"), merge)));

        Assert.Equal("Slide 1\nKept\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(table))));
    }

    [Fact]
    public async Task Extract_ConsecutiveMergedAwayTableCells_AreAllExcluded()
    {
        // Sensitivity: after skipping a covered cell, the next node (the next cell's start tag) must still be visited.
        string table = Table(
            Row(
                Cell(Paragraph("Spanning"), " gridSpan=\"3\""),
                Cell(Paragraph("COVEREDONE"), " hMerge=\"1\""),
                Cell(Paragraph("COVEREDTWO"), " hMerge=\"1\""),
                Cell(Paragraph("Last"))));

        Assert.Equal("Slide 1\nSpanning\nLast\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(table))));
    }

    [Fact]
    public async Task Extract_EmptyMergedAwayTableCell_DoesNotDisturbTheCellsAfterIt()
    {
        string table = Table(Row(Cell(Paragraph("One"), " gridSpan=\"2\""), "<a:tc hMerge=\"1\"/>", Cell(Paragraph("Three"))));

        Assert.Equal("Slide 1\nOne\nThree\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(table))));
    }

    // ---------------------------------------------------------------- extension and customer data lists

    [Fact]
    public async Task Extract_ExtensionListsAndCustomData_AreNotProjected()
    {
        string shape =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Shape\"><a:extLst><a:ext uri=\"{FF2B5EF4}\">"
            + $"<p:txBody>{Paragraph("DRAWINGEXTENSION")}</p:txBody></a:ext></a:extLst></p:cNvPr><p:cNvSpPr/><p:nvPr><p:custDataLst>"
            + $"<p:tags><p:txBody>{Paragraph("CUSTOMDATA")}</p:txBody></p:tags></p:custDataLst></p:nvPr></p:nvSpPr>"
            + $"<p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>{Paragraph("Visible")}</p:txBody>"
            + $"<p:extLst><p:ext uri=\"{{BB962C8B}}\"><p:txBody>{Paragraph("SHAPEEXTENSION")}</p:txBody></p:ext></p:extLst></p:sp>";

        string slide = Slide(shape).Replace(
            "</p:cSld>",
            $"<p:extLst><p:ext uri=\"{{DEADBEEF}}\"><p:sp><p:txBody>{Paragraph("SLIDEEXTENSION")}</p:txBody></p:sp></p:ext></p:extLst></p:cSld>",
            StringComparison.Ordinal);

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(slide)));
    }

    // ---------------------------------------------------------------- text only where text can be

    [Fact]
    public async Task Extract_TextRunOutsideAParagraph_IsNotProjected()
    {
        string stray = "<a:t>STRAYTEXT</a:t><a:r><a:t>STRAYRUN</a:t></a:r><a:br/>";

        string shapes = stray + $"<p:txBody>{stray}</p:txBody>" + Shape(2, Paragraph("Visible"));

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_ParagraphOutsideATextBody_IsNotProjected()
    {
        string shapes = Paragraph("STRAYPARAGRAPH") + "<a:p/>" + Shape(2, Paragraph("Visible"))
            + "<p:sp><p:spPr>" + Paragraph("INSHAPEPROPERTIES") + "</p:spPr></p:sp>";

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_TextOfAFieldOutsideAParagraph_IsNotProjected()
    {
        string shapes = "<p:txBody><a:fld id=\"{1}\" type=\"slidenum\"><a:t>STRAYFIELD</a:t></a:fld></p:txBody>" + Shape(2, Paragraph("Visible"));

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_ElementsNestedInsideATextRun_DoNotContributeText()
    {
        string paragraph = "<a:p><a:r><a:t>one<a:x>NESTEDTEXT</a:x>three</a:t></a:r></a:p>";

        Assert.Equal("Slide 1\nonethree\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(Shape(2, paragraph)))));
    }

    [Fact]
    public async Task Extract_ElementAfterATextRunInTheSameRun_DoesNotContributeText()
    {
        // Sensitivity: closing the text run on its end tag. A stale "inside a:t" state would read the sibling's text.
        string paragraph = "<a:p><a:r><a:t>kept</a:t><a:x>STALETEXT</a:x></a:r></a:p>";

        Assert.Equal("Slide 1\nkept\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(Shape(2, paragraph)))));
    }

    [Fact]
    public async Task Extract_HiddenShapeNestedInsideATextRun_DoesNotLeaveTheRunOpen()
    {
        // The hidden-shape skip swallows the run's own end tag here; the open-run state must not outlive it.
        string paragraph =
            "<a:p><a:r><a:t>VISIBLE<p:nvSpPr><p:cNvPr id=\"9\" name=\"n\" hidden=\"1\"/></p:nvSpPr></a:t><a:x>STALETEXT</a:x></a:r></a:p>";

        Assert.Equal("Slide 1\nVISIBLE\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(Shape(2, paragraph)))));
    }

    [Fact]
    public async Task Extract_HiddenAttributeOnAnElementThatIsNotAShapesNonVisualProperties_HidesNothing()
    {
        string shapes = "<p:cNvPr id=\"9\" name=\"stray\" hidden=\"1\"/>" + Shape(2, Paragraph("Visible"));

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_EmptyTextElement_DoesNotLeaveTheRunOpen()
    {
        string paragraph = "<a:p><a:r><a:t/><a:x>STALETEXT</a:x></a:r></a:p>";

        Assert.Equal("Slide 1\n\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(Shape(2, paragraph)))));
    }

    [Fact]
    public async Task Extract_ElementSharingADepthWithAnEarlierParagraph_AddsNoLineBreak()
    {
        // Sensitivity: the element kind recorded for a depth is reset by every element that opens there.
        string paragraphs = Paragraph("one") + "<a:x><a:y/></a:x>" + Paragraph("two");

        Assert.Equal("Slide 1\none\ntwo\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(Shape(2, paragraphs)))));
    }

    [Fact]
    public async Task Extract_TextBodyInTheDrawingNamespace_IsProjected()
    {
        // Table cells carry their text in a:txBody rather than p:txBody; the cell test above relies on it too.
        string shape = $"<p:sp><p:spPr/><a:txBody><a:bodyPr/><a:lstStyle/>{Paragraph("Drawing body")}</a:txBody></p:sp>";

        Assert.Equal("Slide 1\nDrawing body\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shape))));
    }
}
