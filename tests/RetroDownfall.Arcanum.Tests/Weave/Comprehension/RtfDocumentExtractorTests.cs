using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class RtfDocumentExtractorTests
{
    [Theory]
    [InlineData(@"{\rtf1\ansi Hello {\b bold} world\par Next\tab cell\line end}", "Hello bold world\nNext\tcell\nend")]
    [InlineData(@"{\rtf1 a\{b\}c\\d\~e\_f\-g}", "a{b}c\\d\u00a0e-fg")]
    [InlineData("{\\rtf1 a\r\nb  c}", "ab  c")]
    [InlineData(@"{\rtf1 \emdash\endash\bullet\lquote x\rquote\ldblquote y\rdblquote}", "—–•‘x’“y”")]
    [InlineData(@"{\rtf1\ansi\ansicpg1252 caf\'e9 \u8364?}", "café €")]
    [InlineData(@"{\rtf1\ansi\ansicpg1251 \'cf\'f0\'e8\'e2\'e5\'f2}", "Привет")]
    [InlineData(@"{\rtf1\ansi\ansicpg28591 \'a3\'e9}", "£é")]
    [InlineData(@"{\rtf1\uc1 \u945?{\uc2\u946ab}\u947\'3f\uc0\u948}", "αβγδ")]
    [InlineData(@"{\rtf1\uc1 \u-10179?\u-8704?}", "😀")]
    [InlineData(@"{\rtf1 A{\v hidden{\v0 visible}}B{\deleted gone}C\v invisible\v0 D}", "AvisibleBCD")]
    [InlineData(@"{\rtf1 A{\*\mystery hidden{nested}}B{\info{\author secret}}C{\fonttbl{\f0 Secret;}}D}", "ABCD")]
    [InlineData(@"{\rtf1 A{\object\objdata dead{\result DANGER}}B{\pict 00ff}C{\annotation private}D}", "ABCD")]
    [InlineData(@"{\rtf1 A{\field{\*\fldinst INCLUDEPICTURE evil}{\fldrslt visible}}B}", "AvisibleB")]
    [InlineData(@"{\rtf1 A{\*\mystery\bin5 {\}{}tail}B}", "AB")]
    [InlineData(@"{\rtf1\uc1 \u945\bin3 {\}B}", "αB")]
    public async Task Extract_projects_visible_text_without_formatting_or_excluded_content(string rtf, string expected)
    {
        SessionAttachmentExtractionResult result = await Extract(rtf);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData(@"{\rtf1 {\v hidden{hidden too}}}")]
    [InlineData(@"{\rtf1 {\deleted deleted{deleted too}}}")]
    [InlineData(@"{\rtf1 {\*\unknown private}}")]
    [InlineData(@"{\rtf1 {\pict 1234}}")]
    public async Task Extract_excluded_only_documents_are_not_eligible(string rtf)
    {
        SessionAttachmentExtractionResult result = await Extract(rtf);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Empty(result.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData(@"{\rtf2 text}")]
    [InlineData(@"{\rtf1 text")]
    [InlineData(@"{\rtf1 text}}")]
    [InlineData(@"{\rtf1 text}LEAK")]
    [InlineData(@"{\rtf1 \'xy}")]
    [InlineData(@"{\rtf1 \'a}")]
    [InlineData(@"{\rtf1 text\")]
    [InlineData(@"{\rtf1 \bin-1 x}")]
    [InlineData(@"{\rtf1 \bin999 x}")]
    [InlineData(@"{\rtf1 \bin x}")]
    [InlineData(@"{\rtf1 \uc-1 x}")]
    [InlineData(@"{\rtf1 \uc17 x}")]
    [InlineData(@"{\rtf1 \u65536?}")]
    [InlineData(@"{\rtf1 \u-32769?}")]
    [InlineData(@"{\rtf1 \u-10179?}")]
    [InlineData(@"{\rtf1 \u-8704?}")]
    [InlineData(@"{\rtf1 \u0?}")]
    [InlineData(@"{\rtf1 \foo2147483648 x}")]
    [InlineData(@"{\rtf1 \foo-2147483649 x}")]
    [InlineData(@"{\rtf1 \abcdefghijklmnopqrstuvwxyzabcdefg x}")]
    [InlineData(@"{\rtf1\ansicpg932 x}")]
    [InlineData(@"{\rtf1\mac x}")]
    [InlineData(@"{\rtf1\pc x}")]
    public async Task Extract_rejects_malformed_or_unsupported_input_with_closed_failure(string rtf)
    {
        SessionAttachmentExtractionResult result = await Extract(rtf);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);

        Assert.Equal("Document extraction failed.", result.FailureReason);
    }

    [Fact]
    public async Task Extract_validates_malformed_tail_before_output_ceiling()
    {
        SessionAttachmentExtractionResult result = await Extract("{\\rtf1 " + new string('a', 5000) + "\\'zz}", 4);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task Extract_valid_output_beyond_ceiling_is_truncated()
    {
        SessionAttachmentExtractionResult result = await Extract(@"{\rtf1 abcdef}", 4);

        Assert.Equal("abcd", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public async Task Extract_group_depth_is_bounded_without_recursion()
    {
        SessionAttachmentExtractionResult result = await Extract("{\\rtf1 " + new string('{', 256) + "text" + new string('}', 257));

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Extract_control_count_is_bounded()
    {
        SessionAttachmentExtractionResult result = await Extract("{\\rtf1 " + string.Concat(Enumerable.Repeat("\\b ", 1_000_001)) + "text}");

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Extract_input_length_is_bounded()
    {
        SessionAttachmentExtractionResult result = await Extract("{\\rtf1 " + new string('a', 16_777_216) + "}");

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Extract_raw_ansi_bytes_use_the_declared_codepage()
    {
        byte[] bytes = Encoding.Latin1.GetBytes("{\\rtf1\\ansi\\ansicpg1251 \u00cf\u00f0\u00e8\u00e2\u00e5\u00f2}");

        Assert.Equal("Привет", (await Extract(bytes)).Text);
    }

    [Fact]
    public async Task Extract_stops_immediately_when_sink_refuses()
    {
        RefusingSink sink = new();

        await new RtfDocumentExtractor().ExtractAsync(Encoding.ASCII.GetBytes("{\\rtf1 " + new string('a', 10_000) + "}"), sink, CancellationToken.None);

        Assert.Equal(1, sink.Calls);
    }

    [Fact]
    public void Extract_honors_cancellation_before_scanning()
    {
        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new RtfDocumentExtractor().ExtractAsync(
            "{\\rtf1 text}"u8.ToArray(), new RefusingSink(), cancellation.Token));
    }

    [Fact]
    public async Task Extract_independent_textutil_fixture_preserves_unicode_and_paragraphs()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "Rtf", "textutil-basic.rtf"));

        SessionAttachmentExtractionResult result = await Extract(bytes);

        Assert.Equal("Textutil RTF fixture\nVisible paragraphs preserve café and Ελληνικά.\nSecond paragraph contains a tab:\tvalue.\n", result.Text);
    }

    private static Task<SessionAttachmentExtractionResult> Extract(string rtf, int maximumCharacters = 262_144) =>
        Extract(Encoding.Latin1.GetBytes(rtf), maximumCharacters);

    private static Task<SessionAttachmentExtractionResult> Extract(byte[] bytes, int maximumCharacters = 262_144)
    {
        SessionAttachmentDocumentExtractionHost host = new(new RtfDocumentExtractor().ExtractAsync, TimeSpan.FromSeconds(5), maximumCharacters);

        return host.ExtractAsync(_ => Task.FromResult<ReadOnlyMemory<byte>>(bytes), bytes.Length, 32_000_000, CancellationToken.None);
    }

    private sealed class RefusingSink : ISessionAttachmentDocumentTextSink
    {
        public int Calls { get; private set; }

        public bool TryAppend(ReadOnlySpan<char> text)
        {
            Calls++;

            Assert.Equal(1, Calls);

            return false;
        }

        public void MarkTruncated() { }
    }
}
