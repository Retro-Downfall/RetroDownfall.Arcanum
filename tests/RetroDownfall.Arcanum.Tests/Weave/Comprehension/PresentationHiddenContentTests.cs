using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Hidden-content posture (plan D8) for PowerPoint: only text a viewer sees in the slide show is projected.
/// Each test names the hiding mechanism and proves the hidden text is absent while its visible neighbours remain.
/// </summary>
public sealed class PresentationHiddenContentTests
{
    private static string Paragraph(string text) => PresentationDeck.Paragraph(text);

    private static string Shape(int id, string paragraphs, string properties = "") => PresentationDeck.Shape(id, paragraphs, properties);

    private static string Slide(string shapes, string rootAttributes = "") => PresentationDeck.Slide(shapes, rootAttributes);

    private static string AlternateContent(string choices, string fallback) =>
        $"<mc:AlternateContent xmlns:mc=\"{PresentationDeck.Mc}\">{choices}<mc:Fallback>{fallback}</mc:Fallback></mc:AlternateContent>";

    private static string Choice(string content) =>
        $"<mc:Choice xmlns:a14=\"http://schemas.microsoft.com/office/drawing/2010/main\" Requires=\"a14\">{content}</mc:Choice>";

    // ---------------------------------------------------------------- hidden slides

    [Theory]
    [InlineData(" show=\"0\"")]
    [InlineData(" show=\"false\"")]
    [InlineData(" show=\"False\"")]
    [InlineData(" show=\" 0 \"")]
    public async Task Extract_HiddenSlide_IsExcludedButStillCountsForNumbering(string showAttribute)
    {
        byte[] deck = PresentationDeck.Deck(
            Slide(Shape(2, Paragraph("First"))),
            Slide(Shape(2, Paragraph("HIDDENSLIDETEXT")), showAttribute),
            Slide(Shape(2, Paragraph("Third"))));

        Assert.Equal("Slide 1\nFirst\nSlide 3\nThird\n", await PresentationDeck.TextOfAsync(deck));
    }

