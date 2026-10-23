using System.Diagnostics;
using System.Text;
using System.Xml;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class BoundedXmlReaderTests
{
    private const string Marker = "INJECTED";

    private const int FuzzIterations = 4000;

    // ---------------------------------------------------------------- ordinary documents

    [Fact]
    public void Read_PlainDocument_VisitsElementsAttributesAndText()
    {
        using BoundedXmlReader reader = Create("<a><b>hello</b><c x=\"1\"/></a>");

        List<string> events = [];

        while (reader.Read())
        {
            XmlReader xml = reader.Reader;

            if (xml.NodeType == XmlNodeType.Element)
            {
                events.Add(xml.GetAttribute("x") is { } x ? $"{xml.LocalName}[{x}]" : xml.LocalName);
            }
            else if (xml.NodeType == XmlNodeType.Text)
            {
                events.Add($"text:{xml.Value}");
            }
        }

        Assert.Equal(["a", "b", "text:hello", "c[1]"], events);
    }

    [Fact]
    public void Read_NamespacedElement_ExposesNamespaceUriAndLocalName()
    {
        using BoundedXmlReader reader = Create("<w:p xmlns:w=\"urn:test:w\"/>");

        Assert.True(reader.Read());

        Assert.Equal("p", reader.Reader.LocalName);

        Assert.Equal("urn:test:w", reader.Reader.NamespaceURI);

        Assert.True(reader.Reader.IsEmptyElement);
    }

    [Fact]
    public void Read_CommentsAndProcessingInstructions_NeverSurface()
    {
        // Sensitivity: IgnoreComments / IgnoreProcessingInstructions in the reader settings.
        using BoundedXmlReader reader = Create("<a><!-- secret comment --><?pi secret-instruction?>t</a>");

        List<XmlNodeType> types = [];

        while (reader.Read())
        {
            types.Add(reader.Reader.NodeType);
        }

        Assert.DoesNotContain(XmlNodeType.Comment, types);

        Assert.DoesNotContain(XmlNodeType.ProcessingInstruction, types);

        Assert.Contains(XmlNodeType.Text, types);
    }

    [Fact]
    public void Read_WhitespaceBetweenElements_IsReported()
    {
        // Sensitivity: IgnoreWhitespace = false. Word, Spreadsheet and ODF text depend on seeing whitespace nodes.
        using BoundedXmlReader reader = Create("<a> <b/> </a>");

        int whitespace = 0;

        while (reader.Read())
        {
            if (reader.Reader.NodeType is XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                whitespace++;
            }
        }

        Assert.Equal(2, whitespace);
    }

    [Fact]
    public void Read_PredefinedAndCharacterReferences_AreNotLimitedByTheEntityBudget()
    {
        // 2,000 &amp; references expand to 2,000 characters, over MaxCharactersFromEntities = 1024; predefined and
        // character references are not subject to that budget, so a real document full of them must still read.
        string body = string.Concat(Enumerable.Repeat("&amp;&#65;", 2000));

        using BoundedXmlReader reader = Create($"<a>{body}</a>");

        string text = Drain(reader);

        Assert.Equal(4000, text.Length);

        Assert.Equal(2000, text.Count(character => character == '&'));

        Assert.Equal(2000, text.Count(character => character == 'A'));
    }

    [Fact]
    public void Read_Utf16PartWithByteOrderMark_ReadsText()
    {
        byte[] bytes = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("<?xml version=\"1.0\" encoding=\"utf-16\"?><a>héllo</a>")];

        using BoundedXmlReader reader = BoundedXmlReader.Create(bytes, CancellationToken.None);

        Assert.Equal("héllo", Drain(reader));
    }

    [Fact]
    public void Read_Utf8PartWithByteOrderMark_ReadsText()
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("<a>héllo</a>")];

        using BoundedXmlReader reader = BoundedXmlReader.Create(bytes, CancellationToken.None);

        Assert.Equal("héllo", Drain(reader));
    }

    [Fact]
    public void Create_OverAnArrayOffsetSegment_ReadsOnlyTheSegment()
    {
        byte[] inner = Encoding.UTF8.GetBytes("<a>segment</a>");

        byte[] padded = [.. "junk-before"u8.ToArray(), .. inner, .. "junk-after"u8.ToArray()];

        using BoundedXmlReader reader = BoundedXmlReader.Create(new ReadOnlyMemory<byte>(padded, 11, inner.Length), CancellationToken.None);

        Assert.Equal("segment", Drain(reader));
    }

    [Fact]
    public void Dispose_ClosesTheUnderlyingReader()
    {
        BoundedXmlReader reader = Create("<a/>");

        reader.Dispose();

        Assert.Equal(ReadState.Closed, reader.Reader.ReadState);
    }

    // ---------------------------------------------------------------- DOCTYPE, entities

    [Theory]
    [InlineData("<!DOCTYPE r><r/>")]
    [InlineData("<!DOCTYPE r [<!ELEMENT r ANY>]><r/>")]
    [InlineData("<!DOCTYPE r SYSTEM \"file:///etc/passwd\"><r/>")]
    [InlineData("<!DOCTYPE manifest:manifest PUBLIC \"-//OpenOffice.org//DTD Manifest 1.0//EN\" \"Manifest.dtd\"><manifest:manifest xmlns:manifest=\"urn:test\"/>")]
    public void Read_AnyDocumentTypeDeclaration_IsMalformedXmlWhenProhibited(string xml)
    {
        // Sensitivity: DtdProcessing.Prohibit in the reader settings.
        using BoundedXmlReader reader = Create(xml);

        Rejected(DocumentPackageRejection.MalformedXml, () => Drain(reader));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_InternalEntityMarker_IsMalformedXmlAndTheMarkerNeverAppears(bool ignoreDocumentType)
    {
        // Sensitivity: flipping DtdProcessing.Prohibit (or Ignore) to Parse expands &x; into the marker text.
        const string xml = "<!DOCTYPE r [<!ENTITY x \"INJECTED\">]><r>&x;</r>";

        using BoundedXmlReader reader = Create(xml, ignoreDocumentType);

        StringBuilder seen = new();

        Rejected(DocumentPackageRejection.MalformedXml, () => seen.Append(Drain(reader)));

        Assert.DoesNotContain(Marker, seen.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_ExternalSystemEntity_IsMalformedXmlAndNothingIsResolved(bool ignoreDocumentType)
    {
        const string xml = "<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/hosts\">]><r>&x;</r>";

        using BoundedXmlReader reader = Create(xml, ignoreDocumentType);

        StringBuilder seen = new();

        Rejected(DocumentPackageRejection.MalformedXml, () => seen.Append(Drain(reader)));

        Assert.DoesNotContain("localhost", seen.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_BillionLaughs_IsMalformedXmlAndBounded(bool ignoreDocumentType)
    {
        StringBuilder xml = new("<!DOCTYPE lolz [<!ENTITY lol \"lol\">");

        for (int level = 1; level <= 9; level++)
        {
            string previous = level == 1 ? "lol" : $"lol{level - 1}";

            xml.Append($"<!ENTITY lol{level} \"{string.Concat(Enumerable.Repeat($"&{previous};", 10))}\">");
        }

        xml.Append("]><lolz>&lol9;</lolz>");

        using BoundedXmlReader reader = Create(xml.ToString(), ignoreDocumentType);

        Stopwatch stopwatch = Stopwatch.StartNew();

        Rejected(DocumentPackageRejection.MalformedXml, () => Drain(reader));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Billion laughs took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void Read_ManifestStyleDocumentTypeWithoutInternalSubset_ReadsWhenDocumentTypeIsIgnored()
    {
        const string xml =
            "<!DOCTYPE manifest:manifest PUBLIC \"-//OpenOffice.org//DTD Manifest 1.0//EN\" \"Manifest.dtd\">"
            + "<manifest:manifest xmlns:manifest=\"urn:test:manifest\"><manifest:file-entry manifest:full-path=\"/\"/></manifest:manifest>";

        using BoundedXmlReader reader = Create(xml, ignoreDocumentType: true);

        List<string> elements = [];

        while (reader.Read())
        {
            if (reader.Reader.NodeType == XmlNodeType.Element)
            {
                elements.Add(reader.Reader.LocalName);
            }
        }

        Assert.Equal(["manifest", "file-entry"], elements);
    }

    // ---------------------------------------------------------------- depth, malformed input

    [Fact]
    public void Read_ElementsAtExactlyTheMaximumDepth_AreAllowed()
    {
        using BoundedXmlReader reader = Create(Nested(BoundedXmlReader.MaximumDepth + 1));

        int deepest = 0;

        while (reader.Read())
        {
            deepest = Math.Max(deepest, reader.Reader.Depth);
        }

        Assert.Equal(BoundedXmlReader.MaximumDepth, deepest);
    }

    [Theory]
    [InlineData(BoundedXmlReader.MaximumDepth + 2)]
    [InlineData(300)]
    public void Read_ElementNestedPastTheMaximumDepth_IsStructureBounds(int depth)
    {
        // Sensitivity: the "Depth > MaximumDepth" guard in Read.
        using BoundedXmlReader reader = Create(Nested(depth));

        Rejected(DocumentPackageRejection.StructureBounds, () => Drain(reader));
    }

    [Theory]
    [InlineData("<a><b></a>")]
    [InlineData("<a>")]
    [InlineData("")]
    [InlineData("just text")]
    [InlineData("<a/><b/>")]
    [InlineData("<a:b/>")]
    [InlineData("<a x=\"1\" x=\"2\"/>")]
    [InlineData("<a>&undefined;</a>")]
    [InlineData("<a>&#1;</a>")]
    [InlineData("<a>ok &undefined; ok</a>")]
    [InlineData("<a>ok &#1; ok</a>")]
    [InlineData("<a>caf\u00E9 &#65; &undefined;</a>")]
    public void Read_MalformedXml_IsMalformedXml(string xml)
    {
        // Sensitivity: the forced Value access in Read. XmlReader completes text lazily, so in the last three rows the
        // error is raised by the first Value access, not by Read; it must still surface as a closed rejection.
        using BoundedXmlReader reader = Create(xml);

        Rejected(DocumentPackageRejection.MalformedXml, () => Drain(reader));
    }

    [Fact]
    public void Read_InvalidUtf8Bytes_IsMalformedXml()
    {
        byte[] bytes = [.. "<a>"u8.ToArray(), 0xC3, 0x28, .. "</a>"u8.ToArray()];

        using BoundedXmlReader reader = BoundedXmlReader.Create(bytes, CancellationToken.None);

        Rejected(DocumentPackageRejection.MalformedXml, () => Drain(reader));
    }

    [Fact]
    public void Read_UnsupportedEncodingDeclaration_IsMalformedXml()
    {
        using BoundedXmlReader reader = Create("<?xml version=\"1.0\" encoding=\"no-such-encoding\"?><a/>");

        Rejected(DocumentPackageRejection.MalformedXml, () => Drain(reader));
    }

    [Fact]
    public void Read_MalformedXml_DoesNotCarryTheXmlParserMessage()
    {
        using BoundedXmlReader reader = Create("<a>secretmarker</b>");

        DocumentPackageRejectedException exception = Rejected(DocumentPackageRejection.MalformedXml, () => Drain(reader));

        Assert.DoesNotContain("secretmarker", exception.Message);
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public void Read_CancelledToken_IsObservedWithinTheCancellationInterval()
    {
        // Sensitivity: the "nodes % CancellationInterval" check in Read.
        string xml = $"<a>{string.Concat(Enumerable.Repeat("<x/>", 5000))}</a>";

        using BoundedXmlReader reader = BoundedXmlReader.Create(Encoding.UTF8.GetBytes(xml), new CancellationToken(canceled: true));

        int nodes = 0;

        Assert.Throws<OperationCanceledException>(() =>
        {
            while (reader.Read())
            {
                nodes++;
            }
        });

        Assert.True(nodes <= BoundedXmlReader.CancellationInterval, $"Cancellation took {nodes} nodes to observe.");
    }

    // ---------------------------------------------------------------- SkipElement

    [Fact]
    public void SkipElement_NestedSubtree_ConsumesItAndStopsAtTheNextNode()
    {
        using BoundedXmlReader reader = Create("<a><skip><x><y/></x>text</skip><after>1</after></a>");

        AdvanceTo(reader, "skip");

        reader.SkipElement();

        Assert.Equal(XmlNodeType.Element, reader.Reader.NodeType);

        Assert.Equal("after", reader.Reader.LocalName);
    }

    [Fact]
    public void SkipElement_EmptyElement_ConsumesJustThatNode()
    {
        using BoundedXmlReader reader = Create("<a><e/><f/></a>");

        AdvanceTo(reader, "e");

        reader.SkipElement();

        Assert.Equal("f", reader.Reader.LocalName);
    }

    [Fact]
    public void SkipElement_LastChild_LeavesTheReaderOnTheParentEnd()
    {
        using BoundedXmlReader reader = Create("<a><e><x/></e></a>");

        AdvanceTo(reader, "e");

        reader.SkipElement();

        Assert.Equal(XmlNodeType.EndElement, reader.Reader.NodeType);

        Assert.Equal("a", reader.Reader.LocalName);
    }

    [Fact]
    public void SkipElement_SubtreeNestedPastTheMaximumDepth_IsStructureBounds()
    {
        // Sensitivity: SkipElement must go through Read so the depth guard still applies inside a skipped subtree.
        using BoundedXmlReader reader = Create($"<root><skip>{Nested(300)}</skip></root>");

        AdvanceTo(reader, "skip");

        Rejected(DocumentPackageRejection.StructureBounds, reader.SkipElement);
    }

    [Fact]
    public void SkipElement_MalformedSubtree_IsMalformedXml()
    {
        using BoundedXmlReader reader = Create("<a><s><b></s></a>");

        AdvanceTo(reader, "s");

        Rejected(DocumentPackageRejection.MalformedXml, reader.SkipElement);
    }

    [Fact]
    public void SkipElement_CancelledTokenOverALargeSubtree_IsObserved()
    {
        // Sensitivity: SkipElement must go through Read so the cancellation check still applies inside a skipped subtree.
        string xml = $"<a><skip>{string.Concat(Enumerable.Repeat("<x/>", 5000))}</skip></a>";

        using CancellationTokenSource source = new();

        using BoundedXmlReader reader = BoundedXmlReader.Create(Encoding.UTF8.GetBytes(xml), source.Token);

        AdvanceTo(reader, "skip");

        source.Cancel();

        Assert.Throws<OperationCanceledException>(reader.SkipElement);
    }

    [Fact]
    public void SkipElement_NotOnAnElement_IsAnInvalidOperation()
    {
        using BoundedXmlReader reader = Create("<a>text</a>");

        Assert.True(reader.Read());

        Assert.True(reader.Read());

        Assert.Equal(XmlNodeType.Text, reader.Reader.NodeType);

        Assert.Throws<InvalidOperationException>(reader.SkipElement);
    }

    // ---------------------------------------------------------------- robustness

    [Fact]
    public void Read_RandomlyCorruptedDocuments_OnlyEverSucceedOrThrowAClosedRejection()
    {
        // Seeded corruption of well-formed XML (including a declaration, a namespace and entity references): the only
        // permitted outcomes are a clean read or a DocumentPackageRejectedException, in either DOCTYPE mode.
        byte[] baseline = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"urn:test:w\" w:a=\"1\">"
            + "<w:p w:b=\"x&amp;y\">caf\u00E9 &#65; &lt;t&gt;<![CDATA[raw]]></w:p><!-- c --><?pi d?><w:e/></w:document>");

        Random random = new(20261022);

        for (int iteration = 0; iteration < FuzzIterations; iteration++)
        {
            byte[] mutated = (byte[])baseline.Clone();

            int flips = 1 + random.Next(3);

            for (int flip = 0; flip < flips; flip++)
            {
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            }

            ReadOnlyMemory<byte> input = random.Next(8) == 0 ? mutated.AsMemory(0, random.Next(mutated.Length)) : mutated;

            try
            {
                using BoundedXmlReader reader = BoundedXmlReader.Create(input, CancellationToken.None, ignoreDocumentType: random.Next(2) == 0);

                while (reader.Read())
                {
                    XmlReader xml = reader.Reader;

                    _ = xml.Value;

                    _ = xml.LocalName;

                    _ = xml.NamespaceURI;

                    _ = xml.Depth;

                    _ = xml.IsEmptyElement;

                    _ = xml.GetAttribute("a");

                    _ = xml.GetAttribute("b", "urn:test:w");

                    _ = xml.AttributeCount;
                }
            }
            catch (DocumentPackageRejectedException)
            {
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static BoundedXmlReader Create(string xml, bool ignoreDocumentType = false) =>
        BoundedXmlReader.Create(Encoding.UTF8.GetBytes(xml), CancellationToken.None, ignoreDocumentType);

    private static string Nested(int depth) =>
        string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth));

    private static string Drain(BoundedXmlReader reader)
    {
        StringBuilder text = new();

        while (reader.Read())
        {
            if (reader.Reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA)
            {
                text.Append(reader.Reader.Value);
            }
        }

        return text.ToString();
    }

    private static void AdvanceTo(BoundedXmlReader reader, string elementName)
    {
        while (reader.Read())
        {
            if (reader.Reader.NodeType == XmlNodeType.Element && reader.Reader.LocalName == elementName)
            {
                return;
            }
        }

        throw new InvalidOperationException("Element not found.");
    }

    private static DocumentPackageRejectedException Rejected(DocumentPackageRejection expected, Action act)
    {
        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(act);

        Assert.Equal(expected, exception.Rejection);

        Assert.Equal(DocumentPackageRejections.Reason(expected), exception.Message);

        Assert.Null(exception.InnerException);

        return exception;
    }
}
