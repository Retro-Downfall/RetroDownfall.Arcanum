using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Packaging;

[Collection("ProcessEnvironment")]
public sealed class LocalPdfQualificationHarnessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configuration_enables_canonical_pdf_dependencies_only_when_selected(bool pdf)
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcanum-pdf-config-{Guid.NewGuid():N}.json");

        try
        {
            PdfQualificationDependencies? dependency = pdf
                ? new(new("qwen3-embedding:8b", 40960, 4096), new Uri("http://127.0.0.1:4333/v1/")) : null;

            await PublishedArcanumOllamaQualificationTests.WriteConfigurationAsync(path,
                Path.GetTempPath(), 4300, new Uri("http://127.0.0.1:11434/v1/"), "flint:40b", visionModel: "qwen3.8:27b", pdf: dependency);

            var file = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), ConfigurationJsonContext.Default.ArcanumConfigurationFile)!;

            Assert.Equal(pdf, SessionAttachmentComprehensionPolicy.IsEnabled(file.Arcanum));

            Assert.Equal(pdf, file.Arcanum.ResolveEmbeddings().Enabled);

            Assert.Equal(pdf, file.Arcanum.Security.AllowedUploadMimeTypes.Contains("application/pdf"));

            if (pdf)
            {
                var provider = Assert.Single(file.Arcanum.Providers, p => p.Name == PdfQualificationDependencies.ProviderName);

                Assert.Equal(PdfQualificationDependencies.CredentialVariable, provider.CredentialEnvironmentVariable);

                Assert.NotEqual(file.Arcanum.Providers[0].CredentialEnvironmentVariable, provider.CredentialEnvironmentVariable);

                Assert.Equal(40960, provider.ContextWindowLimit);

                Assert.Equal("qwen3-embedding:8b", file.Arcanum.ResolveEmbeddings().Model);

                Assert.False(file.Arcanum.Features.Embeddings);

                Assert.True(file.Arcanum.Features.AttachmentRetrieval);

                Assert.True(file.Arcanum.Features.Comprehension);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_pdf_turn_binds_text_role_and_only_the_first_turn_has_an_explicit_attachment(bool followup)
    {
        Guid session = Guid.NewGuid();

        Guid attachment = Guid.NewGuid();

        using RecordingHandler handler = new();

        using HttpClient client = new(handler) { BaseAddress = new Uri("http://127.0.0.1:4333/") };

        string actual = await PublishedArcanumOllamaQualificationTests.RunPdfTurnAsync(client, session, "flint:40b",
            Path.GetTempPath(), followup ? PdfQualificationChallenge.SecondPrompt : PdfQualificationChallenge.FirstPrompt,
            followup ? null : attachment, CancellationToken.None);

        Assert.Equal("pdf-result", actual);

        Assert.Equal("/api/intelligence/ping", handler.Path);

        JsonElement request = handler.Body;

        Assert.Equal("flint:40b", request.GetProperty("model").GetString());

        Assert.Equal(session, request.GetProperty("sessionId").GetGuid());

        Assert.True(request.GetProperty("disableAllTools").GetBoolean());

        Assert.True(request.GetProperty("skipSpellRouting").GetBoolean());

        Assert.Equal(followup ? PdfQualificationChallenge.SecondPrompt : PdfQualificationChallenge.FirstPrompt,
            request.GetProperty("prompt").GetString());

        if (followup) Assert.True(!request.TryGetProperty("attachmentReferences", out var refs) || refs.ValueKind == JsonValueKind.Null);
        else Assert.Equal(attachment, Assert.Single(request.GetProperty("attachmentReferences").EnumerateArray()).GetGuid());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void Pdf_mode_is_explicit_and_defaults_reuse_the_existing_embedding_qualification(string? mode, bool enabled)
    {
        var options = PdfQualificationOptions.Read(name => name == PdfQualificationOptions.ModeVariable ? mode : null);

        Assert.Equal(enabled, options is not null);

        if (options is not null)
        {
            Assert.Equal(LongRestQualificationOptions.DefaultEmbeddingModel, options.Model);

            Assert.Equal(LongRestQualificationOptions.DefaultEmbeddingWindow, options.Window);

            Assert.Equal(LongRestQualificationOptions.DefaultEmbeddingDimensions, options.Dimensions);
        }

        Assert.Throws<ArgumentException>(() => PdfQualificationOptions.Read(_ => "yes"));
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("invalid")]
    public void Advertised_embedding_window_requires_unambiguous_actual_embedding_capability(string defect)
    {
        using var valid = JsonDocument.Parse("{\"capabilities\":[\"embedding\"],\"model_info\":{\"qwen.context_length\":40960}}");

        Assert.Equal(40960, PdfQualificationPreflight.AdvertisedWindow(valid.RootElement));

        string wrong = defect switch
        {
            "capability" => "{\"capabilities\":[\"completion\"],\"model_info\":{\"qwen.context_length\":40960}}",
            "missing" => "{\"capabilities\":[\"embedding\"],\"model_info\":{}}",
            "ambiguous" => "{\"capabilities\":[\"embedding\"],\"model_info\":{\"a.context_length\":40960,\"b.context_length\":2048}}",
            _ => "{\"capabilities\":[\"embedding\"],\"model_info\":{\"qwen.context_length\":0}}",
        };

        using var refused = JsonDocument.Parse(wrong);

        Assert.Null(PdfQualificationPreflight.AdvertisedWindow(refused.RootElement));
    }

    [Theory]
    [InlineData("length", "null")]
    [InlineData("stop", "[{\"name\":\"unexpected\"}]")]
    public async Task Canonical_pdf_turn_rejects_incomplete_or_tool_bearing_provider_outcomes(string finish, string tools)
    {
        using RecordingHandler handler = new()
        {
            ResponseBody = "{\"isSuccess\":true,\"data\":{\"text\":\"pdf-result\",\"finishReason\":\"" + finish + "\",\"toolCalls\":" + tools + "}}",
        };

        using HttpClient client = new(handler) { BaseAddress = new Uri("http://127.0.0.1:4333/") };

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => PublishedArcanumOllamaQualificationTests.RunPdfTurnAsync(
            client, Guid.NewGuid(), "flint:40b", Path.GetTempPath(), PdfQualificationChallenge.FirstPrompt,
            Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public void Pdf_model_client_uses_the_linked_turn_budget_and_retains_only_isolated_control_authentication()
    {
        using HttpClient control = new() { BaseAddress = new Uri("http://127.0.0.1:4333/"), Timeout = TimeSpan.FromSeconds(45) };

        control.DefaultRequestHeaders.Add("X-Test-Auth", "isolated-test");

        using HttpClient turns = PublishedArcanumOllamaQualificationTests.CreatePdfTurnClient(control);

        Assert.Equal(Timeout.InfiniteTimeSpan, turns.Timeout);

        Assert.Equal(control.BaseAddress, turns.BaseAddress);

        Assert.Equal("isolated-test", Assert.Single(turns.DefaultRequestHeaders.GetValues("X-Test-Auth")));

        Assert.Equal(TimeSpan.FromSeconds(45), control.Timeout);
    }

    [Fact]
    public async Task Canonical_pdf_turn_observes_caller_cancellation_while_the_fake_provider_is_in_flight()
    {
        using CancellationTokenSource caller = new();

        using RecordingHandler handler = new() { Block = true };

        using HttpClient client = new(handler) { BaseAddress = new Uri("http://127.0.0.1:4333/"), Timeout = Timeout.InfiniteTimeSpan };

        Task<string> running = PublishedArcanumOllamaQualificationTests.RunPdfTurnAsync(client, Guid.NewGuid(),
            "flint:40b", Path.GetTempPath(), PdfQualificationChallenge.FirstPrompt, Guid.NewGuid(), caller.Token);

        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(running.IsCanceled);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool Block { get; init; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ResponseBody { get; init; } = "{\"isSuccess\":true,\"data\":{\"text\":\"pdf-result\",\"finishReason\":\"stop\",\"toolCalls\":null}}";

        public string? Path { get; private set; }

        public JsonElement Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;

            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));

            Body = document.RootElement.Clone();

            Entered.TrySetResult();

            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
            };

            response.Headers.TryAddWithoutValidation("Cache-Control", "no-store, private");

            response.Headers.TryAddWithoutValidation("Pragma", "no-cache");

            response.Content.Headers.TryAddWithoutValidation("Expires", "0");

            return response;
        }
    }
}
