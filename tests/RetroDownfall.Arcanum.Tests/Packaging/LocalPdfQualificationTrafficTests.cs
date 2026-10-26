namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class LocalPdfQualificationTrafficTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("text-model")]
    [InlineData("embedding-model")]
    [InlineData("chat-credential")]
    [InlineData("embedding-credential")]
    [InlineData("pin-source")]
    [InlineData("pin-status")]
    [InlineData("missing-page")]
    [InlineData("missing-inspection")]
    [InlineData("duplicate-chat")]
    [InlineData("failed-embedding")]
    [InlineData("wrong-kind")]
    public void Provider_evidence_requires_two_text_calls_current_pin_source_and_real_embedding_role(string defect)
    {
        Guid attachment = Guid.Parse("abcdef12-3456-4789-abcd-ef1234567890");

        PdfQualificationChallenge challenge = new("a123456789abcdef0123456789abcdef", "b123456789abcdef0123456789abcdef", 71, 8, 83, 11);

        string document = "[PDF page 1]\n" + string.Join('\n', challenge.DocumentLines);

        string pin = "source-kind: Attachment\nsource-label: evidence\nsource-id: " + attachment.ToString("N") + "\nstatus: Current";

        string secondBody = pin + "\n" + document;

        if (defect == "pin-source") secondBody = secondBody.Replace(attachment.ToString("N"), Guid.NewGuid().ToString("N"), StringComparison.Ordinal);

        if (defect == "pin-status") secondBody = secondBody.Replace("status: Current", "status: Error", StringComparison.Ordinal);

        if (defect == "missing-page") secondBody = secondBody.Replace("[PDF page 1]", "", StringComparison.Ordinal);

        if (defect == "missing-inspection") secondBody = secondBody.Replace(challenge.InspectionSeal, "missing", StringComparison.Ordinal);

        LongRestRecordedRequest first = new()
        {
            Sequence = 1,
            Kind = LongRestProviderRequestKind.Chat,
            Model = defect == "text-model" ? "wrong" : "flint:40b",
            Credential = defect == "chat-credential" ? LongRestRequestCredential.Embedding : LongRestRequestCredential.Chat,
            UserText = PdfQualificationChallenge.FirstPrompt,
            ProviderVisibleText = document,
            UpstreamCompleted = true,
            Delivered = true,
            StatusCode = 200,
        };

        LongRestRecordedRequest second = new()
        {
            Sequence = 2,
            Kind = LongRestProviderRequestKind.Chat,
            Model = "flint:40b",
            Credential = LongRestRequestCredential.Chat,
            UserText = PdfQualificationChallenge.SecondPrompt,
            ProviderVisibleText = secondBody,
            UpstreamCompleted = true,
            Delivered = true,
            StatusCode = 200,
        };

        LongRestRecordedRequest embedding = new()
        {
            Sequence = 3,
            Kind = defect == "wrong-kind" ? LongRestProviderRequestKind.Models : LongRestProviderRequestKind.Embedding,
            Model = defect == "embedding-model" ? "wrong" : "qwen3-embedding:8b",
            Credential = defect == "embedding-credential" ? LongRestRequestCredential.Chat : LongRestRequestCredential.Embedding,
            ProviderVisibleText = document,
            UpstreamCompleted = true,
            Delivered = true,
            StatusCode = defect == "failed-embedding" ? 401 : 200,
        };

        List<LongRestRecordedRequest> traffic = [first, second, embedding];

        if (defect == "duplicate-chat") traffic.Add(first);

        Assert.Equal(defect == "none", PdfQualificationEvidence.ProviderTrafficMatches(traffic,
            "flint:40b", "qwen3-embedding:8b", attachment, challenge));
    }
}
