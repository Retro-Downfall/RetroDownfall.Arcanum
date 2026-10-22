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
    public void Extract_MasterWithSeveralTopLevelShapes_UsesTheFirst()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "FIRST") + VisioFixture.Shape(6, "SECOND"))
            .AddPage("P", Instance(1, master: 2))
            .Build();

        Assert.Equal("Page: P\nFIRST\n", VisioFixture.Extract(package).Text);
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
    public void Extract_LayerMembershipOfTheMasterShapeIsNotAppliedToTheInstance()
    {
        // Documented simplification: layers are the page's; the master's own LayerMember names the master's layers.
        string layers = VisioFixture.LayerSection(VisioFixture.LayerRow(0, "Hidden", "0"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Master label", cells: VisioFixture.Cell("LayerMember", "0")))
            .AddPage("P", Instance(1, master: 2), pageSheet: layers)
            .Build();

        Assert.Equal("Page: P\nMaster label\n", VisioFixture.Extract(package).Text);
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
