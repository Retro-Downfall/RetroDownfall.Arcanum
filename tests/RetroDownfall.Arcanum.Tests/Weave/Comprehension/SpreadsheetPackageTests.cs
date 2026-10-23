using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.ExcelWorkbookFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class SpreadsheetPackageTests
{
    private const string WorkbookRels = "xl/_rels/workbook.xml.rels";

    // ------------------------------------------------------------ part resolution

    [Fact]
    public void Extract_PackageWithoutAPackageRootRelationshipsPart_IsMissingPart()
    {
        byte[] package = ComprehensionPackageBuilder.Build(("xl/workbook.xml", $"<workbook xmlns=\"{Main}\"><sheets/></workbook>"));

        ExtractRejected(DocumentPackageRejection.MissingPart, package);
    }

    [Fact]
    public void Extract_PackageRootWithoutAnOfficeDocumentRelationship_IsMissingPart()
    {
        // Sensitivity: reaching the workbook only through the officeDocument relationship, never by guessing xl/workbook.xml.
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("extended-properties")}\" Target=\"docProps/app.xml\"/>")),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\"><sheets/></workbook>"));

        ExtractRejected(DocumentPackageRejection.MissingPart, package);
    }

    [Fact]
    public void Extract_OfficeDocumentRelationshipToAnAbsentPart_IsMissingPart()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"xl/workbook.xml\"/>")));

        ExtractRejected(DocumentPackageRejection.MissingPart, package);
    }

    [Fact]
    public void Extract_OtherRootRelationshipsBeforeTheOfficeDocumentOne_AreSkipped()
    {
        // Sensitivity: choosing the main part by relationship type, not by position. The earlier root relationships target
        // parts that exist but are not a workbook (one is not even XML); reading either as the main part would fail.
        byte[] package = ComprehensionPackageBuilder.Build(
            (
                "_rels/.rels",
                Relationships(
                    "<Relationship Id=\"rIdCore\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>",
                    "<Relationship Id=\"rIdThumb\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail\" Target=\"docProps/thumbnail.jpeg\"/>",
                    $"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"xl/workbook.xml\"/>")),
            ("docProps/core.xml", "<<< malformed"),
            ("docProps/thumbnail.jpeg", "not an image"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Rel}\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rIdS1\"/></sheets></workbook>"),
            (WorkbookRels, Relationships($"<Relationship Id=\"rIdS1\" Type=\"{OfficeDocumentRelationship("worksheet")}\" Target=\"worksheets/sheet1.xml\"/>")),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Main}\"><sheetData>{Row(Text("A1", "found"))}</sheetData></worksheet>"));

        Assert.Equal("Sheet: S\nfound\n", ExtractText(package));
    }

    [Fact]
    public void Extract_ExternalOfficeDocumentRelationship_IsNeverFollowed()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"xl/workbook.xml\" TargetMode=\"External\"/>")),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\"><sheets/></workbook>"));

        ExtractRejected(DocumentPackageRejection.MissingPart, package);
    }

    [Fact]
    public void Extract_VisibleSheetWhoseRelationshipIsAbsent_IsMissingPart()
    {
        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "x")), RelationshipId: "rIdNoSuchRelationship"));

        ExtractRejected(DocumentPackageRejection.MissingPart, package);
    }

    [Fact]
    public void Extract_VisibleSheetWithoutARelationshipId_IsMissingPart()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"xl/workbook.xml\"/>")),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\"><sheets><sheet name=\"S\" sheetId=\"1\"/></sheets></workbook>"));

        ExtractRejected(DocumentPackageRejection.MissingPart, package);
    }

    [Fact]
    public void Extract_VisibleSheetWhoseWorksheetPartIsAbsent_IsMissingPart()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"xl/workbook.xml\"/>")),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Rel}\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            (WorkbookRels, Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("worksheet")}\" Target=\"worksheets/missing.xml\"/>")));

        ExtractRejected(DocumentPackageRejection.MissingPart, package);
    }

    [Fact]
    public void Extract_SharedStringsRelationshipToAnAbsentPart_IsMissingPart()
    {
        byte[] package = Xlsx(
            [new Sheet("S", Row(Text("A1", "x")))],
            sharedStringItems: null,
            extraWorkbookRelationships: $"<Relationship Id=\"rIdSS\" Type=\"{OfficeDocumentRelationship("sharedStrings")}\" Target=\"gone.xml\"/>");

        ExtractRejected(DocumentPackageRejection.MissingPart, package);
    }

    [Fact]
    public void Extract_WorksheetRelationshipThatIsExternal_IsNeverFollowed()
    {
        // The external relationship is omitted by the relationship reader, so the sheet's r:id resolves to nothing.
        byte[] package = Xlsx(
            [new Sheet("S", Row(Text("A1", "x")), RelationshipId: "rIdExternal")],
            sharedStringItems: null,
            extraWorkbookRelationships: $"<Relationship Id=\"rIdExternal\" Type=\"{OfficeDocumentRelationship("worksheet")}\" Target=\"http://evil.example/sheet.xml\" TargetMode=\"External\"/>");

        ExtractRejected(DocumentPackageRejection.MissingPart, package);
    }

    [Fact]
    public void Extract_SharedStringsRelationshipThatIsExternal_IsNeverFollowedEvenWhenAMatchingPartExists()
    {
        // Sensitivity: honouring TargetMode="External". A local part with the same name as the external target must stay unread.
        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 0), Text("B1", "inline")))],
            sharedStringItems: null,
            extraEntries: [("xl/sharedStrings.xml", $"<sst xmlns=\"{Main}\"><si><t>EXTERNALTEXT</t></si></sst>")],
            extraWorkbookRelationships: $"<Relationship Id=\"rIdSS\" Type=\"{OfficeDocumentRelationship("sharedStrings")}\" Target=\"sharedStrings.xml\" TargetMode=\"External\"/>");

        string text = ExtractText(package);

        Assert.Equal("Sheet: S\ninline\n", text);

        Assert.DoesNotContain("EXTERNALTEXT", text);
    }

    [Fact]
    public void Extract_TwoSharedStringsRelationships_UseTheFirstInDocumentOrder()
    {
        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 0)))],
            Si("first part"),
            extraEntries: [("xl/second.xml", $"<sst xmlns=\"{Main}\"><si><t>SECONDPART</t></si></sst>")],
            extraWorkbookRelationships: $"<Relationship Id=\"rIdSS2\" Type=\"{OfficeDocumentRelationship("sharedStrings")}\" Target=\"second.xml\"/>");

        Assert.Equal("Sheet: S\nfirst part\n", ExtractText(package));
    }

    [Fact]
    public void Extract_RelationshipTargetsArePercentUnescapedOnceAndMatchedCaseInsensitively()
    {
        byte[] package = Xlsx(
            new Sheet("Escaped", Row(Text("A1", "escaped")), PartName: "xl/my sheets/Sheet 1.xml", Target: "/xl/my%20sheets/Sheet%201.xml"),
            new Sheet("Upper", Row(Text("A1", "upper")), PartName: "xl/worksheets/lower.xml", Target: "/XL/WORKSHEETS/LOWER.XML"));

        Assert.Equal("Sheet: Escaped\nescaped\nSheet: Upper\nupper\n", ExtractText(package));
    }

    [Fact]
    public void Extract_StrictNamespacesAndRelationshipTypes_AreAccepted()
    {
        byte[] package = Xlsx(
            [
                new Sheet("Visible", Row(Text("A1", "strict visible"))),
                new Sheet("Hidden", Row(Text("A1", "HIDDENSHEETTEXT")), State: "hidden"),
                new Sheet("Chart", string.Empty, Kind: "chartsheet", Part: $"<chartsheet xmlns=\"{StrictMain}\"/>"),
            ],
            sharedStringItems: null,
            strict: true);

        Assert.Equal("Sheet: Visible\nstrict visible\n", ExtractText(package));
    }

    [Fact]
    public void Extract_WorksheetRelationshipTypeFromAnotherNamespace_IsNotAWorksheet()
    {
        // Only the OOXML officeDocument "worksheet" type is read; a look-alike type from elsewhere is skipped.
        byte[] package = Xlsx(
            new Sheet("Lookalike", Row(Text("A1", "SKIPPED")), Type: "http://example.com/relationships/worksheet"),
            new Sheet("Real", Row(Text("A1", "read"))));

        Assert.Equal("Sheet: Real\nread\n", ExtractText(package));
    }

    // ------------------------------------------------------------ format mismatch

    [Fact]
    public void Extract_WordprocessingPackage_IsFormatMismatch()
    {
        const string wordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"word/document.xml\"/>")),
            ("word/document.xml", $"<w:document xmlns:w=\"{wordNamespace}\"><w:body><w:p><w:r><w:t>WORDTEXT</w:t></w:r></w:p></w:body></w:document>"));

        DocumentPackageRejectedException exception = ExtractRejected(DocumentPackageRejection.FormatMismatch, package);

        Assert.DoesNotContain("WORDTEXT", exception.Message);
    }

    [Fact]
    public void Extract_WorkbookRootInAnotherNamespace_IsFormatMismatch()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"xl/workbook.xml\"/>")),
            ("xl/workbook.xml", "<workbook xmlns=\"urn:not-spreadsheetml\"><sheets/></workbook>"));

        ExtractRejected(DocumentPackageRejection.FormatMismatch, package);
    }

    [Fact]
    public void Extract_WorkbookRootWithAnotherName_IsFormatMismatch()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"xl/workbook.xml\"/>")),
            ("xl/workbook.xml", $"<worksheet xmlns=\"{Main}\"/>"));

        ExtractRejected(DocumentPackageRejection.FormatMismatch, package);
    }

    [Fact]
    public void Extract_WorksheetRelationshipToANonWorksheetRoot_IsFormatMismatch()
    {
        byte[] package = Xlsx(new Sheet("S", string.Empty, Part: $"<workbook xmlns=\"{Main}\"/>"));

        ExtractRejected(DocumentPackageRejection.FormatMismatch, package);
    }

    [Fact]
    public void Extract_SharedStringsPartWithoutAnSstRoot_IsFormatMismatch()
    {
        byte[] package = Xlsx(
            [new Sheet("S", Row(Text("A1", "x")))],
            sharedStringItems: null,
            extraEntries: [("xl/sharedStrings.xml", $"<notSst xmlns=\"{Main}\"/>")],
            extraWorkbookRelationships: $"<Relationship Id=\"rIdSS\" Type=\"{OfficeDocumentRelationship("sharedStrings")}\" Target=\"sharedStrings.xml\"/>");

        ExtractRejected(DocumentPackageRejection.FormatMismatch, package);
    }

    // ------------------------------------------------------------ malformed XML and DTDs

    [Fact]
    public void Extract_MalformedWorkbookXml_IsMalformedXml()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"xl/workbook.xml\"/>")),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\"><sheets></workbook>"));

        ExtractRejected(DocumentPackageRejection.MalformedXml, package);
    }

    [Fact]
    public void Extract_MalformedWorksheetXml_IsMalformedXmlAndProjectsNothingOfThatSheet()
    {
        // The worksheet is read to its end before any of its cells are projected; the unclosed element fails the part.
        byte[] package = Xlsx(new Sheet(
            "S",
            string.Empty,
            Part: $"<worksheet xmlns=\"{Main}\"><sheetData><row><c r=\"A1\" t=\"inlineStr\"><is><t>text</t></is></c></row><row></sheetData></worksheet>"));

        ExtractRejected(DocumentPackageRejection.MalformedXml, package);
    }

    [Fact]
    public void Extract_TruncatedWorksheetXml_IsMalformedXml()
    {
        byte[] package = Xlsx(new Sheet("S", string.Empty, Part: $"<worksheet xmlns=\"{Main}\"><sheetData><row><c r=\"A1\" t=\"inlineStr\"><is><t>te"));

        ExtractRejected(DocumentPackageRejection.MalformedXml, package);
    }

    [Fact]
    public void Extract_NonXmlWorksheetBytes_IsMalformedXml()
    {
        byte[] package = Xlsx(new Sheet("S", string.Empty, Part: "this is not xml at all"));

        ExtractRejected(DocumentPackageRejection.MalformedXml, package);
    }

    [Theory]
    [InlineData("worksheet")]
    [InlineData("workbook")]
    [InlineData("sharedStrings")]
    public void Extract_DoctypeInAnyReadPart_IsMalformedXml(string part)
    {
        // Sensitivity: DtdProcessing.Prohibit. A doctype with an internal entity is refused before any text is read.
        const string doctype = "<!DOCTYPE x [<!ENTITY secret \"ENTITYEXPANSIONMARKER\">]>";

        string workbookXml = (part == "workbook" ? doctype : string.Empty) + $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Rel}\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rIdS1\"/></sheets></workbook>";

        string worksheetXml = (part == "worksheet" ? doctype : string.Empty) + $"<worksheet xmlns=\"{Main}\"><sheetData><row><c r=\"A1\" t=\"inlineStr\"><is><t>&secret;</t></is></c></row></sheetData></worksheet>";

        string sharedXml = (part == "sharedStrings" ? doctype : string.Empty) + $"<sst xmlns=\"{Main}\"><si><t>&secret;</t></si></sst>";

        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"xl/workbook.xml\"/>")),
            ("xl/workbook.xml", workbookXml),
            (WorkbookRels, Relationships(
                $"<Relationship Id=\"rIdS1\" Type=\"{OfficeDocumentRelationship("worksheet")}\" Target=\"worksheets/sheet1.xml\"/>",
                $"<Relationship Id=\"rIdSS\" Type=\"{OfficeDocumentRelationship("sharedStrings")}\" Target=\"sharedStrings.xml\"/>")),
            ("xl/worksheets/sheet1.xml", worksheetXml),
            ("xl/sharedStrings.xml", sharedXml));

        DocumentPackageRejectedException exception = ExtractRejected(DocumentPackageRejection.MalformedXml, package);

        Assert.DoesNotContain("ENTITYEXPANSIONMARKER", exception.Message);
    }

    [Fact]
    public void Extract_BillionLaughsWorksheet_IsRefusedWithoutExpanding()
    {
        const string bomb = "<!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\"><!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">]>";

        byte[] package = Xlsx(new Sheet("S", string.Empty, Part: bomb + $"<worksheet xmlns=\"{Main}\"><sheetData><row><c t=\"inlineStr\"><is><t>&lol3;</t></is></c></row></sheetData></worksheet>"));

        ExtractRejected(DocumentPackageRejection.MalformedXml, package);
    }

    // ------------------------------------------------------------ container errors reach the extractor unchanged

    [Fact]
    public void Extract_BytesThatAreNotAZipPackage_AreNotZip()
    {
        ExtractRejected(DocumentPackageRejection.NotZip, Encoding.UTF8.GetBytes("PK this is not really a zip package at all, only text"));
    }

    [Fact]
    public void Extract_CompoundFileSignature_IsEncrypted()
    {
        // Password-protected OOXML is an OLE2 EncryptedPackage, never a ZIP.
        byte[] package = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        ExtractRejected(DocumentPackageRejection.Encrypted, package);
    }

    [Fact]
    public void Extract_EmptyInput_IsNotZip()
    {
        ExtractRejected(DocumentPackageRejection.NotZip, []);
    }

    [Fact]
    public void Extract_WorksheetDeclaredOverTheEntryBound_IsExpansionBoundsThroughTheBoundedReader()
    {
        // Parts are read only through the bounded package reader, so its inflation bounds apply to the worksheet.
        RawZipEntry oversized = RawZipEntry.Deflated("xl/worksheets/big.xml", "<worksheet/>");

        oversized.UncompressedSize = (uint)(BoundedZipPackage.MaximumEntryBytes + 1);

        byte[] package = Xlsx(
            [new Sheet("S", Row(Text("A1", "x")), Target: "/xl/worksheets/big.xml")],
            sharedStringItems: null,
            rawEntries: [oversized]);

        ExtractRejected(DocumentPackageRejection.ExpansionBounds, package);
    }

    // ------------------------------------------------------------ parts that are never parsed

    [Fact]
    public void Extract_UnreferencedPackageParts_AreNeverParsed()
    {
        // Sensitivity: reaching parts only through relationships. These parts are malformed or carry marker text; none is read.
        byte[] package = Xlsx(
            [new Sheet("S", Row(Text("A1", "referenced")))],
            Si("referenced shared"),
            extraEntries:
            [
                ("xl/worksheets/smuggled.xml", $"<worksheet xmlns=\"{Main}\"><sheetData><row><c t=\"inlineStr\"><is><t>SMUGGLEDSHEET</t></is></c></row></sheetData></worksheet>"),
                ("xl/worksheets/smuggled-bad.xml", "<<< malformed SMUGGLEDBAD"),
                ("xl/sharedStrings2.xml", "<<< malformed SMUGGLEDSTRINGS"),
                ("docProps/core.xml", "<<< malformed SMUGGLEDCORE"),
                ("docProps/app.xml", "<Properties><Company>SMUGGLEDAPP</Company></Properties>"),
                ("xl/calcChain.xml", "<<< malformed SMUGGLEDCALC"),
                ("customXml/item1.xml", "<<< malformed SMUGGLEDCUSTOM"),
                ("xl/media/image1.png", "SMUGGLEDIMAGE"),
            ]);

        string text = ExtractText(package);

        Assert.Equal("Sheet: S\nreferenced\n", text);

        Assert.DoesNotContain("SMUGGLED", text);
    }

    [Fact]
    public void Extract_RelationshipsToPartsOtherThanSheetsAndSharedStrings_AreNeverOpened()
    {
        // calcChain, styles, theme, externalLinks, connections and customXml are related from the workbook but not read.
        byte[] package = Xlsx(
            [new Sheet("S", Row(Text("A1", "kept")))],
            sharedStringItems: null,
            extraEntries:
            [
                ("xl/calcChain.xml", "<<< malformed"),
                ("xl/styles.xml", "<<< malformed"),
                ("xl/theme/theme1.xml", "<<< malformed"),
                ("xl/externalLinks/externalLink1.xml", "<<< malformed"),
                ("xl/connections.xml", "<<< malformed"),
                ("customXml/item1.xml", "<<< malformed"),
            ],
            extraWorkbookRelationships: string.Concat(
                $"<Relationship Id=\"rIdC\" Type=\"{OfficeDocumentRelationship("calcChain")}\" Target=\"calcChain.xml\"/>",
                $"<Relationship Id=\"rIdSt\" Type=\"{OfficeDocumentRelationship("styles")}\" Target=\"styles.xml\"/>",
                $"<Relationship Id=\"rIdT\" Type=\"{OfficeDocumentRelationship("theme")}\" Target=\"theme/theme1.xml\"/>",
                $"<Relationship Id=\"rIdE\" Type=\"{OfficeDocumentRelationship("externalLink")}\" Target=\"externalLinks/externalLink1.xml\"/>",
                $"<Relationship Id=\"rIdCn\" Type=\"{OfficeDocumentRelationship("connections")}\" Target=\"connections.xml\"/>",
                $"<Relationship Id=\"rIdX\" Type=\"{OfficeDocumentRelationship("customXml")}\" Target=\"../customXml/item1.xml\"/>"));

        Assert.Equal("Sheet: S\nkept\n", ExtractText(package));
    }

    // ------------------------------------------------------------ macro-enabled workbooks

    [Fact]
    public void Extract_MacroEnabledWorkbook_NeverInflatesTheVbaProject()
    {
        // Sensitivity: opening only the parts the text needs. vbaProject.bin is related from the workbook, declares a
        // bomb-sized length and holds undecodable deflate data: inflating it would throw, reading it would be refused.
        byte[] garbage = [.. Enumerable.Repeat((byte)0xFF, 64)];

        RawZipEntry vba = RawZipEntry.Deflated("xl/vbaProject.bin", garbage);

        vba.DataAreaBytes = garbage;

        vba.UncompressedSize = int.MaxValue;

        vba.CompressedSize = (uint)garbage.Length;

        RawZipEntry customUi = RawZipEntry.Deflated("customUI/customUI.xml", garbage);

        customUi.DataAreaBytes = garbage;

        customUi.UncompressedSize = int.MaxValue;

        customUi.CompressedSize = (uint)garbage.Length;

        RawZipEntry embedding = RawZipEntry.Deflated("xl/embeddings/oleObject1.bin", garbage);

        embedding.DataAreaBytes = garbage;

        embedding.UncompressedSize = int.MaxValue;

        embedding.CompressedSize = (uint)garbage.Length;

        byte[] package = Xlsx(
            [new Sheet("S", Row(Text("A1", "macro workbook text")), AfterSheetData: "<oleObjects><oleObject r:id=\"rIdOle\"/></oleObjects>")],
            Si("unused"),
            extraEntries:
            [
                ("xl/worksheets/_rels/sheet1.xml.rels", Relationships($"<Relationship Id=\"rIdOle\" Type=\"{OfficeDocumentRelationship("oleObject")}\" Target=\"../embeddings/oleObject1.bin\"/>")),
            ],
            extraWorkbookRelationships: "<Relationship Id=\"rIdVba\" Type=\"http://schemas.microsoft.com/office/2006/relationships/vbaProject\" Target=\"vbaProject.bin\"/>"
                + "<Relationship Id=\"rIdUi\" Type=\"http://schemas.microsoft.com/office/2006/relationships/ui/extensibility\" Target=\"../customUI/customUI.xml\"/>",
            rawEntries: [vba, customUi, embedding]);

        // The trap is real: the bounded reader refuses these entries the moment anything tries to read them.
        BoundedZipPackage opened = BoundedZipPackage.Open(package, CancellationToken.None);

        Assert.Equal(DocumentPackageRejection.ExpansionBounds, Assert.Throws<DocumentPackageRejectedException>(() => opened.ReadEntry("xl/vbaProject.bin", CancellationToken.None)).Rejection);

        Assert.Equal("Sheet: S\nmacro workbook text\n", ExtractText(package));
    }
}