    [Theory]
    [InlineData(" show=\"1\"")]
    [InlineData(" show=\"true\"")]
    public async Task Extract_SlideMarkedShown_IsIncluded(string showAttribute)
    {
        Assert.Equal(
            "Slide 1\nShown\n",
            await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(Shape(2, Paragraph("Shown")), showAttribute))));
    }

    [Fact]
    public async Task Extract_EveryVisibleSlideHidden_ProducesNoText()
    {
        byte[] deck = PresentationDeck.Deck(
            Slide(Shape(2, Paragraph("HIDDEN ONE")), " show=\"0\""),
            Slide(Shape(2, Paragraph("HIDDEN TWO")), " show=\"0\""));

        SessionAttachmentExtractionResult result = await PresentationDeck.ExtractAsync(deck);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Equal(string.Empty, result.Text);
    }

    // ---------------------------------------------------------------- hidden shapes

    [Theory]
    [InlineData(" hidden=\"1\"")]
    [InlineData(" hidden=\"true\"")]
    [InlineData(" hidden=\"TRUE\"")]
    [InlineData(" hidden=\" 1 \"")]
    public async Task Extract_HiddenShape_IsExcludedWhileItsNeighboursStay(string hidden)
    {
        string shapes = Shape(2, Paragraph("Before")) + Shape(3, Paragraph("HIDDENSHAPETEXT"), hidden) + Shape(4, Paragraph("After"));

        Assert.Equal("Slide 1\nBefore\nAfter\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Theory]
    [InlineData(" hidden=\"0\"")]
    [InlineData(" hidden=\"false\"")]
    public async Task Extract_ShapeExplicitlyNotHidden_IsIncluded(string hidden)
    {
        Assert.Equal(
            "Slide 1\nSeen\n",
            await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(Shape(2, Paragraph("Seen"), hidden)))));
    }

    [Theory]
    [InlineData("sp", "nvSpPr")]
    [InlineData("grpSp", "nvGrpSpPr")]
    [InlineData("pic", "nvPicPr")]
    [InlineData("graphicFrame", "nvGraphicFramePr")]
    [InlineData("cxnSp", "nvCxnSpPr")]
    public async Task Extract_HiddenShapeOfEveryKind_IsExcluded(string shape, string nonVisual)
    {
        // The text body inside pic/cxnSp is not schema-valid; it proves the container is recognised as hideable.
        string hiddenShape =
            $"<p:{shape}><p:{nonVisual}><p:cNvPr id=\"3\" name=\"Hidden\" hidden=\"1\"/></p:{nonVisual}>"
            + $"<p:txBody><a:bodyPr/><a:lstStyle/>{Paragraph("HIDDENKINDTEXT")}</p:txBody></p:{shape}>";

        string shapes = Shape(2, Paragraph("Before")) + hiddenShape + Shape(4, Paragraph("After"));

        Assert.Equal("Slide 1\nBefore\nAfter\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    // ---------------------------------------------------------------- shape visibility does not depend on element order

    private const string HiddenNonVisual = "<p:nvSpPr><p:cNvPr id=\"2\" name=\"x\" hidden=\"1\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>";

    private static string TextBody(string paragraphs) => $"<p:txBody><a:bodyPr/><a:lstStyle/>{paragraphs}</p:txBody>";

    private static string AlternateContentOf(string content) =>
        $"<mc:AlternateContent xmlns:mc=\"{PresentationDeck.Mc}\" xmlns:a14=\"http://schemas.microsoft.com/office/drawing/2010/main\">"
        + $"<mc:Choice Requires=\"a14\">{content}</mc:Choice><mc:Fallback>{content}</mc:Fallback></mc:AlternateContent>";

    [Fact]
    public async Task Extract_TextBodyBeforeHiddenNonVisualProperties_IsNotProjected()
    {
        // The text arrives before the shape says it is hidden, so it must be held back until the shape has ended.
        string shapes = $"<p:sp>{TextBody(Paragraph("TXBODYFIRSTLEAK"))}{HiddenNonVisual}<p:spPr/></p:sp>" + Shape(3, Paragraph("shown"));

        Assert.Equal("Slide 1\nshown\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_HiddenNonVisualPropertiesInsideAlternateContent_HideTheShape()
    {
        // The wrapper, not the shape, is the parent of the non-visual properties; the shape is still the one hidden.
        string shapes = $"<p:sp>{AlternateContentOf(HiddenNonVisual)}<p:spPr/>{TextBody(Paragraph("ACNVSPPRLEAK"))}</p:sp>" + Shape(3, Paragraph("shown"));

        Assert.Equal("Slide 1\nshown\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_HiddenNonVisualPropertiesInsideAlternateContentAfterTheTextBody_HideTheShape()
    {
        string shapes = $"<p:sp>{TextBody(Paragraph("ACLATELEAK"))}{AlternateContentOf(HiddenNonVisual)}<p:spPr/></p:sp>" + Shape(3, Paragraph("shown"));

        Assert.Equal("Slide 1\nshown\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_HiddenMarkerWrappedInAlternateContentInsideTheNonVisualProperties_HidesTheShape()
    {
        string wrapped = AlternateContentOf("<p:cNvPr id=\"2\" name=\"x\" hidden=\"1\"/>");

        string shapes = $"<p:sp><p:nvSpPr>{wrapped}<p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/>{TextBody(Paragraph("WRAPPEDMARKERLEAK"))}</p:sp>"
            + Shape(3, Paragraph("shown"));

        Assert.Equal("Slide 1\nshown\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Theory]
    [InlineData("sp", "nvSpPr")]
    [InlineData("grpSp", "nvGrpSpPr")]
    [InlineData("pic", "nvPicPr")]
    [InlineData("graphicFrame", "nvGraphicFramePr")]
    [InlineData("cxnSp", "nvCxnSpPr")]
    public async Task Extract_HiddenShapeOfEveryKindWhoseTextComesFirst_IsExcluded(string shape, string nonVisual)
    {
        string hiddenShape =
            $"<p:{shape}>{TextBody(Paragraph("TEXTFIRSTKINDLEAK"))}<p:{nonVisual}><p:cNvPr id=\"3\" name=\"Hidden\" hidden=\"1\"/></p:{nonVisual}></p:{shape}>";

        string shapes = Shape(2, Paragraph("Before")) + hiddenShape + Shape(4, Paragraph("After"));

        Assert.Equal("Slide 1\nBefore\nAfter\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_HiddenGroupWhoseNonVisualPropertiesComeLast_HidesEveryChild()
    {
        string inner = $"<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"6\" name=\"Inner\"/></p:nvGrpSpPr><p:grpSpPr/>{Shape(7, Paragraph("DEEPLATECHILD"))}</p:grpSp>";

        string hiddenGroup =
            "<p:grpSp>" + Shape(4, Paragraph("LATECHILD")) + inner
            + "<p:nvGrpSpPr><p:cNvPr id=\"3\" name=\"Outer\" hidden=\"1\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:grpSp>";

        string shapes = Shape(2, Paragraph("Before")) + hiddenGroup + Shape(8, Paragraph("After"));

        Assert.Equal("Slide 1\nBefore\nAfter\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_HiddenRootGroupWhoseNonVisualPropertiesComeLast_HidesTheWholeShapeTree()
    {
        string slide = Slide(Shape(2, Paragraph("UNDERLATEHIDDENROOT"))).Replace(
            "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>",
            string.Empty,
            StringComparison.Ordinal).Replace(
            "</p:spTree>",
            "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\" hidden=\"1\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr></p:spTree>",
            StringComparison.Ordinal);

        Assert.Equal("Slide 1\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(slide)));
    }

    [Fact]
    public async Task Extract_HiddenTableWhoseNonVisualPropertiesComeLast_IsExcluded()
    {
        string table =
            "<p:graphicFrame><p:xfrm/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr/><a:tblGrid/>"
            + $"<a:tr h=\"1\"><a:tc>{TextBody(Paragraph("LATETABLELEAK")).Replace("p:txBody", "a:txBody", StringComparison.Ordinal)}<a:tcPr/></a:tc></a:tr>"
            + "</a:tbl></a:graphicData></a:graphic>"
            + "<p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Table\" hidden=\"1\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr></p:graphicFrame>";

        string shapes = Shape(2, Paragraph("Before")) + table + Shape(5, Paragraph("After"));

        Assert.Equal("Slide 1\nBefore\nAfter\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_HiddenMarkerNestedInsideATextRun_HidesTheEnclosingShapeAndNothingMore()
    {
        // Not where the schema puts non-visual properties, so the most cautious reading wins: the shape holding it is hidden.
        string paragraph =
            "<a:p><a:r><a:t>NESTEDMARKERLEAK<p:nvSpPr><p:cNvPr id=\"9\" name=\"n\" hidden=\"1\"/></p:nvSpPr></a:t><a:x>STALETEXT</a:x></a:r></a:p>";

        string shapes = Shape(2, paragraph) + Shape(3, Paragraph("shown"));

        Assert.Equal("Slide 1\nshown\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_HiddenShapeLargerThanTheTextBuffer_DoesNotCostTheVisibleShapesAfterIt()
    {
        string huge = new('h', 300_000);

        string shapes = Shape(2, Paragraph("Before"))
            + Shape(3, Paragraph(huge), " hidden=\"1\"")
            + $"<p:sp>{TextBody(Paragraph(huge))}{HiddenNonVisual}<p:spPr/></p:sp>"
            + Shape(4, Paragraph("After"));

        SessionAttachmentExtractionResult result = await PresentationDeck.ExtractAsync(PresentationDeck.Deck(Slide(shapes)));

        Assert.Equal("Slide 1\nBefore\nAfter\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public async Task Extract_VisibleShapeLargerThanTheTextBuffer_IsCutAtTheSinkCeilingAndReportsTruncation()
    {
        string shapes = Shape(2, Paragraph(new string('v', 300_000))) + Shape(3, Paragraph("AFTERTHECEILING"));

        SessionAttachmentExtractionResult result = await PresentationDeck.ExtractAsync(PresentationDeck.Deck(Slide(shapes)));

        Assert.True(result.WasTruncated);

        Assert.Equal(262_144, result.Text.Length);

        Assert.DoesNotContain("AFTERTHECEILING", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_HiddenGroup_HidesEveryChildAtAnyDepth()
    {
        string inner = $"<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"6\" name=\"Inner\"/></p:nvGrpSpPr><p:grpSpPr/>{Shape(7, Paragraph("DEEPCHILD"))}</p:grpSp>";

        string hiddenGroup =
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"3\" name=\"Outer\" hidden=\"1\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>"
            + Shape(4, Paragraph("GROUPCHILD")) + inner + "</p:grpSp>";

        string shapes = Shape(2, Paragraph("Before")) + hiddenGroup + Shape(8, Paragraph("After"));

        Assert.Equal("Slide 1\nBefore\nAfter\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_HiddenChildOfAVisibleGroup_IsExcludedWithoutHidingItsSiblings()
    {
        string group =
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"3\" name=\"Group\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>"
            + Shape(4, Paragraph("First child"))
            + Shape(5, Paragraph("HIDDENCHILD"), " hidden=\"1\"")
            + Shape(6, Paragraph("Last child"))
            + "</p:grpSp>";

        string shapes = group + Shape(7, Paragraph("After the group"));

        Assert.Equal(
            "Slide 1\nFirst child\nLast child\nAfter the group\n",
            await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_HiddenRootGroup_HidesTheWholeShapeTree()
    {
        // Conservative: a hidden shape-tree group is treated like any hidden group, so nothing under it is projected.
        string slide = Slide(Shape(2, Paragraph("UNDERHIDDENROOT"))).Replace(
            "<p:cNvPr id=\"1\" name=\"\"/>",
            "<p:cNvPr id=\"1\" name=\"\" hidden=\"1\"/>",
            StringComparison.Ordinal);

        Assert.Equal("Slide 1\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(slide)));
    }

    [Fact]
    public async Task Extract_HiddenShapeInsideAnAlternateContentChoice_IsExcluded()
    {
        string shapes = AlternateContent(
            Choice(Shape(2, Paragraph("Chosen")) + Shape(3, Paragraph("HIDDENCHOICE"), " hidden=\"1\"")),
            Shape(4, Paragraph("Fallback")));

        Assert.Equal("Slide 1\nChosen\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    // ---------------------------------------------------------------- markup compatibility

    [Fact]
    public async Task Extract_AlternateContent_ProjectsTheChoiceAndNeverTheFallback()
    {
        string shapes = AlternateContent(Choice(Shape(2, Paragraph("Rich text"))), Shape(3, Paragraph("FALLBACKCOPY")));

        Assert.Equal("Slide 1\nRich text\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_AlternateContentWithSeveralChoices_ProjectsOnlyTheFirst()
    {
        string shapes = AlternateContent(
            Choice(Shape(2, Paragraph("First choice"))) + Choice(Shape(3, Paragraph("SECONDCHOICE"))),
            Shape(4, Paragraph("FALLBACKCOPY")));

        Assert.Equal("Slide 1\nFirst choice\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_AlternateContentWithoutAChoice_ProjectsNothing()
    {
        string shapes = Shape(2, Paragraph("Before")) + $"<mc:AlternateContent xmlns:mc=\"{PresentationDeck.Mc}\"><mc:Fallback>{Shape(3, Paragraph("FALLBACKONLY"))}</mc:Fallback></mc:AlternateContent>";

        Assert.Equal("Slide 1\nBefore\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_ConsecutiveAlternateContentBlocks_EachProjectItsOwnFirstChoice()
    {
        // Sensitivity: the first-choice flag is per block, so a second block must not inherit the first one's state.
        string shapes =
            AlternateContent(Choice(Shape(2, Paragraph("One"))), Shape(3, Paragraph("FALLBACKONE")))
            + AlternateContent(Choice(Shape(4, Paragraph("Two"))), Shape(5, Paragraph("FALLBACKTWO")));

        Assert.Equal("Slide 1\nOne\nTwo\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Fact]
    public async Task Extract_NestedAlternateContent_AppliesFirstChoiceAtEveryLevel()
    {
        string inner = AlternateContent(
            Choice(Shape(3, Paragraph("Inner choice"))) + Choice(Shape(4, Paragraph("INNERSECOND"))),
            Shape(5, Paragraph("INNERFALLBACK")));

        string shapes = AlternateContent(
            Choice(Shape(2, Paragraph("Outer choice")) + inner + Shape(6, Paragraph("After inner"))),
            Shape(7, Paragraph("OUTERFALLBACK")));

        Assert.Equal(
            "Slide 1\nOuter choice\nInner choice\nAfter inner\n",
            await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    [Theory]
    [InlineData("Choice")]
    [InlineData("Fallback")]
    public async Task Extract_ChoiceOrFallbackOutsideAlternateContent_IsNotProjected(string element)
    {
        string shapes = Shape(2, Paragraph("Visible"))
            + $"<mc:{element} xmlns:mc=\"{PresentationDeck.Mc}\" Requires=\"a14\">{Shape(3, Paragraph("STRAYCOMPATIBILITY"))}</mc:{element}>";

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }
}
