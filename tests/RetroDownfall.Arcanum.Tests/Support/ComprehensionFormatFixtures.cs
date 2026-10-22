using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Tests.Weave.Comprehension;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.ExcelWorkbookFixtures;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.OpenDocumentFixtures;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// One Comprehension source format as a realistic file: its registry MIME type and label, a file name with the
/// right extension, the package bytes, and two distinctive strings. The visible sentinel is ordinary document text
/// that must reach model context. The hidden sentinel sits in content the posture excludes (see plan D8) and must
/// never surface anywhere.
/// </summary>
internal sealed record ComprehensionFormatFixture(
    string MimeType,
    string FileName,
    string Label,
    string VisibleSentinel,
    string HiddenSentinel,
    byte[] Package)
{
    public string Extension => Path.GetExtension(FileName);

    /// <summary>The model-facing framing every attach, refresh, reference and pin path shows for this format.</summary>
    public string DataFraming => "UNTRUSTED DATA; " + Label + "; layout not preserved";

    /// <summary>The framing the retrieval prompt shows for this format.</summary>
    public string RetrievalFraming => "source-format: " + Label + "; layout not preserved";

    /// <summary>Asserts <paramref name="text"/> carries the visible sentinel exactly and never the hidden one.</summary>
    public void AssertSurfaces(string text)
    {
        Assert.Contains(VisibleSentinel, text, StringComparison.Ordinal);

        Assert.DoesNotContain(HiddenSentinel, text, StringComparison.Ordinal);
    }

    public override string ToString() => MimeType;
}

/// <summary>
/// One package per registry format other than the closed container: a visible sentinel in the document's primary
/// view and a hidden sentinel in a place the extractor must not read (formatting-hidden text, hidden sheets, rows
/// and columns, hidden slides, speaker notes, tracked deletions, annotations, Visio <c>HideText</c>, comments and
/// macro parts). Rows are keyed by MIME type so a theory row names exactly the format that failed.
/// </summary>
internal static class ComprehensionFormatFixtures
{
    private static readonly Lazy<IReadOnlyList<ComprehensionFormatFixture>> Fixtures = new(Create);

    public static IReadOnlyList<ComprehensionFormatFixture> All => Fixtures.Value;

    /// <summary>One theory row per format, holding its MIME type; resolve the fixture with <see cref="For"/>.</summary>
    public static TheoryData<string> MimeTypes
    {
        get
        {
            TheoryData<string> data = [];

            foreach (ComprehensionFormatFixture fixture in All)
            {
                data.Add(fixture.MimeType);
            }

            return data;
        }
    }

    public static ComprehensionFormatFixture For(string mimeType) =>
        All.Single(fixture => string.Equals(fixture.MimeType, mimeType, StringComparison.Ordinal));

    private static IReadOnlyList<ComprehensionFormatFixture> Create() =>
    [
        Make(SessionAttachmentComprehensionPolicy.WordDocumentMimeType, "quarterly-report.docx", "docx", Word),
        Make(SessionAttachmentComprehensionPolicy.WordMacroEnabledDocumentMimeType, "quarterly-report-macros.docm", "docm", WordMacroEnabled),
        Make(SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType, "budget.xlsx", "xlsx", Excel),
        Make(SessionAttachmentComprehensionPolicy.ExcelMacroEnabledWorkbookMimeType, "budget-macros.xlsm", "xlsm", ExcelMacroEnabled),
        Make(SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType, "roadmap.pptx", "pptx", PowerPoint),
        Make(SessionAttachmentComprehensionPolicy.PowerPointMacroEnabledPresentationMimeType, "roadmap-macros.pptm", "pptm", PowerPointMacroEnabled),
        Make(SessionAttachmentComprehensionPolicy.OpenDocumentTextMimeType, "meeting-notes.odt", "odt", OpenDocumentText),
        Make(SessionAttachmentComprehensionPolicy.OpenDocumentSpreadsheetMimeType, "ledger.ods", "ods", OpenDocumentSpreadsheet),
        Make(SessionAttachmentComprehensionPolicy.OpenDocumentPresentationMimeType, "kickoff.odp", "odp", OpenDocumentPresentation),
        Make(SessionAttachmentComprehensionPolicy.VisioDrawingMimeType, "process-flow.vsdx", "vsdx", Visio),
        Make(SessionAttachmentComprehensionPolicy.VisioMacroEnabledDrawingMimeType, "process-flow-macros.vsdm", "vsdm", VisioMacroEnabled),
    ];

    private static ComprehensionFormatFixture Make(
        string mimeType,
        string fileName,
        string tag,
        Func<string, string, byte[]> build)
    {
        // A format missing from the registry still gets its fixture, with no label. Its own theory rows then fail on the
        // path under test, and no other format's row is disturbed. The coverage test names the missing format.
        string label = SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out SessionAttachmentComprehensionFormat? format)
            ? format.FormatName
            : "(not in the Comprehension registry)";

        string visible = $"Sentinel-{tag}-visible-" + Suffix(tag, "visible");

        string hidden = $"Sentinel-{tag}-hidden-" + Suffix(tag, "hidden");

