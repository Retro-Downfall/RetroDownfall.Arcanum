using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// D8 hidden-content rules for Visio shapes and pages: HideText, invisible layers, locally deleted shapes, guides and
/// pages hidden from the Visio user interface. Each rule has a fixture whose hidden text carries a distinct marker.
/// </summary>
public sealed class VisioDrawingExtractorHiddenContentTests
{
    // ---------------------------------------------------------------- HideText

    [Theory]
    [InlineData("1")]
    [InlineData("TRUE")]
    [InlineData("true")]
    [InlineData("2")]
    [InlineData(" 1 ")]
    public void Extract_HideTextTrue_ExcludesThatShapesText(string value)
    {
        byte[] package = Page(
            VisioFixture.Shape(1, "Before")
            + VisioFixture.Shape(2, "HIDDEN-TEXT", cells: VisioFixture.Cell("HideText", value))
            + VisioFixture.Shape(3, "After"));

        Assert.Equal("Page: P\nBefore\nAfter\n", VisioFixture.Extract(package).Text);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("FALSE")]
    [InlineData("false")]
    [InlineData("0.0")]
    public void Extract_HideTextFalse_KeepsTheText(string value)
    {
        byte[] package = Page(VisioFixture.Shape(1, "Shown", cells: VisioFixture.Cell("HideText", value)));

        Assert.Equal("Page: P\nShown\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_HideTextOnAGroupHidesOnlyTheGroupsOwnTextNotItsSubShapes()
    {
        byte[] package = Page(VisioFixture.Shape(
            1,
            "HIDDEN-GROUP-TEXT",
            type: "Group",
            cells: VisioFixture.Cell("HideText", "1"),
            children: VisioFixture.Shape(2, "Child") + VisioFixture.Shape(3, "HIDDEN-CHILD", cells: VisioFixture.Cell("HideText", "1"))));

        Assert.Equal("Page: P\nChild\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_HideTextCellThatArrivesAfterTheTextIsStillHonoured()
    {
        // Forward-only reading buffers the shape until its end, so element order cannot be used to smuggle text.
        const string Shape = "<Shape ID='1' Type='Shape'><Text>HIDDEN-LATE</Text><Cell N='HideText' V='1'/></Shape>";

        byte[] package = Page(Shape + VisioFixture.Shape(2, "Kept"));

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_HideTextCellAfterTheSubShapesStillHidesOnlyTheGroupText()
    {
        const string Group = "<Shape ID='1' Type='Group'><Text>HIDDEN-GROUP</Text><Shapes><Shape ID='2' Type='Shape'><Text>Child</Text></Shape></Shapes><Cell N='HideText' V='1'/></Shape>";

        Assert.Equal("Page: P\nChild\n", VisioFixture.Extract(Page(Group)).Text);
    }

    [Theory]
    [InlineData("<Cell N='HideText' V='maybe'/>")]
    [InlineData("<Cell N='HideText' F='GUARD(1)'/>")]
    [InlineData("<Cell N='HideText' V='' F='=Width&gt;1'/>")]
    public void Extract_HideTextThatIsUnparsableOrAnUnevaluatedFormula_ExcludesConservatively(string cell)
    {
        byte[] package = Page($"<Shape ID='1' Type='Shape'>{cell}<Text>HIDDEN-UNKNOWN</Text></Shape>" + VisioFixture.Shape(2, "Kept"));

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Theory]
    [InlineData("<Cell N='HideText' F='Inh'/>")]
    [InlineData("<Cell N='HideText' V=''/>")]
    [InlineData("<Cell N='HideText'/>")]
    public void Extract_HideTextCellThatSpecifiesNothing_DoesNotHide(string cell)
    {
        byte[] package = Page($"<Shape ID='1' Type='Shape'>{cell}<Text>Shown</Text></Shape>");

        Assert.Equal("Page: P\nShown\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_HideTextValueLongerThanAnyBoolean_IsUnreadableAndHidesConservatively()
    {
        // 65 zeros parse as the number 0, but no real boolean cell is that long: it is treated as unreadable, which hides.
        byte[] longValue = Page($"<Shape ID='1' Type='Shape'><Cell N='HideText' V='{new string('0', 65)}'/><Text>HIDDEN-LONG-VALUE</Text></Shape>");

        byte[] boundaryValue = Page($"<Shape ID='1' Type='Shape'><Cell N='HideText' V='{new string('0', 64)}'/><Text>Shown</Text></Shape>");

        Assert.Equal("Page: P\n", VisioFixture.Extract(longValue).Text);

        Assert.Equal("Page: P\nShown\n", VisioFixture.Extract(boundaryValue).Text);
    }

    [Fact]
    public void Extract_DuplicateHideTextCells_AnyTrueWins()
    {
        byte[] package = Page(VisioFixture.Shape(
            1,
            "HIDDEN-DUPLICATE",
            cells: VisioFixture.Cell("HideText", "1") + VisioFixture.Cell("HideText", "0")));

        Assert.Equal("Page: P\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_HideTextCellInAnotherNamespaceIsNotAHideTextCell()
    {
        byte[] package = Page($"<Shape ID='1' Type='Shape'><Cell xmlns='urn:other' N='HideText' V='1'/><Text>Shown</Text></Shape>");

        Assert.Equal("Page: P\nShown\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- layers

    [Fact]
    public void Extract_ShapesOnAnInvisibleLayerAreExcludedAndAnyInvisibleLayerOfSeveralHidesTheShape()
    {
        string layers = VisioFixture.LayerSection(
            VisioFixture.LayerRow(0, "Visible", "1"),
            VisioFixture.LayerRow(1, "Hidden", "0"),
            VisioFixture.LayerRow(2, "AlsoVisible", "1"));

        string shapes =
            VisioFixture.Shape(1, "On layer zero", cells: VisioFixture.Cell("LayerMember", "0"))
            + VisioFixture.Shape(2, "HIDDEN-LAYER-ONE", cells: VisioFixture.Cell("LayerMember", "1"))
            + VisioFixture.Shape(3, "On zero and two", cells: VisioFixture.Cell("LayerMember", "0;2"))
            + VisioFixture.Shape(4, "HIDDEN-ZERO-AND-ONE", cells: VisioFixture.Cell("LayerMember", "0;1"))
            + VisioFixture.Shape(5, "HIDDEN-ONE-AND-TWO", cells: VisioFixture.Cell("LayerMember", " 2 ; 1 "))
            + VisioFixture.Shape(6, "On no layer");

        byte[] package = Page(shapes, layers);

        Assert.Equal("Page: P\nOn layer zero\nOn zero and two\nOn no layer\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_LayerVisibleCellAcceptsTrueFalseAndDefaultsToVisibleWhenAbsent()
    {
        string layers = VisioFixture.LayerSection(
            VisioFixture.LayerRow(0, "Absent", null),
            VisioFixture.LayerRow(1, "False", "FALSE"),
            VisioFixture.LayerRow(2, "True", "TRUE"),
            VisioFixture.LayerRow(3, "Unparsable", "who knows"));

        string shapes =
            VisioFixture.Shape(1, "Zero", cells: VisioFixture.Cell("LayerMember", "0"))
            + VisioFixture.Shape(2, "HIDDEN-ONE", cells: VisioFixture.Cell("LayerMember", "1"))
            + VisioFixture.Shape(3, "Two", cells: VisioFixture.Cell("LayerMember", "2"))
            + VisioFixture.Shape(4, "HIDDEN-THREE", cells: VisioFixture.Cell("LayerMember", "3"));

        Assert.Equal("Page: P\nZero\nTwo\n", VisioFixture.Extract(Page(shapes, layers)).Text);
    }

    [Fact]
    public void Extract_GroupOnAnInvisibleLayerHidesItsSubShapesToo()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        string group = VisioFixture.Shape(
            1,
            "HIDDEN-GROUP",
            type: "Group",
            cells: VisioFixture.Cell("LayerMember", "0"),
            children: VisioFixture.Shape(2, "HIDDEN-CHILD") + VisioFixture.Shape(3, "HIDDEN-GRANDPARENT", children: VisioFixture.Shape(4, "HIDDEN-GRANDCHILD")));

        byte[] package = Page(group + VisioFixture.Shape(5, "Outside"), layers);

        Assert.Equal("Page: P\nOutside\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_SubShapeOnAnInvisibleLayerIsHiddenWhileItsVisibleGroupAndSiblingsStay()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        string group = VisioFixture.Shape(
            1,
            "Group",
            type: "Group",
            children: VisioFixture.Shape(2, "HIDDEN-CHILD", cells: VisioFixture.Cell("LayerMember", "0")) + VisioFixture.Shape(3, "Sibling"));

        Assert.Equal("Page: P\nGroup\nSibling\n", VisioFixture.Extract(Page(group, layers)).Text);
    }

    [Fact]
    public void Extract_LayerMemberCellThatArrivesAfterTheTextAndSubShapes_StillHidesAllOfThem()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        const string Group = "<Shape ID='1' Type='Group'><Text>HIDDEN-GROUP</Text><Shapes><Shape ID='2'><Text>HIDDEN-CHILD</Text></Shape></Shapes><Cell N='LayerMember' V='0'/></Shape>";

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(Page(Group + VisioFixture.Shape(3, "Kept"), layers)).Text);
    }

    [Fact]
    public void Extract_SubShapesOfAGroupOnAnInvisibleLayer_AreNeverReadSoTheirMastersAreNeverResolved()
    {
        // The master part is malformed: resolving it would fail, so success proves the hidden group's children were skipped.
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        string group = VisioFixture.Shape(
            1,
            type: "Group",
            cells: VisioFixture.Cell("LayerMember", "0"),
            children: VisioFixture.Shape(2, attributes: "Master='2'"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "MASTER-TEXT"))
            .AddPage("P", group + VisioFixture.Shape(3, "Kept"), pageSheet: layers)
            .Replace("visio/masters/master1.xml", "<broken")
            .Build();

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_TextOfAShapeOnAnInvisibleLayer_IsNeverBufferedSoOversizeHiddenTextCannotTruncateTheResult()
    {
        // 1.2 million characters would overflow the per-shape buffer if they were buffered before being discarded.
        Random random = new(5);

        string huge = string.Create(1_200_000, random, static (span, source) =>
        {
            for (int index = 0; index < span.Length; index++)
            {
                span[index] = (char)('a' + source.Next(26));
            }
        });

        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        byte[] package = Page(VisioFixture.Shape(1, huge, cells: VisioFixture.Cell("LayerMember", "0")) + VisioFixture.Shape(2, "After"), layers);

        SessionAttachmentExtractionResult result = VisioFixture.Extract(package);

        Assert.Equal("Page: P\nAfter\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void Extract_OnlyTheFirstTextElementOfAShapeIsProjected()
    {
        byte[] package = Page("<Shape ID='1' Type='Shape'><Text>First</Text><Text>SECOND-TEXT</Text></Shape>");

        Assert.Equal("Page: P\nFirst\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_LayerMemberNamingALayerThePageDoesNotDefine_DoesNotHide()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        byte[] package = Page(VisioFixture.Shape(1, "Shown", cells: VisioFixture.Cell("LayerMember", "7;99999")), layers);

        Assert.Equal("Page: P\nShown\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_LayerMemberEntriesPaddedWithSpaces_AreReadAsTheirIndices()
    {
        string layers = VisioFixture.LayerSection(
            VisioFixture.LayerRow(0, "Visible", "1"),
            VisioFixture.LayerRow(1, "Hidden", "0"),
            VisioFixture.LayerRow(2, "AlsoVisible", "1"));

        byte[] package = Page(VisioFixture.Shape(1, "Shown on zero and two", cells: VisioFixture.Cell("LayerMember", " 0 ; 2 ")), layers);

        Assert.Equal("Page: P\nShown on zero and two\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_LayerMemberWithAnUnparsableEntry_HidesConservativelyOnlyWhenThePageHasAnInvisibleLayer()
    {
        string withInvisible = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        string withoutInvisible = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Fine", "1"));

        string shape = VisioFixture.Shape(1, "MAYBE-HIDDEN", cells: VisioFixture.Cell("LayerMember", "1;x"));

        Assert.Equal("Page: P\n", VisioFixture.Extract(Page(shape, withInvisible)).Text);

        Assert.Equal("Page: P\nMAYBE-HIDDEN\n", VisioFixture.Extract(Page(shape, withoutInvisible)).Text);
    }

    [Theory]
    [InlineData("<Cell N='LayerMember' F='GUARD(1)'/>")]
    [InlineData("<Cell N='LayerMember' V='' F='=Sheet.1!LayerMember'/>")]
    public void Extract_LayerMemberThatIsAnUnevaluatedFormula_HidesConservativelyOnlyWhenThePageHasAnInvisibleLayer(string cell)
    {
        // A formula with no cached value could resolve to any layer, so the shape is held back while a layer is invisible.
        string withInvisible = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        string withoutInvisible = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Fine", "1"));

        string shape = $"<Shape ID='1' Type='Shape'>{cell}<Text>MAYBE-HIDDEN</Text></Shape>";

        Assert.Equal("Page: P\n", VisioFixture.Extract(Page(shape, withInvisible)).Text);

        Assert.Equal("Page: P\nMAYBE-HIDDEN\n", VisioFixture.Extract(Page(shape, withoutInvisible)).Text);
    }

    [Theory]
    [InlineData("<Cell N='LayerMember' F='Inh'/>")]
    [InlineData("<Cell N='LayerMember' V=''/>")]
    [InlineData("<Cell N='LayerMember'/>")]
    public void Extract_LayerMemberThatSpecifiesNothing_DoesNotHide(string cell)
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        string shape = $"<Shape ID='1' Type='Shape'>{cell}<Text>Shown</Text></Shape>";

        Assert.Equal("Page: P\nShown\n", VisioFixture.Extract(Page(shape, layers)).Text);
    }

    [Fact]
    public void Extract_LayerVisibilityBelongsToItsOwnPage()
    {
        string hidden = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Layer", "0"));

        string visible = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Layer", "1"));

        string shape = VisioFixture.Shape(1, "On layer zero", cells: VisioFixture.Cell("LayerMember", "0"));

        byte[] package = new VisioPackageBuilder()
            .AddPage("One", shape, pageSheet: hidden)
            .AddPage("Two", shape, pageSheet: visible)
            .AddPage("Three", shape)
            .Build();

        Assert.Equal("Page: One\nPage: Two\nOn layer zero\nPage: Three\nOn layer zero\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_LayerMemberWithoutAnyLayerSection_DoesNotHide()
    {
        byte[] package = Page(VisioFixture.Shape(1, "Shown", cells: VisioFixture.Cell("LayerMember", "0")));

        Assert.Equal("Page: P\nShown\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_InvisibleLayerIndexBeyondTheSupportedRange_IsStructureBounds()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(8192, "Far", "0"));

        Assert.Equal(DocumentPackageRejection.StructureBounds, VisioFixture.Reject(Page(VisioFixture.Shape(1, "x"), layers)));
    }

    [Fact]
    public void Extract_LastSupportedInvisibleLayerIndexStillHides()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(8191, "Far", "0"));

        byte[] package = Page(VisioFixture.Shape(1, "HIDDEN-FAR", cells: VisioFixture.Cell("LayerMember", "8191")) + VisioFixture.Shape(2, "Kept"), layers);

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_VisibleLayerRowBeyondTheSupportedRange_IsHarmless()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(100000, "Far", "1"));

        Assert.Equal("Page: P\nx\n", VisioFixture.Extract(Page(VisioFixture.Shape(1, "x"), layers)).Text);
    }

    [Theory]
    [InlineData("<Row><Cell N='Visible' V='0'/></Row>")]
    [InlineData("<Row IX='-1'><Cell N='Visible' V='0'/></Row>")]
    [InlineData("<Row IX='abc'><Cell N='Visible' V='0'/></Row>")]
    public void Extract_LayerRowWithoutAUsableIndex_IsMalformedXmlBecauseItsVisibilityCouldNotBeApplied(string row)
    {
        string layers = $"<Section N='Layer'>{row}</Section>";

        Assert.Equal(DocumentPackageRejection.MalformedXml, VisioFixture.Reject(Page(VisioFixture.Shape(1, "x"), layers)));
    }

    [Fact]
    public void Extract_OnlyTheLayerSectionOfThePageSheetDefinesLayers()
    {
        // Another named section whose rows happen to carry a Visible cell must not be mistaken for the layer table.
        const string Other = "<Section N='Scratch'><Row IX='0'><Cell N='Visible' V='0'/></Row></Section>";

        byte[] package = Page(VisioFixture.Shape(1, "Shown", cells: VisioFixture.Cell("LayerMember", "0")), Other);

        Assert.Equal("Page: P\nShown\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_LayerSectionsOutsideThePageSheetAreIgnored()
    {
        // A Layer section inside a shape (not a page) defines nothing the page can reference.
        string shape = "<Shape ID='1' Type='Shape'><Section N='Layer'><Row IX='0'><Cell N='Visible' V='0'/></Row></Section><Cell N='LayerMember' V='0'/><Text>Shown</Text></Shape>";

        Assert.Equal("Page: P\nShown\n", VisioFixture.Extract(Page(shape)).Text);
    }

    // ---------------------------------------------------------------- locally deleted shapes and guides

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public void Extract_LocallyDeletedShape_IsExcludedWithItsSubShapes(string del)
    {
        byte[] package = Page(
            VisioFixture.Shape(1, "HIDDEN-DELETED", attributes: $"Del='{del}'", children: VisioFixture.Shape(2, "HIDDEN-DELETED-CHILD"))
            + VisioFixture.Shape(3, "Kept"));

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    public void Extract_ShapeWhoseDelIsFalse_IsKept(string del)
    {
        byte[] package = Page(VisioFixture.Shape(1, "Kept", attributes: $"Del='{del}'"));

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_GuideShapeText_IsExcluded()
    {
        byte[] package = Page(VisioFixture.Shape(1, "HIDDEN-GUIDE", type: "Guide") + VisioFixture.Shape(2, "Kept"));

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- pages hidden from the user interface

    [Fact]
    public void Extract_PageHiddenFromTheUserInterface_IsExcludedEntirelyAndItsPartIsNeverRead()
    {
        // The hidden page's part is malformed: reading it would fail, so success proves it was not opened.
        string hidden = "<Cell N='UIVisibility' V='1'/>";

        byte[] package = new VisioPackageBuilder()
            .AddPage("Shown", VisioFixture.Shape(1, "Visible text"))
            .AddPage("Hidden", VisioFixture.Shape(1, "HIDDEN-PAGE-TEXT"), pageSheet: hidden)
            .AddPage("Also shown", VisioFixture.Shape(1, "More"))
            .Replace("visio/pages/page2.xml", "<broken")
            .Build();

        Assert.Equal("Page: Shown\nVisible text\nPage: Also shown\nMore\n", VisioFixture.Extract(package).Text);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("FALSE")]
    public void Extract_PageWhoseUiVisibilityIsNormal_IsKept(string value)
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Shown", VisioFixture.Shape(1, "Visible text"), pageSheet: VisioFixture.Cell("UIVisibility", value))
            .Build();

        Assert.Equal("Page: Shown\nVisible text\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_HiddenPagesStillCountTowardThePageLimit()
    {
        string hidden = VisioFixture.Cell("UIVisibility", "1");

        byte[] package = new VisioPackageBuilder()
            .AddPage("A", VisioFixture.Shape(1, "One"))
            .AddPage("B", VisioFixture.Shape(1, "Two"), pageSheet: hidden)
            .AddPage("C", VisioFixture.Shape(1, "Three"))
            .Build();

        Assert.Equal(DocumentPackageRejection.StructureBounds, VisioFixture.Reject(package, new VisioDrawingExtractor(maximumPages: 2)));
    }

    private static byte[] Page(string shapes, string pageSheet = "") =>
        new VisioPackageBuilder().AddPage("P", shapes, pageSheet).Build();
}
