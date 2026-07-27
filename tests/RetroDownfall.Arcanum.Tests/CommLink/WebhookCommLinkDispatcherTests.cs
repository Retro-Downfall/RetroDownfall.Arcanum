using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.CommLink;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.CommLink;

[Collection("OutboundUrlGuardDns")]
public sealed class WebhookCommLinkDispatcherTests : IDisposable
{

    private const string PublicWebhookUrl = "https://example.com/hooks/arcanum";
    private const string WebhookEnvironmentVariable = "ARCANUM_TEST_COMMLINK_WEBHOOK_URL";

    private readonly IDnsResolver _originalResolver;
    private readonly string? _originalDefaultWebhookUrl;
    private readonly string? _originalWebhookUrl;

    public WebhookCommLinkDispatcherTests()
    {

        _originalResolver = OutboundUrlGuard.DnsResolver;
        _originalDefaultWebhookUrl = System.Environment.GetEnvironmentVariable(
            EnvironmentCredentialResolver.DefaultCommLinkWebhookUrlEnvironmentVariable);
        _originalWebhookUrl = System.Environment.GetEnvironmentVariable(
            WebhookEnvironmentVariable);
        System.Environment.SetEnvironmentVariable(
            EnvironmentCredentialResolver.DefaultCommLinkWebhookUrlEnvironmentVariable,
            null);
        System.Environment.SetEnvironmentVariable(WebhookEnvironmentVariable, null);

        FakeDnsResolver fake = new();

        fake.Add("example.com", IPAddress.Parse("93.184.216.34"));
        fake.Add("127.0.0.1", IPAddress.Parse("127.0.0.1"));
        fake.Add("localhost", IPAddress.Parse("127.0.0.1"));

        OutboundUrlGuard.DnsResolver = fake;

    }

    public void Dispose()
    {

        OutboundUrlGuard.DnsResolver = _originalResolver;
        System.Environment.SetEnvironmentVariable(
            EnvironmentCredentialResolver.DefaultCommLinkWebhookUrlEnvironmentVariable,
            _originalDefaultWebhookUrl);
        System.Environment.SetEnvironmentVariable(
            WebhookEnvironmentVariable,
            _originalWebhookUrl);

    }

    [Fact]
    public async Task DispatchAsync_missing_webhook_url_returns_suppressed_without_http()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        WebhookCommLinkDispatcher dispatcher = CreateDispatcher(handler, new ArcanumSettings());

        CommLinkMessage message = new("title", "body", CommLinkSeverity.Info, "test");

        Result<CommLinkDeliveryResult> result = await dispatcher.DispatchAsync(message);

        Assert.True(result.IsSuccess);

        Assert.Equal(CommLinkDeliveryStatus.Suppressed, result.Value.Status);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task DispatchAsync_invalid_webhook_url_returns_suppressed_without_http()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ArcanumSettings settings = SettingsWithWebhook("not-a-valid-uri");

        WebhookCommLinkDispatcher dispatcher = CreateDispatcher(handler, settings);

        Result<CommLinkDeliveryResult> result =
            await dispatcher.DispatchAsync(new CommLinkMessage("t", "b", CommLinkSeverity.Warning, "src"));

        Assert.True(result.IsSuccess);

        Assert.Equal(CommLinkDeliveryStatus.Suppressed, result.Value.Status);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task DispatchAsync_disallowed_scheme_returns_suppressed_without_http()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ArcanumSettings settings = SettingsWithWebhook(
            "ftp://example.com/hook",
            allowedSchemes: ["https"]);

        WebhookCommLinkDispatcher dispatcher = CreateDispatcher(handler, settings);

        Result<CommLinkDeliveryResult> result =
            await dispatcher.DispatchAsync(new CommLinkMessage("t", "b", CommLinkSeverity.Critical, "src"));

        Assert.True(result.IsSuccess);

        Assert.Equal(CommLinkDeliveryStatus.Suppressed, result.Value.Status);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task DispatchAsync_loopback_webhook_rejected_by_outbound_policy_returns_suppressed_without_http()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ArcanumSettings settings = SettingsWithWebhook("https://127.0.0.1/hook");

        WebhookCommLinkDispatcher dispatcher = CreateDispatcher(handler, settings);

        Result<CommLinkDeliveryResult> result =
            await dispatcher.DispatchAsync(new CommLinkMessage("t", "b", CommLinkSeverity.Info, "src"));

        Assert.True(result.IsSuccess);

        Assert.Equal(CommLinkDeliveryStatus.Suppressed, result.Value.Status);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task DispatchAsync_http_allowed_when_explicitly_opted_in()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ArcanumSettings settings = SettingsWithWebhook(
            "http://example.com/hook",
            allowedSchemes: ["https", "http"]);

        WebhookCommLinkDispatcher dispatcher = CreateDispatcher(handler, settings);

        Result<CommLinkDeliveryResult> result =
            await dispatcher.DispatchAsync(new CommLinkMessage("t", "b", CommLinkSeverity.Info, "src"));

