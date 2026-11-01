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

    [Theory]
    [InlineData(@"{\rtf1{\*\stylesheet{\s0\v Hidden Normal;}}IMPLICIT-SECRET}")]
    [InlineData(@"{\rtf1{\*\defchp\v}IMPLICIT-SECRET}")]
    [InlineData(@"{\rtf1{\*\defpap\v}IMPLICIT-SECRET}")]
    [InlineData(@"{\rtf1{\stylesheet{\*\cs1\additive\v Hidden;}}A{\cs1 SECRET}B}")]
    [InlineData(@"{\rtf1{\stylesheet{\s1\v Hidden;}}A{\s1 SECRET}B}")]
    [InlineData(@"{\rtf1{\stylesheet{\s0\v Hidden Normal;}}IMPLICIT-SECRET}")]
    [InlineData(@"{\rtf1{\stylesheet{\v Hidden Normal;}}IMPLICIT-SECRET}")]
    [InlineData(@"{\rtf1{\stylesheet{\s0\sbasedon1 Normal;}{\s1\sbasedon0\v Hidden;}}SECRET}")]
    [InlineData(@"{\rtf1{\stylesheet{\s0 Visible Normal;}}ordinary text}")]
    [InlineData(@"{\rtf1 A{\cs1\v0 SECRET}B}")]
    [InlineData(@"{\rtf1\s1 SECRET}")]
    [InlineData(@"{\rtf1\ts1 SECRET}")]
    [InlineData(@"{\rtf1\ds1 SECRET}")]
    [InlineData(@"{\rtf1\defcs1 SECRET}")]
    [InlineData(@"{\rtf1\defds1 SECRET}")]
    [InlineData(@"{\rtf1{\defchp\v}IMPLICIT-SECRET}")]
    [InlineData(@"{\rtf1{\defpap\v}IMPLICIT-SECRET}")]
    public async Task Extract_declines_style_dependent_visibility_instead_of_exposing_unknown_text(string rtf)
    {
        SessionAttachmentExtractionResult result = await Extract(rtf);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);

        Assert.Equal("Document extraction failed.", result.FailureReason);
    }

    [Fact]
    public async Task Extract_stylesheet_after_output_ceiling_still_fails()
    {
        SessionAttachmentExtractionResult result = await Extract("{\\rtf1 " + new string('a', 5000) + @"{\stylesheet{\s0\v Hidden;}}}", 4);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);
    }

    [Theory]
    [InlineData(@"{\rtf1 before{\*\unknown{\stylesheet{\s0\v Hidden;}}\cs1 ignored}after}")]
    [InlineData(@"{\rtf1 before{\object{\stylesheet{\s0\v Hidden;}}\defchp ignored}after}")]
    public async Task Extract_does_not_interpret_styles_inside_opaque_destinations(string rtf)
    {
        Assert.Equal("beforeafter", (await Extract(rtf)).Text);
    }

    [Theory]
    [InlineData("fcharset0")]
    [InlineData("cpg1252")]
    [InlineData("ansicpg1252")]
    public async Task Extract_font_metadata_with_inherited_font_ids_cannot_exceed_font_capacity(string declaration)
    {
        string rtf = "{\\rtf1 " + InheritedFontDeclarations(4097, declaration) + "text}";

        SessionAttachmentExtractionResult result = await Extract(rtf);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task Extract_normal_font_declarations_cannot_extend_a_bypassed_font_capacity()
    {
        string rtf = "{\\rtf1 " + InheritedFontDeclarations(4097, "fcharset0") + @"{\fonttbl\f6000\fcharset0 Extra;}text}";

        SessionAttachmentExtractionResult result = await Extract(rtf);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task Extract_existing_font_metadata_can_be_updated_at_capacity()
    {
        string rtf = "{\\rtf1 " + InheritedFontDeclarations(4096, "fcharset0") + @"\f0{\fonttbl\fcharset204}\'cf\'f0\'e8\'e2\'e5\'f2}";

        Assert.Equal("Привет", (await Extract(rtf)).Text);
    }

    private static string InheritedFontDeclarations(int count, string declaration)
    {
        StringBuilder rtf = new();

        for (int font = 0; font < count; font++)
        {
            rtf.Append("\\f").Append(font).Append("{\\fonttbl\\").Append(declaration).Append('}');
        }

        return rtf.ToString();
    }

    private static Task<SessionAttachmentExtractionResult> Extract(string rtf, int maximumCharacters = 262_144)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(rtf);

        SessionAttachmentDocumentExtractionHost host = new(new RtfDocumentExtractor().ExtractAsync, TimeSpan.FromSeconds(5), maximumCharacters);

        return host.ExtractAsync(_ => Task.FromResult<ReadOnlyMemory<byte>>(bytes), bytes.Length, 32_000_000, CancellationToken.None);
    }
}
