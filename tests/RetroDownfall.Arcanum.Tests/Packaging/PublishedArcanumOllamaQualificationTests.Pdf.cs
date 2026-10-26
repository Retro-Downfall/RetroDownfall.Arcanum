using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed partial class PublishedArcanumOllamaQualificationTests
{
    internal static async Task<string> RunPdfTurnAsync(HttpClient api, Guid sessionId, string model,
        string workspace, string prompt, Guid? attachmentId, CancellationToken cancellationToken)
    {
        using CancellationTokenSource turnDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        turnDeadline.CancelAfter(TurnBudget);

        cancellationToken = turnDeadline.Token;

        PingRequest request = new(prompt, Model: model, WorkingDirectory: workspace, SessionId: sessionId,
            DisableMcpTools: true, UnattendedMode: true, SkipSpellRouting: true, Temperature: 0,
            MaxOutputTokens: 512, AttachmentReferences: attachmentId is { } id ? [id] : null,
            DisableAllTools: true);

        using HttpResponseMessage response = await api.PostAsync("api/intelligence/ping",
            JsonContent.Create(request, ArcanumJsonContext.Default.PingRequest), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertRecallProtectedHeaders(response);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.True(document.RootElement.GetProperty("isSuccess").GetBoolean());

        JsonElement data = document.RootElement.GetProperty("data");

        Assert.Equal("stop", data.GetProperty("finishReason").GetString());

        Assert.True(!data.TryGetProperty("toolCalls", out JsonElement tools) || tools.ValueKind == JsonValueKind.Null
            || tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() == 0);

        return data.GetProperty("text").GetString()!;
    }

    internal static HttpClient CreatePdfTurnClient(HttpClient control)
    {
        HttpClient client = new(LocalOllamaQualificationGuards.CreateLoopbackOnlyHttpHandler())
        {
            BaseAddress = control.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        foreach (var header in control.DefaultRequestHeaders) client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);

        return client;
    }

    private static async Task RunPdfQualificationAsync(HttpClient api, string configurationDirectory,
        string workspace, string textModel, PdfQualificationOptions options, LongRestQualificationProxy proxy, Guid originalSessionId)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(10));

        CancellationToken token = deadline.Token;

        using HttpClient pdfApi = CreatePdfTurnClient(api);

        JsonElement[] originalRows = await ReadRecallRowsAsync(api, originalSessionId, token);

        PdfQualificationChallenge challenge = PdfQualificationChallenge.Create();

        byte[] pdf = challenge.BuildPdf();

        string hash = ComputeSha256(pdf);

        string configuration = await File.ReadAllTextAsync(Path.Combine(configurationDirectory, "arcanum.json"), token);

        foreach (string surface in new[] { PdfQualificationChallenge.Metadata, PdfQualificationChallenge.FirstPrompt,
            PdfQualificationChallenge.SecondPrompt, configuration, textModel, options.Model })
        {
            Assert.True(!surface.Contains(challenge.FirstSeal, StringComparison.Ordinal)
                && !surface.Contains(challenge.SecondSeal, StringComparison.Ordinal), "A private PDF fact leaked into a non-PDF input surface.");
        }

        Assert.DoesNotContain(challenge.InspectionSeal, challenge.FirstExpected, StringComparison.Ordinal);

        using HttpResponseMessage created = await api.PostAsync("api/sessions",
            JsonContent.Create(new CreateSessionRequest(null, "PDF qualification"), ArcanumJsonContext.Default.CreateSessionRequest), token);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using JsonDocument session = JsonDocument.Parse(await created.Content.ReadAsStringAsync(token));

        Guid sessionId = session.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        Assert.NotEqual(originalSessionId, sessionId);

        int beforeUpload = proxy.Position;

        using MultipartFormDataContent multipart = new();

        using ByteArrayContent file = new(pdf);

        file.Headers.ContentType = new("application/pdf");

        multipart.Add(file, "file", PdfQualificationChallenge.FileName);

        multipart.Add(new StringContent(PdfQualificationChallenge.LogicalName), "logicalName");

        using HttpResponseMessage uploaded = await api.PostAsync($"api/sessions/{sessionId:D}/attachments", multipart, token);

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        using JsonDocument upload = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync(token));

        Assert.True(upload.RootElement.GetProperty("isSuccess").GetBoolean());

        SessionAttachmentDto attachment = upload.RootElement.GetProperty("data").Deserialize(ArcanumJsonContext.Default.SessionAttachmentDto)!;

        Assert.True(PdfQualificationEvidence.AttachmentMatches(attachment, sessionId, pdf));

        using HttpResponseMessage exported = await api.GetAsync($"api/sessions/{sessionId:D}/attachments/{attachment.Id:D}/content", token);

        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);

        Assert.Equal("application/pdf", exported.Content.Headers.ContentType?.MediaType);

        byte[] retained = await exported.Content.ReadAsByteArrayAsync(token);

        Assert.True(pdf.AsSpan().SequenceEqual(retained), "Exported PDF bytes must match the original upload exactly.");

        Assert.Equal(hash, ComputeSha256(retained));

        string root = Path.GetFullPath(Path.Combine(configurationDirectory, "attachments")) + Path.DirectorySeparatorChar;

        string payloadPath = Path.GetFullPath(Path.Combine(root, attachment.RelativePath));

        Assert.StartsWith(root, payloadPath, StringComparison.Ordinal);

        byte[] encrypted = await File.ReadAllBytesAsync(payloadPath, token);

        try
        {
            Assert.True(PdfQualificationEvidence.EncryptedPayloadMatches(encrypted, pdf, challenge));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }

        LongRestQualificationGrimoire grimoire = await LongRestQualificationGrimoire.OpenAsync(configurationDirectory, token);

        Assert.Empty(await ReadRecallRowsAsync(api, sessionId, token));

        string first = await RunPdfTurnAsync(pdfApi, sessionId, textModel, workspace,
            PdfQualificationChallenge.FirstPrompt, attachment.Id, token);

        Assert.True(challenge.MatchesFirstAnswer(first), "The PDF turn did not compute the exact winning seal and net quantity.");

        JsonElement[] firstRows = await ReadRecallRowsAsync(api, sessionId, token);

        Assert.True(PdfQualificationEvidence.TranscriptMatches([], firstRows, sessionId,
            [PdfQualificationChallenge.FirstPrompt], [first], await grimoire.ReadReplyModelsAsync(sessionId, token), textModel));

        using HttpResponseMessage pinned = await api.PostAsync($"api/sessions/{sessionId:D}/context-pins",
            JsonContent.Create(new CreateSessionContextPinRequest(SessionContextPinKind.Attachment,
                attachment.Id.ToString("D"), PdfQualificationChallenge.LogicalName, hash),
                ArcanumJsonContext.Default.CreateSessionContextPinRequest), token);

        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);

        using JsonDocument pin = JsonDocument.Parse(await pinned.Content.ReadAsStringAsync(token));

        Assert.True(pin.RootElement.GetProperty("isSuccess").GetBoolean());

        Assert.Equal(sessionId, pin.RootElement.GetProperty("data").GetProperty("sessionId").GetGuid());

        Assert.Equal(attachment.Id.ToString("D"), pin.RootElement.GetProperty("data").GetProperty("targetIdentifier").GetString());

        Assert.Equal(hash, pin.RootElement.GetProperty("data").GetProperty("contentVersion").GetString());

        using HttpResponseMessage pins = await api.GetAsync($"api/sessions/{sessionId:D}/context-pins", token);

        Assert.Equal(HttpStatusCode.OK, pins.StatusCode);

        using JsonDocument persistedPins = JsonDocument.Parse(await pins.Content.ReadAsStringAsync(token));

        JsonElement persistedPin = Assert.Single(persistedPins.RootElement.GetProperty("data").EnumerateArray());

        Assert.Equal(attachment.Id.ToString("D"), persistedPin.GetProperty("targetIdentifier").GetString());

        Assert.Equal(hash, persistedPin.GetProperty("contentVersion").GetString());

        string second = await RunPdfTurnAsync(pdfApi, sessionId, textModel, workspace,
            PdfQualificationChallenge.SecondPrompt, attachmentId: null, token);

        Assert.True(challenge.MatchesSecondAnswer(second), "The pinned PDF turn did not extract the distinct secondary seal.");

        Assert.True(PdfQualificationEvidence.TranscriptMatches(firstRows,
            await ReadRecallRowsAsync(api, sessionId, token), sessionId,
            [PdfQualificationChallenge.FirstPrompt, PdfQualificationChallenge.SecondPrompt], [first, second],
            await grimoire.ReadReplyModelsAsync(sessionId, token), textModel));

        using CancellationTokenSource indexDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);

        indexDeadline.CancelAfter(TimeSpan.FromMinutes(3));

        while (!PdfQualificationEvidence.IndexMatches(
            await PdfQualificationEvidence.ReadPublishedChunksAsync(grimoire, sessionId, attachment.Id, indexDeadline.Token),
            sessionId, attachment.Id, hash, options.Dimensions, challenge))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), indexDeadline.Token);
        }

        IReadOnlyList<LongRestRecordedRequest> traffic = proxy.Since(beforeUpload);

        Assert.True(PdfQualificationEvidence.ProviderTrafficMatches(traffic, textModel, options.Model, attachment.Id, challenge),
            "The PDF phase needs exactly two successful text-role calls, current pin source materialization and successful PDF-source embedding traffic under its separate credential.");

        int embeddingCount = traffic.Count(static request => request.Kind == LongRestProviderRequestKind.Embedding);

        Assert.True(LocalOllamaQualificationGuards.SameDurableRows(originalRows,
            await ReadRecallRowsAsync(api, originalSessionId, token)));

        Console.WriteLine($"PDF dependency evidence: text model={textModel}, Chat credential, calls=2; embedding model={options.Model}, Embedding credential, calls={embeddingCount}. This is functional PDF evidence; dedicated embedding qualification remains separate and last.");
    }
}