        Assert.True(result.IsSuccess);

        Assert.Equal(CommLinkDeliveryStatus.Delivered, result.Value.Status);

        Assert.Single(handler.Requests);

    }

    [Fact]
    public async Task DispatchAsync_host_not_in_allowed_hosts_returns_suppressed_without_http()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ArcanumSettings settings = SettingsWithWebhook(
            PublicWebhookUrl,
            allowedHosts: ["hooks.example.com"]);

        WebhookCommLinkDispatcher dispatcher = CreateDispatcher(handler, settings);

        Result<CommLinkDeliveryResult> result =
            await dispatcher.DispatchAsync(new CommLinkMessage("t", "b", CommLinkSeverity.Info, "src"));

        Assert.True(result.IsSuccess);

        Assert.Equal(CommLinkDeliveryStatus.Suppressed, result.Value.Status);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task DispatchAsync_success_posts_json_payload_to_webhook()
    {

        string? capturedJson = null;

        RecordingHttpHandler handler = new(async request =>
        {

            capturedJson = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK);

        });

        ArcanumSettings settings = SettingsWithWebhook(PublicWebhookUrl);

        WebhookCommLinkDispatcher dispatcher = CreateDispatcher(handler, settings);

        CommLinkMessage message = new("Alert", "Details", CommLinkSeverity.Warning, "unit-test");

        Result<CommLinkDeliveryResult> result = await dispatcher.DispatchAsync(message);

        Assert.True(result.IsSuccess);

        Assert.Equal(CommLinkDeliveryStatus.Delivered, result.Value.Status);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal(new Uri(PublicWebhookUrl), request.RequestUri);

        Assert.NotNull(capturedJson);

        using JsonDocument doc = JsonDocument.Parse(capturedJson!);

        Assert.Equal("Alert", doc.RootElement.GetProperty("title").GetString());

        Assert.Equal("Details", doc.RootElement.GetProperty("body").GetString());

        Assert.Equal("Warning", doc.RootElement.GetProperty("severity").GetString());

        Assert.Equal("unit-test", doc.RootElement.GetProperty("source").GetString());

        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("timestampUtc").GetString()));

    }

    [Fact]
    public async Task DispatchAsync_http_error_returns_failure()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            ReasonPhrase = PublicWebhookUrl,
        }));

        ArcanumSettings settings = SettingsWithWebhook(PublicWebhookUrl);

        WebhookCommLinkDispatcher dispatcher = CreateDispatcher(handler, settings);

        Result<CommLinkDeliveryResult> result =
            await dispatcher.DispatchAsync(new CommLinkMessage("t", "b", CommLinkSeverity.Info, "src"));

        Assert.True(result.IsFailure);

        Assert.Equal("CommLink.WebhookHttpError", result.Error.Code);

        Assert.Contains("502", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicWebhookUrl, result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task DispatchAsync_handler_exception_returns_failure()
    {

        RecordingHttpHandler handler = new(_ =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException($"network down at {PublicWebhookUrl}")));
        RecordingLogger logger = new();

        ArcanumSettings settings = SettingsWithWebhook(PublicWebhookUrl);

        WebhookCommLinkDispatcher dispatcher = CreateDispatcher(handler, settings, logger);

        Result<CommLinkDeliveryResult> result =
            await dispatcher.DispatchAsync(new CommLinkMessage("t", "b", CommLinkSeverity.Info, "src"));

        Assert.True(result.IsFailure);

        Assert.Equal("CommLink.WebhookException", result.Error.Code);
        Assert.DoesNotContain(PublicWebhookUrl, result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains(PublicWebhookUrl, StringComparison.Ordinal));

    }

    private static ArcanumSettings SettingsWithWebhook(
        string webhookUrl,
        string[]? allowedSchemes = null,
        string[]? allowedHosts = null)
    {
        System.Environment.SetEnvironmentVariable(
            WebhookEnvironmentVariable,
            webhookUrl);

        return new ArcanumSettings
        {
            Integrations = new IntegrationSettings
            {
                CommLink = new CommLinkIntegrationSettings
                {
                    WebhookUrlEnvironmentVariable = WebhookEnvironmentVariable,
                    AllowedSchemes = allowedSchemes ?? ["https"],
                    AllowedHosts = allowedHosts ?? [],
                },
            },
        };
    }

    private static WebhookCommLinkDispatcher CreateDispatcher(
        RecordingHttpHandler handler,
        ArcanumSettings settings,
        ILogger<WebhookCommLinkDispatcher>? logger = null)
    {

        FakeHttpClientFactory factory = new(handler);

        TestOptionsMonitor<ArcanumSettings> monitor = new(settings);

        return new WebhookCommLinkDispatcher(
            factory,
            monitor,
            logger ?? NullLogger<WebhookCommLinkDispatcher>.Instance);

    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);

    }

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            Requests.Add(request);

            return responder(request);

        }

    }

    private sealed class RecordingLogger : ILogger<WebhookCommLinkDispatcher>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

}
