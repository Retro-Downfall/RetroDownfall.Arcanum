using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Packaging;

[Collection("ProcessEnvironment")]
public sealed class LocalPdfQualificationEvidenceTests
{
    private static readonly Guid Session = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid Attachment = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Pdf_strength_receipt_requires_completed_pdf_even_after_other_phases_pass()
    {
        Assert.Throws<InvalidOperationException>(() => PdfQualificationEvidence.Receipt(pdf: true, perplexity: false, pdfCompleted: false));

        Assert.Equal("local-ollama-aot-pdf-qualification:v1", PdfQualificationEvidence.Receipt(true, false, true));

        Assert.Equal("local-ollama-aot-pdf-perplexity-qualification:v1", PdfQualificationEvidence.Receipt(true, true, true));

        Assert.Equal("local-ollama-aot-qualification:v1", PdfQualificationEvidence.Receipt(false, false, false));

        Assert.Equal("local-ollama-aot-perplexity-qualification:v1", PdfQualificationEvidence.Receipt(false, true, false));
    }

    [Fact]
    public void Uploaded_pdf_requires_exact_session_binary_mime_hash_length_name_and_version()
    {
        byte[] pdf = "fixed-pdf"u8.ToArray();

        SessionAttachmentDto dto = new(Attachment, PdfQualificationChallenge.LogicalName, PdfQualificationChallenge.FileName,
            1, "bound/fixture.arcblob", "application/pdf", pdf.Length, SessionAttachmentKind.Binary,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pdf)), DateTimeOffset.UtcNow, SessionId: Session);

        Assert.True(PdfQualificationEvidence.AttachmentMatches(dto, Session, pdf));

        foreach (var wrong in new[]
        {
            dto with { SessionId = Guid.NewGuid() }, dto with { Kind = SessionAttachmentKind.Text },
            dto with { MimeType = "text/plain" }, dto with { ContentSha256 = new string('0', 64) },
            dto with { ByteLength = 1 }, dto with { Version = 2 }, dto with { OriginalFileName = "answer.pdf" },
            dto with { LogicalKey = "answer" }, dto with { ContentSha256 = dto.ContentSha256.ToLowerInvariant() },
            dto with { ContentSha256 = dto.ContentSha256 + "0" }, dto with { SessionId = null },
            dto with { Id = Guid.Empty },
        }) Assert.False(PdfQualificationEvidence.AttachmentMatches(wrong, Session, pdf));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("attachment")]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("generation")]
    [InlineData("status")]
    [InlineData("dimension")]
    [InlineData("vector")]
    [InlineData("vector-dimension")]
    [InlineData("content")]
    [InlineData("page")]
    [InlineData("timestamp")]
    public void Published_index_grader_rejects_wrong_source_generation_page_or_vector_evidence(string defect)
    {
        PdfQualificationChallenge challenge = new("a123456789abcdef0123456789abcdef", "b123456789abcdef0123456789abcdef", 71, 8, 83, 11);

        var expected = new PdfPublishedChunk(Session, Attachment, 1, "hash", "application/pdf", "Indexed", "hash",
            "generation", "generation", "[PDF page 1]\n" + string.Join('\n', challenge.DocumentLines), 64, 64, 256,
            "2026-10-26T12:00:00.0000000+00:00", "2026-10-26T12:00:01.0000000+00:00");

        Assert.True(PdfQualificationEvidence.IndexMatches([expected], Session, Attachment, "hash", 64, challenge));

        var wrong = defect switch
        {
            "session" => expected with { SessionId = Guid.NewGuid() },
            "attachment" => expected with { AttachmentId = Guid.NewGuid() },
            "hash" => expected with { StateHash = "stale" },
            "version" => expected with { Version = 2 },
            "generation" => expected with { ChunkGeneration = "stale" },
            "status" => expected with { Status = "Pending" },
            "dimension" => expected with { ChunkDimension = 63 },
            "vector" => expected with { VectorBytes = 252 },
            "vector-dimension" => expected with { VectorDimension = 63 },
            "content" => expected with { Content = "[PDF page 1] seals only " + challenge.FirstSeal + challenge.SecondSeal },
            "page" => expected with { Content = string.Join('\n', challenge.DocumentLines) },
            _ => expected with { ExtractedAt = "NULL" },
        };

        Assert.False(PdfQualificationEvidence.IndexMatches([wrong], Session, Attachment, "hash", 64, challenge));

        Assert.False(PdfQualificationEvidence.IndexMatches([], Session, Attachment, "hash", 64, challenge));
    }

    [Fact]
    public void Transcript_grader_requires_two_text_replies_exact_pairs_and_unchanged_first_turn()
    {
        JsonElement[] first = [Row("user", "first"), Row("assistant", "reply-one")];

        JsonElement[] final = [.. first, Row("user", "second"), Row("assistant", "reply-two")];

        Assert.True(PdfQualificationEvidence.TranscriptMatches(first, final, Session,
            ["first", "second"], ["reply-one", "reply-two"], ["flint:40b", "flint:40b"], "flint:40b"));

        Assert.False(PdfQualificationEvidence.TranscriptMatches(first, final, Session,
            ["first", "second"], ["reply-one", "reply-two"], ["flint:40b", "qwen3.8:27b"], "flint:40b"));

        JsonElement[] changed = [Row("user", "first"), .. final[1..]];

        Assert.False(PdfQualificationEvidence.TranscriptMatches(first, changed, Session,
            ["first", "second"], ["reply-one", "reply-two"], ["flint:40b", "flint:40b"], "flint:40b"));

        Assert.False(PdfQualificationEvidence.TranscriptMatches(first, [.. final, Row("tool", "extra")], Session,
            ["first", "second"], ["reply-one", "reply-two"], ["flint:40b", "flint:40b"], "flint:40b"));
    }

    [Fact]
    public void Payload_envelope_and_private_content_checks_reject_plain_pdf_or_seal_bytes()
    {
        PdfQualificationChallenge challenge = new("a123456789abcdef0123456789abcdef", "b123456789abcdef0123456789abcdef", 71, 8, 83, 11);

        byte[] pdf = challenge.BuildPdf();

        Assert.True(PdfQualificationEvidence.EncryptedPayloadMatches("ARCABLOBciphertext-fixture"u8.ToArray(), pdf, challenge));

        Assert.False(PdfQualificationEvidence.EncryptedPayloadMatches(pdf, pdf, challenge));

        Assert.False(PdfQualificationEvidence.EncryptedPayloadMatches([.. "ARCABLOB"u8, .. pdf], pdf, challenge));

        Assert.False(PdfQualificationEvidence.EncryptedPayloadMatches(Encoding.ASCII.GetBytes("ARCABLOB" + challenge.FirstSeal), pdf, challenge));

        Assert.False(PdfQualificationEvidence.EncryptedPayloadMatches(Encoding.ASCII.GetBytes("ARCABLOB" + challenge.SecondSeal), pdf, challenge));
    }

    private static JsonElement Row(string role, string content)
    {
        JsonObject row = new()
        {
            ["id"] = Guid.NewGuid().ToString("D"),
            ["sessionId"] = Session.ToString("D"),
            ["role"] = role,
            ["content"] = content,
            ["toolName"] = null,
            ["toolCallId"] = null,
        };

        using var document = JsonDocument.Parse(row.ToJsonString());

        return document.RootElement.Clone();
    }
}
