using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Packaging;

internal sealed record PdfPublishedChunk(Guid SessionId, Guid AttachmentId, int Version, string Hash, string Mime,
    string Status, string StateHash, string PublishedGeneration, string ChunkGeneration, string Content,
    int ChunkDimension, int VectorDimension, int VectorBytes, string ExtractedAt, string IndexedAt);

internal static class PdfQualificationEvidence
{
    public static string Receipt(bool pdf, bool perplexity, bool pdfCompleted)
    {
        if (pdf && !pdfCompleted) throw new InvalidOperationException("PDF qualification did not complete.");

        return (pdf, perplexity) switch
        {
            (true, true) => "local-ollama-aot-pdf-perplexity-qualification:v1",
            (true, false) => "local-ollama-aot-pdf-qualification:v1",
            (false, true) => "local-ollama-aot-perplexity-qualification:v1",
            _ => "local-ollama-aot-qualification:v1",
        };
    }

    public static bool AttachmentMatches(SessionAttachmentDto attachment, Guid sessionId, byte[] pdf) =>
        attachment.Id != Guid.Empty && attachment.SessionId == sessionId && attachment.Version == 1
            && attachment.Kind == SessionAttachmentKind.Binary && attachment.MimeType == "application/pdf"
            && attachment.LogicalKey == PdfQualificationChallenge.LogicalName
            && attachment.OriginalFileName == PdfQualificationChallenge.FileName
            && attachment.ByteLength == pdf.Length
            && attachment.ContentSha256 == Convert.ToHexStringLower(SHA256.HashData(pdf));

    public static bool TranscriptMatches(IReadOnlyList<JsonElement> before, IReadOnlyList<JsonElement> after,
        Guid sessionId, IReadOnlyList<string> prompts, IReadOnlyList<string> replies,
        IReadOnlyList<string?> models, string textModel) =>
        models.Count == replies.Count && models.All(model => string.Equals(model, textModel, StringComparison.Ordinal))
            && before.Count <= after.Count
            && LocalOllamaQualificationGuards.SameDurableRows(before, after.Take(before.Count).ToArray())
            && LocalOllamaQualificationGuards.TryReadLocalTranscript(after, sessionId, prompts, replies);

