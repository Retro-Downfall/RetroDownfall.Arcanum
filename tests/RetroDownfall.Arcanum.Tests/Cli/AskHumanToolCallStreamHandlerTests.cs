using System.Net;
using System.Net.Http.Headers;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class AskHumanToolCallStreamHandlerTests
{

    private static ArcanumApiClient CreateApiClient(
        Func<string, string, CancellationToken, bool>? submitValidator = null,
        bool success = true,
        string? errorCode = null,
        string? errorMessage = null)
    {

        DelegatingHandler handler = new SubmitHandler(submitValidator, success, errorCode, errorMessage);

        IHttpClientFactory factory = new HttpClientFactoryStub(handler);

        ISecretStore secretStore = new SecretStoreStub();

        return new ArcanumApiClient(factory, secretStore);

    }

    private sealed class HttpClientFactoryStub(DelegatingHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name)
        {

            HttpClient client = new(handler)
            {

                BaseAddress = new Uri("http://localhost:5000/")

            };

            return client;

        }

    }

    private sealed class SecretStoreStub : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(Guid.NewGuid().ToString("N"));

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => Task.FromResult(SecretStoreReadResult.Ok(Guid.NewGuid().ToString("N")));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class SubmitHandler : DelegatingHandler
    {

        private readonly Func<string, string, CancellationToken, bool>? _submitValidator;

        private readonly bool _success;

        private readonly string? _errorCode;

        private readonly string? _errorMessage;

        public SubmitHandler(
            Func<string, string, CancellationToken, bool>? submitValidator,
            bool success,
            string? errorCode,
            string? errorMessage)
        {

            _submitValidator = submitValidator;

            _success = success;

            _errorCode = errorCode;

            _errorMessage = errorMessage;

        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {

            if (request.RequestUri?.OriginalString?.EndsWith("api/intelligence/human-response", StringComparison.Ordinal) != true)
            {

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            }

            string? promptId = null;

            string? answer = null;

            if (request.Content is not null)
            {

                byte[] body = request.Content.ReadAsByteArrayAsync(cancellationToken).Result;

                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);

                promptId = doc.RootElement.GetProperty("promptId").GetString();

                answer = doc.RootElement.GetProperty("answer").GetString();

            }

            bool allowed = _submitValidator?.Invoke(promptId ?? string.Empty, answer ?? string.Empty, cancellationToken) ?? true;

            HttpResponseMessage response = new(HttpStatusCode.OK);

            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            string json = _success && allowed
                ? "{\"isSuccess\":true,\"data\":true}"
                : $"{{\"isSuccess\":false,\"error\":{{\"code\":\"{_errorCode ?? "Api.Error"}\",\"message\":\"{_errorMessage ?? "failed"}\"}}}}";

            response.Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(json));

            return Task.FromResult(response);

        }

    }

    private sealed class FakePalette : IThemePalette
    {

        public Color Text { get; } = Color.White;

        public Color Heading { get; } = Color.White;

        public Color Highlight { get; } = Color.White;

        public Color Error { get; } = Color.Red;

        public Color Muted { get; } = Color.Grey;

    }

    [Fact]
    public async Task TryHandleAskHumanAsync_NonToolCallEvent_ReturnsNotHandled()
    {

        IntelligenceEvent evt = new(IntelligenceEventType.Status, "ask_human", "{\"question\":\"q\",\"promptId\":\"p\"}");

        AskHumanResult result = await AskHumanToolCallStreamHandler.TryHandleAskHumanAsync(
            evt,
            unattended: false,
            isInteractive: true,
            CreateApiClient(),
            new FakePalette(),
            CancellationToken.None);

        Assert.Equal(AskHumanResult.NotHandled, result);

    }

    [Fact]
    public async Task TryHandleAskHumanAsync_WrongToolName_ReturnsNotHandled()
    {

        IntelligenceEvent evt = new(IntelligenceEventType.ToolCall, "other_tool", "{\"question\":\"q\",\"promptId\":\"p\"}");

        AskHumanResult result = await AskHumanToolCallStreamHandler.TryHandleAskHumanAsync(
            evt,
            unattended: false,
            isInteractive: true,
            CreateApiClient(),
            new FakePalette(),
            CancellationToken.None);

        Assert.Equal(AskHumanResult.NotHandled, result);

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"question\":\"q\",\"promptId\":\"p\"")]
    public async Task TryHandleAskHumanAsync_InvalidData_ReturnsNotHandled(string? data)
    {

        IntelligenceEvent evt = new(IntelligenceEventType.ToolCall, "ask_human", data);

        AskHumanResult result = await AskHumanToolCallStreamHandler.TryHandleAskHumanAsync(
            evt,
            unattended: false,
            isInteractive: true,
            CreateApiClient(),
            new FakePalette(),
            CancellationToken.None);

        Assert.Equal(AskHumanResult.NotHandled, result);

    }

    [Fact]
    public async Task TryHandleAskHumanAsync_UnattendedMode_SubmitsAutoReply()
    {

        string? receivedPromptId = null;

        string? receivedAnswer = null;

        IntelligenceEvent evt = new(
            IntelligenceEventType.ToolCall,
            "ask_human",
            "{\"question\":\"Proceed?\",\"promptId\":\"prompt-42\"}");

        ArcanumApiClient client = CreateApiClient((promptId, answer, _) =>
        {

            receivedPromptId = promptId;

            receivedAnswer = answer;

            return true;

        });

        AskHumanResult result = await AskHumanToolCallStreamHandler.TryHandleAskHumanAsync(
            evt,
            unattended: true,
            isInteractive: true,
            client,
            new FakePalette(),
            CancellationToken.None);

        Assert.Equal(AskHumanResult.Handled, result);

        Assert.Equal("prompt-42", receivedPromptId);

        Assert.Contains("unattended mode", receivedAnswer, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task TryHandleAskHumanAsync_NonInteractiveMode_SubmitsAutoReply()
    {

        string? receivedAnswer = null;

        IntelligenceEvent evt = new(
            IntelligenceEventType.ToolCall,
            "ask_human",
            "{\"question\":\"Proceed?\",\"promptId\":\"prompt-42\"}");

        ArcanumApiClient client = CreateApiClient((_, answer, _) =>
        {

            receivedAnswer = answer;

            return true;

        });

        AskHumanResult result = await AskHumanToolCallStreamHandler.TryHandleAskHumanAsync(
            evt,
            unattended: false,
            isInteractive: false,
            client,
            new FakePalette(),
            CancellationToken.None);

        Assert.Equal(AskHumanResult.Handled, result);

        Assert.Contains("No interactive terminal", receivedAnswer, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task TryHandleAskHumanAsync_ApiSubmissionFails_ReturnsSubmitFailed()
    {

        IntelligenceEvent evt = new(
            IntelligenceEventType.ToolCall,
            "ask_human",
            "{\"question\":\"Proceed?\",\"promptId\":\"prompt-42\"}");

        ArcanumApiClient client = CreateApiClient(
            success: false,
            errorCode: "Api.SubmitFailed",
            errorMessage: "Submit failed");

        AskHumanResult result = await AskHumanToolCallStreamHandler.TryHandleAskHumanAsync(
            evt,
            unattended: true,
            isInteractive: true,
            client,
            new FakePalette(),
            CancellationToken.None);

        Assert.Equal(AskHumanResult.SubmitFailed, result);

    }

    [Fact]
    public async Task TryHandleAskHumanAsync_CancellationRequested_PropagatesCancel()
    {

        IntelligenceEvent evt = new(
            IntelligenceEventType.ToolCall,
            "ask_human",
            "{\"question\":\"Proceed?\",\"promptId\":\"prompt-42\"}");

        using CancellationTokenSource cts = new();

        cts.Cancel();

        ArcanumApiClient client = CreateApiClient((_, _, ct) =>
        {

            cts.Token.ThrowIfCancellationRequested();

            return true;

        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AskHumanToolCallStreamHandler.TryHandleAskHumanAsync(
                evt,
                unattended: true,
                isInteractive: true,
                client,
                new FakePalette(),
                cts.Token));

    }

}