        return new ComprehensionFormatFixture(mimeType, fileName, label, visible, hidden, build(visible, hidden));
    }

    /// <summary>A stable, per-format hex suffix so no two formats share a sentinel.</summary>
    private static string Suffix(string tag, string kind)
    {
        uint hash = 2166136261;

        foreach (char current in tag + "/" + kind)
        {
            hash = (hash ^ current) * 16777619;
        }

        return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- Word

    private static byte[] Word(string visible, string hidden) => WordPackage(visible, hidden, macroEnabled: false);

    private static byte[] WordMacroEnabled(string visible, string hidden) => WordPackage(visible, hidden, macroEnabled: true);

    private static byte[] WordPackage(string visible, string hidden, bool macroEnabled)
    {
        string body = WordprocessingFixtures.P("Quarterly summary")
            + WordprocessingFixtures.P("Reviewed text " + visible)
            + $"<w:p>{WordprocessingFixtures.R(hidden, "<w:vanish/>")}</w:p>";

        string comments = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:comments {WordprocessingFixtures.Namespaces()}>"
            + $"<w:comment w:id=\"0\" w:author=\"Reviewer\"><w:p><w:r><w:t>{hidden}</w:t></w:r></w:p></w:comment></w:comments>";

        List<string> relationships =
        [
            WordprocessingFixtures.Relationship("rIdComments", WordprocessingFixtures.CommentsType, "comments.xml"),
        ];

        List<(string Name, string Content)> extra = [("word/comments.xml", comments)];

        if (macroEnabled)
        {
            relationships.Add(WordprocessingFixtures.Relationship("rIdVba", WordprocessingFixtures.VbaProjectType, "vbaProject.bin"));

            extra.Add(("word/vbaProject.bin", "Sub Hidden()  ' " + hidden));
        }

        return WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(body),
            documentRelationships: [.. relationships],
            extraEntries: [.. extra]);
    }

    // ---------------------------------------------------------------- Excel

    private static byte[] Excel(string visible, string hidden) => ExcelPackage(visible, hidden, macroEnabled: false);

    private static byte[] ExcelMacroEnabled(string visible, string hidden) => ExcelPackage(visible, hidden, macroEnabled: true);

    private static byte[] ExcelPackage(string visible, string hidden, bool macroEnabled)
    {
        // Cell C1 sits in a hidden column, row 2 is hidden, and the whole second sheet is hidden.
        string summaryRows = Row(Shared("A1", 0), Number("B1", "42"), Text("C1", hidden))
            + $"<row hidden=\"1\">{Text("A2", hidden)}</row>";

        Sheet summary = new("Summary", summaryRows, Cols: "<cols><col min=\"3\" max=\"3\" hidden=\"1\"/></cols>");

        Sheet archive = new("Archive", Row(Text("A1", hidden)), State: "hidden");

        if (!macroEnabled)
        {
            return Xlsx([summary, archive], Si(visible));
        }

        return Xlsx(
            [summary, archive],
            Si(visible),
            extraEntries: [("xl/vbaProject.bin", "Sub Hidden()  ' " + hidden)],
            extraWorkbookRelationships: $"<Relationship Id=\"rIdVba\" Type=\"http://schemas.microsoft.com/office/2006/relationships/vbaProject\" Target=\"vbaProject.bin\"/>");
    }

    // ---------------------------------------------------------------- PowerPoint

    private static byte[] PowerPoint(string visible, string hidden) => PowerPointPackage(visible, hidden, macroEnabled: false);

    private static byte[] PowerPointMacroEnabled(string visible, string hidden) => PowerPointPackage(visible, hidden, macroEnabled: true);

    private static byte[] PowerPointPackage(string visible, string hidden, bool macroEnabled)
    {
        // Slide 2 is hidden from the slide show. Slide 1 owns a speaker-notes part that is never read.
        List<(string Name, string Content)> entries = PresentationDeck.Entries(
            PresentationDeck.Slide(
                PresentationDeck.Shape(2, PresentationDeck.Paragraph("Roadmap"))
                + PresentationDeck.Shape(3, PresentationDeck.Paragraph("Milestone " + visible))),
            PresentationDeck.Slide(PresentationDeck.Shape(2, PresentationDeck.Paragraph(hidden)), " show=\"0\""));

        entries.Add((
            "ppt/slides/_rels/slide1.xml.rels",
            PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.Type("notesSlide"), "../notesSlides/notesSlide1.xml"))));

        entries.Add((
            "ppt/notesSlides/notesSlide1.xml",
            $"{PresentationDeck.Declaration}<p:notes xmlns:a=\"{PresentationDeck.A}\" xmlns:r=\"{PresentationDeck.R}\" xmlns:p=\"{PresentationDeck.P}\"><p:cSld><p:spTree>"
            + "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>"
            + PresentationDeck.Shape(2, PresentationDeck.Paragraph(hidden))
            + "</p:spTree></p:cSld></p:notes>"));

        if (macroEnabled)
        {
            string rels = entries.Single(entry => entry.Name == "ppt/_rels/presentation.xml.rels").Content.Replace(
                "</Relationships>",
                PresentationDeck.Rel("rIdVba", "http://schemas.microsoft.com/office/2006/relationships/vbaProject", "vbaProject.bin") + "</Relationships>",
                StringComparison.Ordinal);

            PresentationDeck.Replace(entries, "ppt/_rels/presentation.xml.rels", rels);

            entries.Add(("ppt/vbaProject.bin", "Sub Hidden()  ' " + hidden));
        }

        return PresentationDeck.Build(entries);
    }

    // ---------------------------------------------------------------- OpenDocument

    private static byte[] OpenDocumentText(string visible, string hidden)
    {
        // The deletion lives only in tracked changes and the annotation is a reviewer's comment: neither is body text.
        string body = "<text:tracked-changes text:track-changes=\"true\">"
            + "<text:changed-region text:id=\"ct1\"><text:deletion><office:change-info><dc:creator>Reviewer</dc:creator><dc:date>2026-01-01T00:00:00</dc:date></office:change-info>"
            + $"<text:p>{hidden}</text:p></text:deletion></text:changed-region></text:tracked-changes>"
            + "<text:p>Meeting notes</text:p>"
            + $"<text:p>Decision {visible}<text:change text:change-id=\"ct1\"/> recorded</text:p>"
            + "<text:p>Follow-up<office:annotation office:name=\"__Annotation__1\"><dc:creator>Reviewer</dc:creator><dc:date>2026-01-01T00:00:00</dc:date>"
            + $"<text:p>{hidden}</text:p></office:annotation><office:annotation-end office:name=\"__Annotation__1\"/></text:p>";

        return Odt(body);
    }

    private static byte[] OpenDocumentSpreadsheet(string visible, string hidden)
    {
        // Column 2 and row 2 are collapsed, and the second table is not displayed.
        string visibleRow = "<table:table-row>"
            + $"<table:table-cell office:value-type=\"string\"><text:p>{visible}</text:p></table:table-cell>"
            + $"<table:table-cell office:value-type=\"string\"><text:p>{hidden}</text:p></table:table-cell>"
            + "</table:table-row>";

        string collapsedRow = "<table:table-row table:visibility=\"collapse\">"
            + $"<table:table-cell office:value-type=\"string\"><text:p>{hidden}</text:p></table:table-cell></table:table-row>";

        string ledger = "<table:table table:name=\"Ledger\">"
            + "<table:table-column/><table:table-column table:visibility=\"collapse\"/>"
            + visibleRow
            + collapsedRow
            + "</table:table>";

        string archive = "<table:table table:name=\"Archive\" table:style-name=\"ta2\">"
            + "<table:table-column/>"
            + $"<table:table-row><table:table-cell office:value-type=\"string\"><text:p>{hidden}</text:p></table:table-cell></table:table-row>"
            + "</table:table>";

        return Ods(
            ledger + archive,
            automaticStyles: Style("table", "ta2", properties: "<style:table-properties table:display=\"false\"/>"));
    }

    private static byte[] OpenDocumentPresentation(string visible, string hidden)
    {
        // Page 2 is hidden by its drawing-page style, and page 1 carries speaker notes.
        static string Frame(string text) =>
            $"<draw:frame presentation:class=\"outline\" draw:layer=\"layout\" svg:width=\"20cm\"><draw:text-box><text:p>{text}</text:p></draw:text-box></draw:frame>";

        string notes = "<presentation:notes><draw:page-thumbnail/><draw:frame presentation:class=\"notes\"><draw:text-box>"
            + $"<text:p>{hidden}</text:p></draw:text-box></draw:frame></presentation:notes>";

        string pages = $"<draw:page draw:name=\"page1\" draw:master-page-name=\"Default\">{Frame("Kickoff")}{Frame("Agenda " + visible)}{notes}</draw:page>"
            + $"<draw:page draw:name=\"page2\" draw:master-page-name=\"Default\" draw:style-name=\"dp1\">{Frame(hidden)}</draw:page>";

        return Odp(
            pages,
            automaticStyles: Style("drawing-page", "dp1", properties: "<style:drawing-page-properties presentation:visibility=\"hidden\"/>"));
    }

    // ---------------------------------------------------------------- Visio

    private static byte[] Visio(string visible, string hidden) => VisioPackage(visible, hidden, macroEnabled: false);

    private static byte[] VisioMacroEnabled(string visible, string hidden) => VisioPackage(visible, hidden, macroEnabled: true);

    private static byte[] VisioPackage(string visible, string hidden, bool macroEnabled)
    {
        // The second shape hides its text; the builder adds comment, window and core-property parts that carry markers.
        VisioPackageBuilder builder = new VisioPackageBuilder()
            .AddPage(
                "Process",
                VisioFixture.Shape(1, "Approval step " + visible)
                + VisioFixture.Shape(2, hidden, cells: VisioFixture.Cell("HideText", "1")));

        if (macroEnabled)
        {
            builder = builder
                .MacroEnabled()
                .AddRaw(VisioPackageBuilder.UninflatableEntry("visio/vbaProject.bin"));
        }

        return builder.Build();
    }
}
