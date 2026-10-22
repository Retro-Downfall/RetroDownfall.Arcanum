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
