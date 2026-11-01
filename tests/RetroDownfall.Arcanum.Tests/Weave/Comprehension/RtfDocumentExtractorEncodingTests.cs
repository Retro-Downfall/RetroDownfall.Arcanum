using System.Net;

using System.Net.Sockets;

using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class RtfDocumentExtractorEncodingTests
{
    [Theory]
    [InlineData(@"{\rtf1\ansicpg1252 \'80\'91\'92}", "€‘’")]
    [InlineData(@"{\rtf1\ansicpg1252{\fonttbl{\f0\fcharset0 Arial;}{\f1\fcharset204 Arial;}}\f1 \'cf\'f0\'e8\'e2\'e5\'f2}", "Привет")]
    [InlineData(@"{\rtf1\ansicpg1252{\fonttbl{\f0\cpg1251 Arial;}}\f0 \'cf\'f0\'e8\'e2\'e5\'f2}", "Привет")]
    [InlineData(@"{\rtf1\deff1{\fonttbl{\f0\fcharset0 Arial;}{\f1\fcharset204 Arial;}}\'cf\'f0\'e8\'e2\'e5\'f2}", "Привет")]
    [InlineData(@"{\rtf1\uc1 \u945{X}\u946\tab Y}", "αXβY")]
    [InlineData(@"{\rtf1\uc1 \u945\*X}", "αX")]
    [InlineData(@"{\rtf1 A{\deleted hide{\deleted0 visible}hide}B}", "AvisibleB")]
    public async Task Extract_honors_font_encoding_and_scoped_properties(string rtf, string expected)
    {
        SessionAttachmentExtractionResult result = await Extract(rtf);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData(@"{\rtf1{\fonttbl{\f0\fcharset128 MS Gothic;}}\f0 text}")]
    [InlineData(@"{\rtf1{\fonttbl{\f0\cpg932 MS Gothic;}}\f0 text}")]
    [InlineData(@"{\rtf1 before\'00after}")]
    public async Task Extract_rejects_unsupported_font_codepage_or_nul_escape(string rtf)
    {
        SessionAttachmentExtractionResult result = await Extract(rtf);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("objdata")]
    [InlineData("objclass")]
    [InlineData("objname")]
    [InlineData("objalias")]
    [InlineData("objsect")]
    [InlineData("result")]
    [InlineData("pict")]
    [InlineData("shppict")]
    [InlineData("nonshppict")]
    [InlineData("fldinst")]
    public async Task Extract_object_and_field_destinations_cannot_leak_nested_content(string destination)
    {
        SessionAttachmentExtractionResult result = await Extract("{\\rtf1 before{\\" + destination + " LEAK{\\v0 nested leak}}after}");

        Assert.Equal("beforeafter", result.Text);
    }

    [Theory]
    [InlineData(@"{\rtf1 before{\*\unknown\ansicpg932\uc999\u0 payload}after}")]
    [InlineData(@"{\rtf1 before{\object\cpg932\u-999999 payload{\result ignored}}after}")]
    [InlineData(@"{\rtf1 before{\pict\ansicpg42\u0\'00}after}")]
    public async Task Extract_ignored_destinations_have_no_character_semantics(string rtf)
    {
        Assert.Equal("beforeafter", (await Extract(rtf)).Text);
    }

    [Theory]
    [InlineData(@"{\rtf1{\upr{ANSI-LEAK}{\*\ud{Unicode visible}}}}", "Unicode visible")]
    [InlineData(@"{\rtf1 before{\upr{ANSI-LEAK}{\*\ud Unicode \uc0\u945}}after}", "beforeUnicode αafter")]
    [InlineData(@"{\rtf1{\upr{ANSI-LEAK}{\*\ud A{\upr{NESTED-ANSI}{\*\ud B}}C}}}", "ABC")]
    [InlineData(@"{\rtf1{\upr{\ansicpg932\u0 ANSI}{\*\ud Unicode}}}", "Unicode")]
    [InlineData(@"{\rtf1{\upr{\bin5 {\}{}ANSI}{\*\ud Unicode}}}", "Unicode")]
    [InlineData(@"{\rtf1 before{\webhidden\v0 HIDDEN-LEAK}after}", "beforeafter")]
    [InlineData(@"{\rtf1 before{\v\webhidden0 HIDDEN-LEAK}after}", "beforeafter")]
    [InlineData(@"{\rtf1 before{\webhidden hidden{\webhidden0 visible}hidden}after}", "beforevisibleafter")]
    [InlineData(@"{\rtf1 before{\webhidden\v\plain visible}after}", "beforevisibleafter")]
    public async Task Extract_selects_unicode_alternatives_and_keeps_hidden_properties_independent(string rtf, string expected)
    {
        Assert.Equal(expected, (await Extract(rtf)).Text);
    }

    [Theory]
    [InlineData(@"{\rtf1{\upr{ANSI-LEAK}}}")]
    [InlineData(@"{\rtf1{\upr{ANSI-LEAK}{Unicode without marker}}}")]
    [InlineData(@"{\rtf1{\upr{ANSI-LEAK}{\*\unknown Unicode}}}")]
    [InlineData(@"{\rtf1{\upr{ANSI-LEAK}{\*\ud Unicode}{EXTRA-LEAK}}}")]
    [InlineData(@"{\rtf1{\upr TEXT-LEAK{ANSI}{\*\ud Unicode}}}")]
    [InlineData(@"{\rtf1{\upr{ANSI-LEAK}{\*\ud Unicode}TAIL-LEAK}}")]
    [InlineData(@"{\rtf1{\upr{ANSI-LEAK}{\*\ud Unicode\'zz}}}")]
    public async Task Extract_rejects_malformed_unicode_alternatives_without_ansi_fallback(string rtf)
    {
        SessionAttachmentExtractionResult result = await Extract(rtf);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task Extract_never_resolves_external_fields_or_objects()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);

        listener.Start();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string target = $"http://127.0.0.1:{port}/rtf-external-sentinel";

        string rtf = "{\\rtf1 visible{\\object\\objautlink{\\objdata " + target + "}{\\result OBJECT-LEAK}}"
            + "{\\field{\\*\\fldinst INCLUDETEXT \"" + target + "\"}{\\fldrslt label}}}";

        SessionAttachmentExtractionResult result = await Extract(rtf);

        Assert.Equal("visiblelabel", result.Text);

        await Task.Delay(100);

        Assert.False(listener.Pending(), "RTF extraction followed an external reference.");
    }

    [Fact]
    public async Task Extract_malformed_unicode_alternative_after_output_ceiling_still_fails()
    {
        SessionAttachmentExtractionResult result = await Extract("{\\rtf1 " + new string('a', 5000) + @"{\upr{ANSI-LEAK}}}", 4);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);
    }

    private static Task<SessionAttachmentExtractionResult> Extract(string rtf, int maximumCharacters = 262_144)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(rtf);

        SessionAttachmentDocumentExtractionHost host = new(new RtfDocumentExtractor().ExtractAsync, TimeSpan.FromSeconds(5), maximumCharacters);

        return host.ExtractAsync(_ => Task.FromResult<ReadOnlyMemory<byte>>(bytes), bytes.Length, 32_000_000, CancellationToken.None);
    }
}
