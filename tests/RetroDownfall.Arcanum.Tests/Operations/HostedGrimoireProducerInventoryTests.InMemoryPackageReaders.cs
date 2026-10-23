using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // The Comprehension package readers inflate a ZIP entry with a DeflateStream over a MemoryStream of the snapshot bytes and parse
    // each part with an XmlReader over a read-only MemoryStream. Either reads whatever stream it was handed, so neither is reviewed by
    // name: the members that read or release the input are accepted only where the input is proved to be memory.
    private const string InMemoryInflaterBody =
        "byte[] packed = new byte[4]; byte[] buffer = new byte[8]; "
        + "using System.IO.MemoryStream source = new(packed, 0, packed.Length, writable: false); "
        + "using System.IO.Compression.DeflateStream inflater = new(source, System.IO.Compression.CompressionMode.Decompress); "
        + "_ = inflater.Read(buffer, 0, buffer.Length);";

    [Fact]
    public void An_inflater_over_an_in_memory_stream_is_no_producer()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource(InMemoryInflaterBody));

        Assert.DoesNotContain(result.Items, static site => site.Callee.StartsWith("System.IO.Compression.DeflateStream", StringComparison.Ordinal)
            || site.Callee == "System.IO.Stream.Dispose");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.StartsWith("System.IO.Compression.DeflateStream", StringComparison.Ordinal));
    }

    [Fact]
    public void An_inflater_over_a_file_stream_stays_refused()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource(InMemoryInflaterBody.Replace(
            "using System.IO.MemoryStream source = new(packed, 0, packed.Length, writable: false);",
            "using System.IO.Stream source = System.IO.File.OpenRead(\"packed\");",
            StringComparison.Ordinal)));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.IO.Compression.DeflateStream..ctor");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.IO.Compression.DeflateStream.Read");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.Stream.Dispose"
            && site.Kind == HostedProducerSiteKind.FileSystemEffect);
    }

    // The production shape of BoundedXmlReader: sealed, a private constructor that keeps the reader it is handed as the get-only
    // Reader, and one construction that hands it XmlReader.Create over a MemoryStream with settings that resolve nothing.
    private const string BoundedXmlReaderFixture = """
        namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension
        {
            internal sealed class BoundedXmlReader : System.IDisposable
            {
                private BoundedXmlReader(System.Xml.XmlReader reader)
                {
                    Reader = reader;
                }

                public System.Xml.XmlReader Reader { get; }

                public static BoundedXmlReader Create(System.ReadOnlyMemory<byte> part, bool ignoreDocumentType = false)
                {
                    System.Xml.XmlReaderSettings settings = new()
                    {
                        DtdProcessing = ignoreDocumentType ? System.Xml.DtdProcessing.Ignore : System.Xml.DtdProcessing.Prohibit,
                        XmlResolver = null,
                        IgnoreComments = true,
                        CloseInput = true,
                    };

                    System.IO.MemoryStream stream = OpenStream(part);

                    return new BoundedXmlReader(System.Xml.XmlReader.Create(stream, settings));
                }

                public bool Read()
                {
                    bool more = Reader.Read();

                    if (more && Reader.HasValue)
                    {
                        _ = Reader.Value;
                    }

                    return more;
                }

                public void Dispose() => Reader.Dispose();

                private static System.IO.MemoryStream OpenStream(System.ReadOnlyMemory<byte> part)
                {
                    byte[] copy = part.ToArray();

                    return new System.IO.MemoryStream(copy, 0, copy.Length, writable: false);
                }
            }

            internal sealed class FieldWalker(BoundedXmlReader xml)
            {
                private readonly System.Xml.XmlReader _reader = xml.Reader;

                public string Text() => _reader.Value;
            }

            internal sealed class ConstructorWalker
            {
                private readonly System.Xml.XmlReader _reader;

                public ConstructorWalker(BoundedXmlReader xml)
                {
                    _reader = xml.Reader;
                }

                public string Text() => _reader.Value;
            }

            internal static class PartReader
            {
                public static string Run(byte[] part)
                {
                    using BoundedXmlReader xml = BoundedXmlReader.Create(part);

                    System.Xml.XmlReader reader = xml.Reader;

                    string text = string.Empty;

                    while (xml.Read())
                    {
                        if (reader.NodeType == System.Xml.XmlNodeType.Text)
                        {
                            text = reader.Value + new FieldWalker(xml).Text() + new ConstructorWalker(xml).Text();
                        }
                    }

                    return text;
                }
            }
        }
        """;

    // A recursive-descent reader is one group of members that all call each other. The transitive scan asks whether any member reachable
    // along a path that repeats no member has a site; a member it has finished without one is not scanned again from another path, so a
    // group of twelve is scanned twelve times, not once per path through it (some 10^8). A site in the group is still found.
    private static string RecursiveGroupFixture(bool site)
    {
        const int members = 12;

        string Body(int index) => (site && index == members - 1 ? "System.IO.File.Exists(\"group\"); " : string.Empty)
            + string.Concat(Enumerable.Range(0, members).Where(other => other != index).Select(other => $"if (depth > {other}) M{other}(depth - 1); "));

        return "using System; public static class RecursiveGroup { public static readonly int Value = Start(); "
            + "private static int Start() { M0(3); return 1; } "
            + string.Concat(Enumerable.Range(0, members).Select(index => $"private static void M{index}(int depth) {{ {Body(index)}}} "))
            + "}";
    }

    [Fact]
    public void A_recursive_group_without_a_site_is_scanned_once_per_member()
    {
        IReadOnlyList<string> hazards = HostedGrimoireProducerInventory.ProbeStaticInitializationHazards([Compile(RecursiveGroupFixture(site: false))]);

        Assert.Empty(hazards);
    }

    [Fact]
    public void A_site_in_a_recursive_group_is_still_found()
    {
        IReadOnlyList<string> hazards = HostedGrimoireProducerInventory.ProbeStaticInitializationHazards([Compile(RecursiveGroupFixture(site: true))]);

        Assert.Equal(["RecursiveGroup"], hazards);
    }

    // A detached callback must own the admission it runs under only when it can reach a producer site. A recursive-descent parser
    // re-enters a member it is already in with the very same traversal state; that re-entry adds nothing the state's own traversal
    // does not report, so a detached parser that reaches no site needs no ownership. A site anywhere in the recursion still does.
    [Theory]
    [InlineData("", true)]
    [InlineData("System.IO.File.Delete(\"path\"); ", false)]
    public void A_detached_recursive_parser_needs_ownership_only_when_it_reaches_a_site(string effect, bool unowned)
    {
        string helpers = "static class Walker { "
            + "public static void Walk(System.Collections.Generic.Stack<int> work) { while (work.TryPop(out int next)) { Step(work, next); } } "
            + "private static void Step(System.Collections.Generic.Stack<int> work, int next) { " + effect + "if (next > 0) { Walk(work); } } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "_ = Task.Run(() => Walker.Walk(new System.Collections.Generic.Stack<int>()));", helpers));

        Assert.Equal(unowned, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverBoundedXmlReader(string fixture) =>
        Discover(FixtureSource("_ = RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.PartReader.Run(new byte[] { 60 });", fixture));

    [Fact]
    public void A_bounded_xml_reader_over_memory_reads_no_file()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverBoundedXmlReader(BoundedXmlReaderFixture);

        Assert.DoesNotContain(result.Items, static site => site.Callee.StartsWith("System.Xml.", StringComparison.Ordinal)
            || site.Callee.StartsWith("System.IO.", StringComparison.Ordinal));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.StartsWith("System.Xml.", StringComparison.Ordinal));
    }

    // Each mutation breaks one premise of the proof, and the reader members it covers are refused again.
    [Theory]
    [InlineData("System.Xml.DtdProcessing.Ignore", "System.Xml.DtdProcessing.Parse", "System.Xml.XmlReader.Create")]
    [InlineData("XmlResolver = null,", "XmlResolver = new System.Xml.XmlUrlResolver(),", "System.Xml.XmlReader.Create")]
    [InlineData("IgnoreComments = true,", "ValidationType = System.Xml.ValidationType.Schema,", "System.Xml.XmlReader.Create")]
    [InlineData("System.IO.MemoryStream stream = OpenStream(part);", "System.IO.Stream stream = System.IO.File.OpenRead(\"part\");", "System.Xml.XmlReader.Create")]
    [InlineData("System.IO.MemoryStream stream = OpenStream(part);", "System.IO.MemoryStream stream = OpenStream(part); settings.DtdProcessing = System.Xml.DtdProcessing.Parse;", "System.Xml.XmlReader.Create")]
    [InlineData("private BoundedXmlReader(System.Xml.XmlReader reader)", "public BoundedXmlReader(System.Xml.XmlReader reader)", "System.Xml.XmlReader.Read")]
    [InlineData("internal sealed class BoundedXmlReader", "internal class BoundedXmlReader", "System.Xml.XmlReader.Read")]
    [InlineData("public System.Xml.XmlReader Reader { get; }", "public System.Xml.XmlReader Reader { get; private set; }", "System.Xml.XmlReader.Read")]
    [InlineData("_reader = xml.Reader;", "_reader = System.Xml.XmlReader.Create(new System.IO.MemoryStream(), new System.Xml.XmlReaderSettings());", "System.Xml.XmlReader.Value")]
    public void A_bounded_xml_reader_premise_that_does_not_hold_is_refused(string original, string mutation, string refused)
    {
        Assert.Contains(original, BoundedXmlReaderFixture, StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverBoundedXmlReader(BoundedXmlReaderFixture.Replace(original, mutation, StringComparison.Ordinal));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == refused);
    }
}