    public static bool IndexMatches(IReadOnlyList<PdfPublishedChunk> chunks, Guid sessionId, Guid attachmentId,
        string hash, int dimensions, PdfQualificationChallenge challenge)
    {
        if (chunks.Count == 0 || chunks.Any(row => row.SessionId != sessionId || row.AttachmentId != attachmentId
            || row.Version != 1 || row.Hash != hash || row.StateHash != hash || row.Mime != "application/pdf"
            || row.Status != "Indexed" || string.IsNullOrWhiteSpace(row.PublishedGeneration)
            || row.PublishedGeneration != row.ChunkGeneration || row.ChunkDimension != dimensions
            || row.VectorDimension != dimensions || row.VectorBytes != checked(dimensions * sizeof(float))
            || !DateTimeOffset.TryParse(row.ExtractedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || !DateTimeOffset.TryParse(row.IndexedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) return false;

        string content = string.Join('\n', chunks.Select(static row => row.Content));

        return content.Contains("[PDF page 1]", StringComparison.Ordinal)
            && challenge.DocumentLines.All(line => content.Contains(line, StringComparison.Ordinal));
    }

    public static bool ProviderTrafficMatches(IReadOnlyList<LongRestRecordedRequest> traffic,
        string textModel, string embeddingModel, Guid attachmentId, PdfQualificationChallenge challenge)
    {
        LongRestRecordedRequest[] chat = traffic.Where(static request => request.Kind == LongRestProviderRequestKind.Chat).ToArray();

        if (chat.Length != 2 || traffic.Any(static request => request.Kind is LongRestProviderRequestKind.Summary or LongRestProviderRequestKind.Adjudication)) return false;

        var first = chat.Where(request => request.UserText.Contains(PdfQualificationChallenge.FirstPrompt, StringComparison.Ordinal)).ToArray();

        var second = chat.Where(request => request.UserText.Contains(PdfQualificationChallenge.SecondPrompt, StringComparison.Ordinal)).ToArray();

        if (first.Length != 1 || second.Length != 1 || first[0].Sequence >= second[0].Sequence
            || chat.Any(request => request.Model != textModel || request.Credential != LongRestRequestCredential.Chat
                || !request.UpstreamCompleted || !request.Delivered || request.StatusCode != 200)) return false;

        string pinFrame = "source-kind: Attachment\nsource-label: evidence\nsource-id: " + attachmentId.ToString("N") + "\nstatus: Current";

        if (!second[0].ProviderVisibleText.Contains(pinFrame, StringComparison.Ordinal)
            || first[0].ProviderVisibleText.Contains(pinFrame, StringComparison.Ordinal)
            || !second[0].ProviderVisibleText.Contains("[PDF page 1]", StringComparison.Ordinal)
            || !challenge.DocumentLines.All(line => second[0].ProviderVisibleText.Contains(line, StringComparison.Ordinal))) return false;

        var embedding = traffic.Where(static request => request.Kind == LongRestProviderRequestKind.Embedding).ToArray();

        return embedding.Length > 0 && embedding.All(request => request.Model == embeddingModel
            && request.Credential == LongRestRequestCredential.Embedding && request.UpstreamCompleted
            && request.Delivered && request.StatusCode == 200)
            && embedding.Any(request => request.ProviderVisibleText.Contains(challenge.FirstSeal, StringComparison.Ordinal)
                && request.ProviderVisibleText.Contains(challenge.SecondSeal, StringComparison.Ordinal));
    }

    public static bool EncryptedPayloadMatches(byte[] payload, byte[] pdf, PdfQualificationChallenge challenge) =>
        payload.AsSpan().StartsWith("ARCABLOB"u8)
            && payload.AsSpan().IndexOf(pdf) < 0
            && payload.AsSpan().IndexOf(Encoding.ASCII.GetBytes(challenge.FirstSeal)) < 0
            && payload.AsSpan().IndexOf(Encoding.ASCII.GetBytes(challenge.SecondSeal)) < 0;

    /// <summary>Fixed read-only query: no caller-controlled SQL or operator installation is consulted.</summary>
    public static Task<IReadOnlyList<PdfPublishedChunk>> ReadPublishedChunksAsync(
        LongRestQualificationGrimoire grimoire, Guid sessionId, Guid attachmentId, CancellationToken cancellationToken) =>
        ReadPublishedChunksAsync(grimoire.DumpAsync, sessionId, attachmentId, cancellationToken);

    internal static async Task<IReadOnlyList<PdfPublishedChunk>> ReadPublishedChunksAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<string[]>>> query,
        Guid sessionId, Guid attachmentId, CancellationToken cancellationToken)
    {
        IReadOnlyList<string[]> rows = await query("""
            SELECT c.SessionId, c.AttachmentId, c.Version, c.ContentSha256, c.MimeType,
                   s.Status, s.ContentSha256, s.PublishedGenerationId, c.GenerationId, c.Content,
                   c.EmbeddingDimension, e.Dim, length(e.Embedding), c.ExtractedAt, c.IndexedAt
              FROM session_attachment_chunks c
              JOIN SessionAttachments a ON a.Id = c.AttachmentId AND lower(a.SessionId) = lower(c.SessionId)
                AND a.Version = c.Version AND a.ContentSha256 = c.ContentSha256
                AND a.MimeType = c.MimeType AND a.State = 'Bound' AND a.EncryptionVersion > 0
              JOIN session_attachment_index_state s ON s.AttachmentId = c.AttachmentId
                AND s.ExtractedAt IS NOT NULL AND s.IndexedAt IS NOT NULL
              JOIN session_attachment_embeddings e ON e.ChunkId = c.ChunkId
             WHERE c.MimeType = 'application/pdf'
             ORDER BY c.SessionId, c.AttachmentId, c.ChunkIndex;
            """, cancellationToken);

        return rows.Where(row => Guid.Parse(row[0]) == sessionId && Guid.Parse(row[1]) == attachmentId)
            .Select(static row => new PdfPublishedChunk(Guid.Parse(row[0]), Guid.Parse(row[1]),
                int.Parse(row[2], CultureInfo.InvariantCulture), row[3], row[4], row[5], row[6], row[7], row[8], row[9],
                int.Parse(row[10], CultureInfo.InvariantCulture), int.Parse(row[11], CultureInfo.InvariantCulture),
                int.Parse(row[12], CultureInfo.InvariantCulture), row[13], row[14])).ToArray();
    }
}
