using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// D8 master inheritance: a shape that has no Text of its own takes the text of its master shape, once per instance,
/// where the instance appears. Master parts are never projected on their own.
/// </summary>
public sealed class VisioDrawingExtractorMasterTests
{
    // ---------------------------------------------------------------- required acceptance fixture

    [Fact]
    public void Extract_StencilLabelLivesOnlyOnTheMaster_AppearsOncePerInstanceBearingPageAndNeverStandalone()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "STENCIL-LABEL"))
            .AddMaster(3, VisioFixture.Shape(6, "UNUSED-MASTER-LABEL"))
            .AddPage("Page 1", Instance(1, master: 2) + VisioFixture.Shape(2, "Page one note"))
            .AddPage("Page 2", VisioFixture.Shape(1, "Page two only"))
            .AddPage("Page 3", Instance(1, master: 2))
            .Build();

        string text = VisioFixture.Extract(package).Text;

        Assert.Equal("Page: Page 1\nSTENCIL-LABEL\nPage one note\nPage: Page 2\nPage two only\nPage: Page 3\nSTENCIL-LABEL\n", text);

        Assert.DoesNotContain("UNUSED-MASTER-LABEL", text);
    }

    [Fact]
    public void Extract_TwoMastersWithTheSameId_TheFirstOneInMastersXmlIsTheMaster()
    {
        // Master IDs are unique in a valid package; for an invalid one the choice is deterministic, never the last writer.
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "FIRST-MASTER"))
            .AddMaster(2, VisioFixture.Shape(5, "SECOND-MASTER"))
            .AddPage("P", Instance(1, master: 2))
            .Build();

        Assert.Equal("Page: P\nFIRST-MASTER\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_TwoInstancesOnOnePage_EmitTheInheritedTextTwice()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddPage("P", Instance(1, master: 2) + Instance(2, master: 2))
            .Build();

        Assert.Equal("Page: P\nLabel\nLabel\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- instance text versus master text

    [Fact]
    public void Extract_InstanceWithItsOwnText_UsesItInsteadOfTheMastersAndEmitsTheMastersNowhere()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "MASTER-TEXT"))
            .AddPage("P", Instance(1, master: 2, text: "Own text"))
            .Build();

        Assert.Equal("Page: P\nOwn text\n", VisioFixture.Extract(package).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<cp IX='0'/>")]
    public void Extract_InstanceWithAnEmptyTextElement_OverridesTheMastersTextToEmpty(string emptyText)
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "MASTER-TEXT"))
            .AddPage("P", Instance(1, master: 2, text: emptyText) + Instance(2, master: 2))
            .Build();

        Assert.Equal("Page: P\nMASTER-TEXT\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_SelfClosingTextElementOnAnInstance_OverridesTheMastersTextToEmpty()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "MASTER-TEXT"))
            .AddPage("P", "<Shape ID='1' Type='Shape' Master='2'><Text/></Shape>")
            .Build();

        Assert.Equal("Page: P\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_InheritedTextFollowsTheSameMixedContentRulesAsInstanceText()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "<cp IX='0'/>Rev <fld IX='0'>7</fld><pp IX='0'/> final"))
            .AddPage("P", Instance(1, master: 2))
            .Build();

        Assert.Equal("Page: P\nRev 7 final\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterWithSeveralTopLevelShapes_LendsNothingToATopLevelInstance()
    {
        // With several top-level shapes there is no one shape the instance stands for: it inherits nothing for itself.
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "FIRST") + VisioFixture.Shape(6, "SECOND"))
            .AddPage("P", Instance(1, master: 2) + Instance(2, master: 2, text: "Own"))
            .Build();

        Assert.Equal("Page: P\nOwn\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterWithSeveralTopLevelShapes_StillLendsToSubShapesThroughMasterShape()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "FIRST") + VisioFixture.Shape(6, "SECOND"))
            .AddPage("P", Instance(1, master: 2, children: SubShape(2, masterShape: 6) + SubShape(3, masterShape: 5)))
            .Build();

        Assert.Equal("Page: P\nSECOND\nFIRST\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterWithSeveralTopLevelShapes_LendsNoHidingCellToATopLevelInstanceEither()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        string master =
            VisioFixture.Shape(5, "FIRST", cells: VisioFixture.Cell("HideText", "1") + VisioFixture.Cell("LayerMember", "0"))
            + VisioFixture.Shape(6, "SECOND");

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, master)
            .AddPage("P", Instance(1, master: 2, text: "Own"), pageSheet: layers)
            .Build();

        Assert.Equal("Page: P\nOwn\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterTopLevelShapeWithoutText_InheritsNothingEvenIfALaterShapeHasText()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5) + VisioFixture.Shape(6, "SECOND"))
            .AddPage("P", Instance(1, master: 2))
            .Build();

        Assert.Equal("Page: P\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterWhoseOnlyLiveTopLevelShapeSitsBesideADeletedOrGuideOne_LendsThatShape()
    {
        // A deleted shape and a guide are not drawn, so they are not top-level shapes the instance could stand for.
        string master =
            VisioFixture.Shape(4, "DELETED-FIRST", attributes: "Del='1'")
            + VisioFixture.Shape(5, "GUIDE", type: "Guide")
            + VisioFixture.Shape(6, "THE-LIVE-SHAPE");

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, master)
            .AddPage("P", Instance(1, master: 2))
            .Build();

        Assert.Equal("Page: P\nTHE-LIVE-SHAPE\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- sub-shapes of an instantiated group

    [Fact]
    public void Extract_GroupInstanceSubShapes_InheritTheTextOfTheirMasterShapeById()
    {
        string masterGroup = VisioFixture.Shape(
            10,
            "Master group label",
            type: "Group",
            children: VisioFixture.Shape(11, "Master child A") + VisioFixture.Shape(12, "Master child B") + VisioFixture.Shape(13, "Master child C"));

        string instance = Instance(
            1,
            master: 3,
            children:
                SubShape(2, masterShape: 12)
                + SubShape(3, masterShape: 11)
                + SubShape(4, masterShape: 13, text: "Overridden C")
                + SubShape(5, masterShape: 99));

        byte[] package = new VisioPackageBuilder().AddMaster(3, masterGroup).AddPage("P", instance).Build();

        Assert.Equal("Page: P\nMaster group label\nMaster child B\nMaster child A\nOverridden C\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_SubShapeLookupIsScopedToItsOwnGroupsMaster()
    {
        string masterOne = VisioFixture.Shape(10, "One", type: "Group", children: VisioFixture.Shape(11, "One child"));

        string masterTwo = VisioFixture.Shape(10, "Two", type: "Group", children: VisioFixture.Shape(11, "Two child"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(1, masterOne)
            .AddMaster(2, masterTwo)
            .AddPage("P", Instance(1, master: 2, children: SubShape(2, masterShape: 11)) + Instance(3, master: 1, children: SubShape(4, masterShape: 11)))
            .Build();

        Assert.Equal("Page: P\nTwo\nTwo child\nOne\nOne child\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_NestedGroupSubShapes_KeepTheMasterOfTheInstanceTheyAreInside()
    {
        string master = VisioFixture.Shape(
            10,
            "Top",
            type: "Group",
            children: VisioFixture.Shape(11, "Inner group", type: "Group", children: VisioFixture.Shape(12, "Deep")));

        string instance = Instance(
            1,
            master: 4,
            children: SubShape(2, masterShape: 11, children: SubShape(3, masterShape: 12)));

        byte[] package = new VisioPackageBuilder().AddMaster(4, master).AddPage("P", instance).Build();

        Assert.Equal("Page: P\nTop\nInner group\nDeep\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_InstanceNestedInsideAPlainGroup_InheritsFromItsOwnMaster()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Nested label"))
            .AddPage("P", VisioFixture.Shape(1, "Plain group", type: "Group", children: Instance(2, master: 2) + VisioFixture.Shape(3, "Sibling")))
            .Build();

        Assert.Equal("Page: P\nPlain group\nNested label\nSibling\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_SubShapeWithMasterShapeButNoEnclosingMaster_InheritsNothing()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(10, "Top", type: "Group", children: VisioFixture.Shape(11, "MASTER-CHILD")))
            .AddPage("P", VisioFixture.Shape(1, type: "Group", children: SubShape(2, masterShape: 11)))
            .Build();

        Assert.Equal("Page: P\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_SubShapeOfAnInstanceThatHasNoMasterShape_InheritsNothingNotTheMastersTopLevelShape()
    {
        // A shape added locally inside an instantiated group has no counterpart in the master: it must not borrow the
        // master's top-level label just because its group is an instance.
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "MASTER-TOP-LABEL"))
            .AddPage("P", Instance(1, master: 2, children: VisioFixture.Shape(2) + VisioFixture.Shape(3, "Local note")))
            .Build();

        Assert.Equal("Page: P\nMASTER-TOP-LABEL\nLocal note\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterShapeAttributeThatNamesNoShapeOfTheMaster_InheritsNothing()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddPage("P", Instance(1, master: 2, children: SubShape(2, masterShape: 77)))
            .Build();

        Assert.Equal("Page: P\nLabel\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- HideText through the master

    [Fact]
    public void Extract_MasterHideText_HidesTheInheritedText()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "HIDDEN-MASTER-TEXT", cells: VisioFixture.Cell("HideText", "1")))
            .AddPage("P", Instance(1, master: 2) + VisioFixture.Shape(2, "Kept"))
            .Build();

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterHideTextThatArrivesAfterTheMasterText_IsStillHonoured()
    {
        const string MasterShape = "<Shape ID='5' Type='Shape'><Text>HIDDEN-LATE-MASTER</Text><Cell N='HideText' V='1'/></Shape>";

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, MasterShape)
            .AddPage("P", Instance(1, master: 2) + VisioFixture.Shape(2, "Kept"))
            .Build();

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterSubShapeHideText_HidesOnlyThatSubShapesInheritedText()
    {
        string master = VisioFixture.Shape(
            10,
            "Group label",
            type: "Group",
            children: VisioFixture.Shape(11, "HIDDEN-SUB", cells: VisioFixture.Cell("HideText", "1")) + VisioFixture.Shape(12, "Visible sub"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, master)
            .AddPage("P", Instance(1, master: 2, children: SubShape(2, masterShape: 11) + SubShape(3, masterShape: 12)))
            .Build();

        Assert.Equal("Page: P\nGroup label\nVisible sub\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_InstanceThatInheritsHideTextFromItsMaster_HidesItsOwnTextToo()
    {
        // The HideText cell is inherited like any other cell: an instance with its own text and no HideText of its own is hidden.
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Master", cells: VisioFixture.Cell("HideText", "1")))
            .AddPage("P", Instance(1, master: 2, text: "HIDDEN-OWN-TEXT") + VisioFixture.Shape(2, "Kept"))
            .Build();

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_InstanceThatSetsHideTextFalse_ShowsItsOwnTextButDoesNotRecoverAHiddenMastersText()
    {
        // Conservative simplification: a hiding master's text is not retained, so an instance that overrides HideText to
        // false still gets nothing inherited. Its own text is its own, and the explicit false lets it show.
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "HIDDEN-MASTER-LABEL", cells: VisioFixture.Cell("HideText", "1")))
            .AddPage("P", Instance(1, master: 2, cells: VisioFixture.Cell("HideText", "0")) + Instance(2, master: 2, text: "Own", cells: VisioFixture.Cell("HideText", "0")))
            .Build();

        Assert.Equal("Page: P\nOwn\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_InstanceHideText_HidesItsInheritedTextAndLeavesTheOtherInstancesIntact()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Inherited"))
            .AddPage("P", Instance(1, master: 2, cells: VisioFixture.Cell("HideText", "1")) + VisioFixture.Shape(3, "Between") + Instance(2, master: 2))
            .Build();

        Assert.Equal("Page: P\nBetween\nInherited\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- other rules apply to inherited text

    [Fact]
    public void Extract_InstanceOnAnInvisibleLayer_InheritsNothingAndNeverResolvesItsMaster()
    {
        // The master part is malformed: resolving it would fail, so success proves a hidden instance does not read it.
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "MASTER-TEXT"))
            .AddPage("P", Instance(1, master: 2, cells: VisioFixture.Cell("LayerMember", "0")), pageSheet: layers)
            .Replace("visio/masters/master1.xml", "<broken")
            .Build();

        Assert.Equal("Page: P\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_InstanceWithoutALayerMemberCell_InheritsTheInvisibleLayerOfItsMasterShape()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "MASTER-ON-HIDDEN-LAYER", cells: VisioFixture.Cell("LayerMember", "0")))
            .AddPage("P", Instance(1, master: 2) + VisioFixture.Shape(3, "Kept"), pageSheet: layers)
            .Build();

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_InstanceWithItsOwnTextOnAnInheritedInvisibleLayer_IsHiddenToo()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"), VisioFixture.LayerRow(1, "Shown", "1"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Master", cells: VisioFixture.Cell("LayerMember", "1;0")))
            .AddPage("P", Instance(1, master: 2, text: "HIDDEN-OWN-TEXT") + VisioFixture.Shape(3, "Kept"), pageSheet: layers)
            .Build();

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_InstanceThatSetsItsOwnLayerMember_OverridesTheMastersLayerMembership()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"), VisioFixture.LayerRow(1, "Shown", "1"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Master label", cells: VisioFixture.Cell("LayerMember", "0")))
            .AddPage("P", Instance(1, master: 2, text: "Own", cells: VisioFixture.Cell("LayerMember", "1")), pageSheet: layers)
            .Build();

        Assert.Equal("Page: P\nOwn\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_InstanceWhoseLayerMemberSpecifiesNothing_StillInheritsTheMastersLayerMembership()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "MASTER-ON-HIDDEN-LAYER", cells: VisioFixture.Cell("LayerMember", "0")))
            .AddPage("P", Instance(1, master: 2, cells: "<Cell N='LayerMember' V='' F='Inh'/>") + VisioFixture.Shape(3, "Kept"), pageSheet: layers)
            .Build();

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterShapeOnAVisibleLayerOrOnAPageWithoutInvisibleLayers_ShowsTheInstance()
    {
        string visible = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"), VisioFixture.LayerRow(1, "Shown", "1"));

        string none = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Shown", "1"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Master label", cells: VisioFixture.Cell("LayerMember", "1")))
            .AddMaster(3, VisioFixture.Shape(5, "Other label", cells: VisioFixture.Cell("LayerMember", "0")))
            .AddPage("One", Instance(1, master: 2), pageSheet: visible)
            .AddPage("Two", Instance(1, master: 3), pageSheet: none)
            .Build();

        Assert.Equal("Page: One\nMaster label\nPage: Two\nOther label\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterShapeLayerMemberFormulaWithoutAValue_HidesTheInstanceConservativelyOnlyWhenThePageHasAnInvisibleLayer()
    {
        string withInvisible = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        string withoutInvisible = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Fine", "1"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "MAYBE-HIDDEN", cells: "<Cell N='LayerMember' F='GUARD(1)'/>"))
            .AddPage("One", Instance(1, master: 2), pageSheet: withInvisible)
            .AddPage("Two", Instance(1, master: 2), pageSheet: withoutInvisible)
            .Build();

        Assert.Equal("Page: One\nPage: Two\nMAYBE-HIDDEN\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterGroupOnAnInvisibleLayer_HidesTheWholeInstanceSubtree()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        string masterGroup = VisioFixture.Shape(
            10,
            "HIDDEN-GROUP",
            type: "Group",
            cells: VisioFixture.Cell("LayerMember", "0"),
            children: VisioFixture.Shape(11, "HIDDEN-MEMBER"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(3, masterGroup)
            .AddPage("P", Instance(1, master: 3, children: SubShape(2, masterShape: 11)) + VisioFixture.Shape(4, "Kept"), pageSheet: layers)
            .Build();

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_SubShapeInheritingAnInvisibleLayerFromItsMasterShape_IsHiddenWhileTheGroupAndSiblingsStay()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        string masterGroup = VisioFixture.Shape(
            10,
            "Group label",
            type: "Group",
            children: VisioFixture.Shape(11, "HIDDEN-MEMBER", cells: VisioFixture.Cell("LayerMember", "0")) + VisioFixture.Shape(12, "Member B"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(3, masterGroup)
            .AddPage("P", Instance(1, master: 3, children: SubShape(2, masterShape: 11) + SubShape(3, masterShape: 12)), pageSheet: layers)
            .Build();

        Assert.Equal("Page: P\nGroup label\nMember B\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterLayerMemberLongerThanTheRetentionBound_HidesConservativelyOnlyWhenThePageHasAnInvisibleLayer()
    {
        string withInvisible = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"), VisioFixture.LayerRow(1, "Shown", "1"));

        string withoutInvisible = VisioFixture.LayerSection(VisioFixture.LayerRow(1, "Shown", "1"));

        // 600 entries naming only the visible layer 1: far beyond the length kept per master shape, so it is not known to be safe.
        string longMembership = string.Join(';', Enumerable.Repeat("1", 600));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "LONG-MEMBERSHIP", cells: VisioFixture.Cell("LayerMember", longMembership)))
            .AddMaster(3, VisioFixture.Shape(5, "Short membership", cells: VisioFixture.Cell("LayerMember", "1;1;1")))
            .AddPage("One", Instance(1, master: 2) + Instance(2, master: 3), pageSheet: withInvisible)
            .AddPage("Two", Instance(1, master: 2), pageSheet: withoutInvisible)
            .Build();

        Assert.Equal("Page: One\nShort membership\nPage: Two\nLONG-MEMBERSHIP\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterLayerMembershipBeyondTheTotalRetentionBudget_HidesConservativelyOnceTheBudgetIsSpent()
    {
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"), VisioFixture.LayerRow(1, "Shown", "1"));

        // Each member keeps 400 characters of visible-layer membership; 200 of them pass the 65,536-character budget.
        string membership = string.Join(';', Enumerable.Repeat("1", 200));

        string members = string.Concat(Enumerable.Range(1, 200).Select(id => VisioFixture.Shape(id, $"Member {id}", cells: VisioFixture.Cell("LayerMember", membership))));

        string masterGroup = VisioFixture.Shape(500, type: "Group", children: members);

        string instanceMembers = string.Concat(Enumerable.Range(1, 200).Select(id => SubShape(1000 + id, masterShape: id)));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(3, masterGroup)
            .AddPage("P", Instance(1, master: 3, children: instanceMembers), pageSheet: layers)
            .Build();

        string text = VisioFixture.Extract(package).Text;

        Assert.StartsWith("Page: P\nMember 1\nMember 2\n", text, StringComparison.Ordinal);

        Assert.DoesNotContain("Member 200\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_LocallyDeletedOrGuideMasterShape_InheritsNothing()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "DELETED-MASTER", attributes: "Del='1'"))
            .AddMaster(3, VisioFixture.Shape(5, "GUIDE-MASTER", type: "Guide"))
            .AddPage("P", Instance(1, master: 2) + Instance(2, master: 3))
            .Build();

        Assert.Equal("Page: P\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- group display mode through the master

    [Fact]
    public void Extract_GroupInstanceInheritingDisplayModeZeroFromItsMaster_DoesNotEmitTheInheritedGroupText()
    {
        string masterGroup = VisioFixture.Shape(
            10,
            "HIDDEN-MASTER-GROUP-TEXT",
            type: "Group",
            cells: VisioFixture.Cell("DisplayMode", "0"),
            children: VisioFixture.Shape(11, "Member"));

        string instance = VisioFixture.Shape(
            1,
            null,
            type: "Group",
            attributes: "Master='3'",
            children: SubShape(2, masterShape: 11));

        byte[] package = new VisioPackageBuilder().AddMaster(3, masterGroup).AddPage("P", instance).Build();

        Assert.Equal("Page: P\nMember\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_GroupInstanceWithItsOwnTextAndAnInheritedDisplayModeZero_HidesItsOwnTextButKeepsItsSubShapes()
    {
        string masterGroup = VisioFixture.Shape(
            10,
            "Master group",
            type: "Group",
            cells: VisioFixture.Cell("DisplayMode", "0"),
            children: VisioFixture.Shape(11, "Member"));

        string instance = VisioFixture.Shape(
            1,
            "HIDDEN-OWN-GROUP-TEXT",
            type: "Group",
            attributes: "Master='3'",
            children: SubShape(2, masterShape: 11));

        byte[] package = new VisioPackageBuilder().AddMaster(3, masterGroup).AddPage("P", instance).Build();

        Assert.Equal("Page: P\nMember\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_GroupInstanceThatSetsDisplayModeShown_ShowsItsOwnTextButDoesNotRecoverAHiddenMastersText()
    {
        // Same conservative simplification as HideText: a hiding master's text is not retained.
        string masterGroup = VisioFixture.Shape(
            10,
            "HIDDEN-MASTER-LABEL",
            type: "Group",
            cells: VisioFixture.Cell("DisplayMode", "0"));

        string inheriting = VisioFixture.Shape(1, null, type: "Group", attributes: "Master='3'", cells: VisioFixture.Cell("DisplayMode", "1"));

        string own = VisioFixture.Shape(2, "Own", type: "Group", attributes: "Master='3'", cells: VisioFixture.Cell("DisplayMode", "2"));

        byte[] package = new VisioPackageBuilder().AddMaster(3, masterGroup).AddPage("P", inheriting + own).Build();

        Assert.Equal("Page: P\nOwn\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterGroupDisplayModeThatArrivesAfterTheMasterText_IsStillHonoured()
    {
        const string MasterGroup = "<Shape ID='10' Type='Group'><Text>HIDDEN-LATE-MASTER</Text><Cell N='DisplayMode' V='0'/></Shape>";

        byte[] package = new VisioPackageBuilder()
            .AddMaster(3, MasterGroup)
            .AddPage("P", VisioFixture.Shape(1, null, type: "Group", attributes: "Master='3'") + VisioFixture.Shape(2, "Kept"))
            .Build();

        Assert.Equal("Page: P\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterShapeThatIsNotAGroupWithDisplayModeZero_StillLendsItsText()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label", cells: VisioFixture.Cell("DisplayMode", "0")))
            .AddPage("P", Instance(1, master: 2))
            .Build();

        Assert.Equal("Page: P\nLabel\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- the masters graph

    [Fact]
    public void Extract_InstanceOfAMasterThePackageDoesNotDefine_InheritsNothing()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddPage("P", Instance(1, master: 42) + Instance(2, master: 2))
            .Build();

        Assert.Equal("Page: P\nLabel\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_PackageWithoutAMastersPart_InheritsNothing()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("P", Instance(1, master: 2) + VisioFixture.Shape(2, "Own"))
            .Build();

        Assert.Equal("Page: P\nOwn\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_UnusedMasterWhosePartIsMalformedOrAbsent_NeverFailsTheExtraction()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddMaster(3, VisioFixture.Shape(5, "Broken"))
            .AddMaster(4, VisioFixture.Shape(5, "Absent"))
            .AddPage("P", Instance(1, master: 2))
            .Replace("visio/masters/master2.xml", "<broken")
            .Remove("visio/masters/master3.xml")
            .Build();

        Assert.Equal("Page: P\nLabel\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MasterPartListedButAbsent_IsMissingPartWhenAnInstanceNeedsIt()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddPage("P", Instance(1, master: 2))
            .Remove("visio/masters/master1.xml")
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_MasterWithoutARelOrWithAnUnresolvableOne_IsMissingPartWhenAnInstanceNeedsIt()
    {
        string noRel = VisioFixture.Xml("Masters", "<Master ID='2' NameU='M'><PageSheet/></Master>");

        string badRel = VisioFixture.Xml("Masters", "<Master ID='2' NameU='M'><PageSheet/><Rel r:id='rId99'/></Master>");

        foreach (string masters in new[] { noRel, badRel })
        {
            byte[] package = new VisioPackageBuilder()
                .AddMaster(2, VisioFixture.Shape(5, "Label"))
                .AddPage("P", Instance(1, master: 2))
                .Replace("visio/masters/masters.xml", masters)
                .Build();

            Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
        }
    }

    [Fact]
    public void Extract_MasterRelationshipOfAnotherType_IsMissingPartWhenAnInstanceNeedsIt()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddPage("P", Instance(1, master: 2))
            .Replace("visio/masters/_rels/masters.xml.rels", VisioPackageBuilder.Rels(("rId1", VisioFixture.VisioRelationshipPrefix + "page", "master1.xml", false)))
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_ExternalMasterRelationship_IsNeverFollowed()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddPage("P", Instance(1, master: 2))
            .Replace("visio/masters/_rels/masters.xml.rels", VisioPackageBuilder.Rels(("rId1", VisioFixture.VisioRelationshipPrefix + "master", "http://evil.example/master1.xml", true)))
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Theory]
    [InlineData("Pages")]
    [InlineData("PageContents")]
    public void Extract_MastersPartWithAnotherRootElement_IsFormatMismatchEvenWithoutInstances(string root)
    {
        string wrong = VisioFixture.Xml(root, string.Empty);

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddPage("P", VisioFixture.Shape(1, "Own"))
            .Replace("visio/masters/masters.xml", wrong)
            .Build();

        Assert.Equal(DocumentPackageRejection.FormatMismatch, VisioFixture.Reject(package));
    }

    [Theory]
    [InlineData("PageContents")]
    [InlineData("Shapes")]
    public void Extract_MasterPartWithAnotherRootElement_IsFormatMismatchWhenAnInstanceNeedsIt(string root)
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddPage("P", Instance(1, master: 2))
            .Replace("visio/masters/master1.xml", VisioFixture.Xml(root, string.Empty))
            .Build();

        Assert.Equal(DocumentPackageRejection.FormatMismatch, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_MalformedMasterPart_IsMalformedXmlWhenAnInstanceNeedsIt()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddPage("P", Instance(1, master: 2))
            .Replace("visio/masters/master1.xml", VisioFixture.Xml("MasterContents", "<Shapes><Shape ID='5'><Text>x</Shape></Shapes>"))
            .Build();

        Assert.Equal(DocumentPackageRejection.MalformedXml, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_MasterPartWithADoctype_IsMalformedXml()
    {
        const string Master =
            "<?xml version='1.0'?><!DOCTYPE MasterContents [<!ENTITY marker 'EXPANDED-MASTER-ENTITY'>]>"
            + $"<MasterContents xmlns='{VisioFixture.Main}'><Shapes><Shape ID='5'><Text>&marker;</Text></Shape></Shapes></MasterContents>";

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddPage("P", Instance(1, master: 2))
            .Replace("visio/masters/master1.xml", Master)
            .Build();

        Assert.Equal(DocumentPackageRejection.MalformedXml, VisioFixture.Reject(package));
    }

    [Theory]
    [InlineData("master1.xml", "master1.xml")]
    [InlineData("master1.xml", "MASTER1.XML")]
    [InlineData("master1.xml", "/visio/masters/master1.xml")]
    [InlineData("master 1.xml", "master%201.xml")]
    public void Extract_TwoMastersNamingOnePart_IsFormatMismatchEvenWithoutInstances(string first, string second)
    {
        // Real producers give every master its own part; a shared one would be re-parsed per master ID.
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "One"))
            .AddMaster(3, VisioFixture.Shape(5, "Two"))
            .AddPage("P", VisioFixture.Shape(1, "Own"))
            .Replace("visio/masters/master 1.xml", VisioFixture.Xml("MasterContents", "<Shapes>" + VisioFixture.Shape(5, "One") + "</Shapes>"))
            .Replace(
                "visio/masters/_rels/masters.xml.rels",
                VisioPackageBuilder.Rels(
                    ("rId1", VisioFixture.VisioRelationshipPrefix + "master", first, false),
                    ("rId2", VisioFixture.VisioRelationshipPrefix + "master", second, false)))
            .Build();

        Assert.Equal(DocumentPackageRejection.FormatMismatch, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_ManyMastersNamingOnePart_IsFormatMismatchInsteadOfParsingThePartPerMaster()
    {
        // 600 master IDs that all point at one ~100 KB part, each instantiated once: parsing per master would inflate it 600 times.
        Random random = new(7);

        string padding = string.Concat(Enumerable.Range(0, 100_000 / 8).Select(_ => random.Next().ToString("x8")));

        string masterShape = VisioFixture.Shape(
            5,
            "Label",
            cells: VisioFixture.Cell("HideText", "1") + $"<Section N='User'><Row N='pad'><Cell N='Value' V='{padding}'/></Row></Section>");

        string masters = VisioFixture.Xml("Masters", string.Concat(Enumerable.Range(1, 600).Select(id => $"<Master ID='{id}' NameU='M{id}'><PageSheet/><Rel r:id='rId1'/></Master>")));

        string instances = string.Concat(Enumerable.Range(1, 600).Select(id => Instance(id, master: id)));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, masterShape)
            .AddPage("P", instances)
            .Replace("visio/masters/masters.xml", masters)
            .Build();

        Assert.Equal(DocumentPackageRejection.FormatMismatch, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_MastersWithoutAResolvableRelationship_AreNotMistakenForSharingAPart()
    {
        // Two Master elements that name no part share nothing; only an instance that needs one is refused, as MissingPart.
        string masters = VisioFixture.Xml(
            "Masters",
            "<Master ID='2' NameU='A'><PageSheet/></Master><Master ID='3' NameU='B'><PageSheet/><Rel r:id='rId99'/></Master>");

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddPage("P", VisioFixture.Shape(1, "Own"))
            .Replace("visio/masters/masters.xml", masters)
            .Build();

        Assert.Equal("Page: P\nOwn\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_ManyInstancesOfOneLargeMaster_ParseTheMasterPartOnlyOnce()
    {
        // The master part is ~100 KB and not very compressible; parsing it per instance would exhaust the 64 MiB
        // aggregate inflation budget long before 700 instances. The master's shape is hidden so nothing is emitted.
        Random random = new(7);

        string padding = string.Concat(Enumerable.Range(0, 100_000 / 8).Select(_ => random.Next().ToString("x8")));

        string masterShape = VisioFixture.Shape(
            5,
            "Label",
            cells: VisioFixture.Cell("HideText", "1") + $"<Section N='User'><Row N='pad'><Cell N='Value' V='{padding}'/></Row></Section>");

        string instances = string.Concat(Enumerable.Range(1, 700).Select(id => Instance(id, master: 2)));

        byte[] package = new VisioPackageBuilder().AddMaster(2, masterShape).AddPage("P", instances).Build();

        Assert.Equal("Page: P\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- master count and retention bounds

    [Fact]
    public void Extract_MoreMastersThanTheLimit_IsStructureBoundsEvenWithoutInstances()
    {
        byte[] package = ThreeMasters();

        Assert.Equal(DocumentPackageRejection.StructureBounds, VisioFixture.Reject(package, new VisioDrawingExtractor(4096, maximumMasters: 2)));
    }

    [Fact]
    public void Extract_MasterCountAtTheLimit_Succeeds()
    {
        Assert.Equal("Page: P\nOwn\n", VisioFixture.Extract(ThreeMasters(), new VisioDrawingExtractor(4096, maximumMasters: 3)).Text);
    }

    [Fact]
    public void Extract_ProductionMasterLimitsAre4096MastersAnd262144Characters()
    {
        Assert.Equal(4096, VisioDrawingExtractor.MaximumMasters);

        Assert.Equal(262_144, VisioDrawingExtractor.MaximumMasterTextCharacters);
    }

    [Fact]
    public void Extract_MasterTextAtTheRetentionLimit_IsInheritedInFull()
    {
        string label = new('m', 20);

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, label))
            .AddPage("P", Instance(1, master: 2))
            .Build();

        SessionAttachmentExtractionResult result = VisioFixture.Extract(package, new VisioDrawingExtractor(4096, 4096, maximumMasterCharacters: 20));

        Assert.Equal($"Page: P\n{label}\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void Extract_MasterTextBeyondTheRetentionLimit_StopsAndMarksTheResultTruncated()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, new string('m', 21)))
            .AddPage("P", VisioFixture.Shape(1, "Before") + Instance(2, master: 2) + VisioFixture.Shape(3, "NEVER-REACHED"))
            .Build();

        SessionAttachmentExtractionResult result = VisioFixture.Extract(package, new VisioDrawingExtractor(4096, 4096, maximumMasterCharacters: 20));

        Assert.Equal("Page: P\nBefore\n", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_ProductionRetentionLimitTruncatesA262145CharacterMasterText()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, new string('m', 262_145)))
            .AddPage("P", Instance(1, master: 2))
            .Build();

        SessionAttachmentExtractionResult result = VisioFixture.Extract(package);

        Assert.Equal("Page: P\n", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_OverflowingMasterTextThatTheInstanceNeverNeeds_DoesNotTruncate()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, new string('m', 21)))
            .AddPage("P", Instance(1, master: 2, text: "Own", cells: VisioFixture.Cell("HideText", "0")))
            .Build();

        SessionAttachmentExtractionResult result = VisioFixture.Extract(package, new VisioDrawingExtractor(4096, 4096, maximumMasterCharacters: 20));

        Assert.Equal("Page: P\nOwn\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void Extract_RetainedMasterTextIsSharedAcrossMasters()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, new string('a', 15)))
            .AddMaster(3, VisioFixture.Shape(5, new string('b', 15)))
            .AddPage("P", Instance(1, master: 2) + Instance(2, master: 3))
            .Build();

        SessionAttachmentExtractionResult result = VisioFixture.Extract(package, new VisioDrawingExtractor(4096, 4096, maximumMasterCharacters: 20));

        Assert.Equal($"Page: P\n{new string('a', 15)}\n", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_HiddenMasterTextIsNotRetained_SoItDoesNotUseUpTheSharedBudget()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, new string('a', 15), cells: VisioFixture.Cell("HideText", "1")))
            .AddMaster(3, VisioFixture.Shape(5, new string('b', 15)))
            .AddPage("P", Instance(1, master: 2) + Instance(2, master: 3))
            .Build();

        SessionAttachmentExtractionResult result = VisioFixture.Extract(package, new VisioDrawingExtractor(4096, 4096, maximumMasterCharacters: 20));

        Assert.Equal($"Page: P\n{new string('b', 15)}\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void Extract_MoreRetainedMasterShapesThanTheLimit_TruncatesWhenADroppedShapeIsNeededButNotForRetainedOnes()
    {
        // 8,200 hidden sub-shapes: only the first 8,192 are retained. Shape 1 is retained; shape 8200 is not.
        string children = string.Concat(Enumerable.Range(1, 8200).Select(id => VisioFixture.Shape(id, "x", cells: VisioFixture.Cell("HideText", "1"))));

        string master = VisioFixture.Shape(9000, type: "Group", children: children);

        byte[] retained = new VisioPackageBuilder()
            .AddMaster(2, master)
            .AddPage("P", Instance(1, master: 2, children: SubShape(2, masterShape: 1)) + VisioFixture.Shape(3, "After"))
            .Build();

        SessionAttachmentExtractionResult retainedResult = VisioFixture.Extract(retained);

        Assert.Equal("Page: P\nAfter\n", retainedResult.Text);

        Assert.False(retainedResult.WasTruncated);

        byte[] dropped = new VisioPackageBuilder()
            .AddMaster(2, master)
            .AddPage("P", Instance(1, master: 2, children: SubShape(2, masterShape: 8200)) + VisioFixture.Shape(3, "NEVER-REACHED"))
            .Build();

        SessionAttachmentExtractionResult droppedResult = VisioFixture.Extract(dropped);

        Assert.Equal("Page: P\n", droppedResult.Text);

        Assert.True(droppedResult.WasTruncated);
    }

    private static string Instance(int id, int master, string? text = null, string cells = "", string children = "") =>
        VisioFixture.Shape(id, text, attributes: $"Master='{master}'", cells: cells, children: children);

    private static string SubShape(int id, int masterShape, string? text = null, string cells = "", string children = "") =>
        VisioFixture.Shape(id, text, attributes: $"MasterShape='{masterShape}'", cells: cells, children: children);

    private static byte[] ThreeMasters() => new VisioPackageBuilder()
        .AddMaster(1, VisioFixture.Shape(5, "One"))
        .AddMaster(2, VisioFixture.Shape(5, "Two"))
        .AddMaster(3, VisioFixture.Shape(5, "Three"))
        .AddPage("P", VisioFixture.Shape(1, "Own"))
        .Build();
}
