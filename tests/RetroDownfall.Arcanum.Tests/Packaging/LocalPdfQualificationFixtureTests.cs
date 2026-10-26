using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;
using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class LocalPdfQualificationFixtureTests
{
    private static PdfQualificationChallenge Fixed() => new("a123456789abcdef0123456789abcdef", "b123456789abcdef0123456789abcdef", 71, 8, 83, 11);

    [Fact]
    public void Deterministic_one_page_pdf_has_exact_xref_and_stream_lengths_and_real_extractable_seals()
    {
        var challenge = Fixed();

        byte[] bytes = challenge.BuildPdf();

        Assert.Equal(bytes, Fixed().BuildPdf());

        Assert.Equal(833, bytes.Length);

        Assert.Equal("3f4a02ffb7c1dd2610e518bed31851bb9c9c08701e5b01bb9901c76472898b82", Convert.ToHexStringLower(SHA256.HashData(bytes)));

        string pdf = Encoding.ASCII.GetString(bytes);

        Assert.StartsWith("%PDF-1.4\n", pdf, StringComparison.Ordinal);

        Match xref = Regex.Match(pdf, @"xref\n0 6\n0000000000 65535 f \n((?:[0-9]{10} 00000 n \n){5})");

        Assert.True(xref.Success);

        string[] offsets = xref.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        for (int index = 0; index < offsets.Length; index++)
        {
            int offset = int.Parse(offsets[index][..10], CultureInfo.InvariantCulture);

            Assert.StartsWith($"{index + 1} 0 obj\n", pdf[offset..], StringComparison.Ordinal);
        }

        int startxref = int.Parse(Regex.Match(pdf, @"startxref\n([0-9]+)\n%%EOF").Groups[1].Value, CultureInfo.InvariantCulture);

        Assert.Equal(xref.Index, startxref);

        Match stream = Regex.Match(pdf, @"/Length ([0-9]+) >>\nstream\n([\s\S]*?)\nendstream");

        Assert.Equal(int.Parse(stream.Groups[1].Value, CultureInfo.InvariantCulture), Encoding.ASCII.GetByteCount(stream.Groups[2].Value));

        var extracted = new PdfTextExtractor().Extract(bytes, PdfExtractionLimits.Default, CancellationToken.None);

        Assert.Equal(DocumentExtractionKind.Extracted, extracted.Kind);

        string text = Assert.Single(extracted.Pages);

        Assert.All(challenge.DocumentLines, line => Assert.Contains(line, text, StringComparison.Ordinal));

        Assert.False(extracted.Truncated);

        Assert.DoesNotContain("/JavaScript", pdf, StringComparison.Ordinal);

        Assert.DoesNotContain("/URI", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void Writer_escapes_parentheses_and_backslashes_and_rejects_non_printable_or_non_ascii_lines()
    {
        const string line = "Cargo (sample) \\ checksum";

        byte[] bytes = PdfQualificationChallenge.BuildPdf([line]);

        var extracted = new PdfTextExtractor().Extract(bytes, PdfExtractionLimits.Default, CancellationToken.None);

        Assert.Equal(DocumentExtractionKind.Extracted, extracted.Kind);

        Assert.Contains(line, Assert.Single(extracted.Pages), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => PdfQualificationChallenge.BuildPdf(["a\nb"]));

        Assert.Throws<ArgumentException>(() => PdfQualificationChallenge.BuildPdf(["caf\u00e9"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Arithmetic_and_distinct_fact_graders_require_exact_answers_without_leaking_answers_to_prompts(bool firstWins)
    {
        var challenge = firstWins ? Fixed() with { FirstQuantity = 95 } : Fixed();

        Assert.Equal(firstWins ? "SEAL=a123456789abcdef0123456789abcdef; NET=87" : "SEAL=b123456789abcdef0123456789abcdef; NET=72", challenge.FirstExpected);

        Assert.Equal(firstWins ? "INSPECTION=b123456789abcdef0123456789abcdef" : "INSPECTION=a123456789abcdef0123456789abcdef", challenge.SecondExpected);

        Assert.DoesNotContain(challenge.InspectionSeal, challenge.FirstExpected, StringComparison.Ordinal);

        Assert.True(challenge.MatchesFirstAnswer(challenge.FirstExpected));

        Assert.True(challenge.MatchesSecondAnswer(challenge.SecondExpected));

        foreach (string wrong in new[] { "", challenge.SecondExpected, challenge.FirstExpected + "; EXTRA=yes", challenge.FirstExpected.Replace("NET=", "NET=0", StringComparison.Ordinal) })
        {
            Assert.False(challenge.MatchesFirstAnswer(wrong));
        }

        Assert.False(challenge.MatchesSecondAnswer(challenge.FirstExpected));

        Assert.False(challenge.MatchesSecondAnswer(challenge.SecondExpected + " extra"));

        Assert.DoesNotContain(challenge.FirstSeal, PdfQualificationChallenge.FirstPrompt, StringComparison.Ordinal);

        Assert.DoesNotContain(challenge.SecondSeal, PdfQualificationChallenge.FirstPrompt, StringComparison.Ordinal);

        Assert.DoesNotContain(challenge.FirstSeal, PdfQualificationChallenge.SecondPrompt, StringComparison.Ordinal);

        Assert.DoesNotContain(challenge.SecondSeal, PdfQualificationChallenge.SecondPrompt, StringComparison.Ordinal);

        Assert.DoesNotContain(challenge.FirstExpected, PdfQualificationChallenge.Metadata, StringComparison.Ordinal);

        Assert.DoesNotContain(challenge.SecondExpected, PdfQualificationChallenge.Metadata, StringComparison.Ordinal);
    }
}
