using System.Net;
using System.Net.Http.Headers;
using System.Text;

using RetroDownfall.Arcanum.Cli.Services.Setup;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Issue #19 — provider validation must distinguish endpoint reachability, TLS/certificate failure,
/// authentication failure, model absence, malformed response, and timeout, must stay inside a strict
/// timeout, and must never spend inference tokens.
/// </summary>
[Collection("OutboundUrlGuardDns")]
public sealed class SetupProviderProbeTests : IDisposable
{

    private const string ModelListJson =
        """{"object":"list","data":[{"id":"gpt-test"},{"id":"other-model"}]}""";

    private readonly IDnsResolver _originalResolver;

    public SetupProviderProbeTests()
    {

        _originalResolver = OutboundUrlGuard.DnsResolver;

        FakeDnsResolver fake = new();

        fake.Add("provider.test", IPAddress.Parse("93.184.216.34"));

        OutboundUrlGuard.DnsResolver = fake;

    }

    public void Dispose() => OutboundUrlGuard.DnsResolver = _originalResolver;

    [Fact]
    public async Task A_reachable_endpoint_that_advertises_the_model_is_reachable()
    {

        SetupConnectivityResult result = await Probe(
            new ScriptedHandler(Json(ModelListJson)),
            model: "gpt-test");

        Assert.Equal(SetupConnectivityStatus.Reachable, result.Status);

        Assert.True(result.SelectedModelAdvertised);

        Assert.Equal(2, result.ModelsAdvertised);

    }

    [Fact]
    public async Task Only_the_non_billable_models_route_is_requested()
    {

        ScriptedHandler handler = new(Json(ModelListJson));

        _ = await Probe(handler, model: "gpt-test");

        Assert.Equal("https://provider.test/v1/models", handler.RequestUri?.ToString());

        Assert.Equal(HttpMethod.Get, handler.Method);

    }

    [Fact]
    public async Task A_supplied_credential_is_sent_as_a_bearer_token_and_never_returned()
    {

        const string Secret = "sk-probe-secret";

        ScriptedHandler handler = new(Json(ModelListJson));

        SetupConnectivityResult result = await Probe(
            handler,
            model: "gpt-test",
            apiKey: Secret);

        Assert.Equal("Bearer", handler.Authorization?.Scheme);

        Assert.Equal(Secret, handler.Authorization?.Parameter);

        Assert.DoesNotContain(Secret, result.Detail, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_keyless_provider_sends_no_authorization_header()
    {

        ScriptedHandler handler = new(Json(ModelListJson));

        _ = await Probe(handler, model: "gpt-test");

        Assert.Null(handler.Authorization);

    }

    [Fact]
    public async Task A_reachable_endpoint_without_the_selected_model_reports_model_absence()
    {

        SetupConnectivityResult result = await Probe(
            new ScriptedHandler(Json(ModelListJson)),
            model: "missing-model");

        Assert.Equal(SetupConnectivityStatus.ModelMissing, result.Status);

        Assert.False(result.SelectedModelAdvertised);

        Assert.Contains("missing-model", result.Detail, StringComparison.Ordinal);

    }

    [Fact]
    public async Task An_endpoint_that_advertises_no_models_stays_reachable_and_says_so()
    {

        SetupConnectivityResult result = await Probe(
            new ScriptedHandler(Json("""{"object":"list","data":[]}""")),
            model: "gpt-test");

        Assert.Equal(SetupConnectivityStatus.Reachable, result.Status);

        Assert.Equal(0, result.ModelsAdvertised);

        Assert.Contains("no models", result.Detail, StringComparison.OrdinalIgnoreCase);

    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Rejected_credentials_report_authentication_failure(HttpStatusCode statusCode)
    {

        SetupConnectivityResult result = await Probe(
            new ScriptedHandler(new HttpResponseMessage(statusCode)),
            model: "gpt-test");

        Assert.Equal(SetupConnectivityStatus.AuthenticationFailed, result.Status);

    }

    [Fact]
    public async Task Other_error_status_codes_report_unreachable_with_the_status()
    {

        SetupConnectivityResult result = await Probe(
            new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.BadGateway)),
            model: "gpt-test");

        Assert.Equal(SetupConnectivityStatus.Unreachable, result.Status);

        Assert.Contains("502", result.Detail, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_successful_response_that_is_not_a_model_list_reports_malformed()
    {

        SetupConnectivityResult result = await Probe(
            new ScriptedHandler(Json("<html>not json</html>")),
            model: "gpt-test");

        Assert.Equal(SetupConnectivityStatus.MalformedResponse, result.Status);

    }

    [Fact]
    public async Task A_tls_failure_is_reported_separately_from_a_connection_failure()
    {

        SetupConnectivityResult result = await Probe(
            new ThrowingHandler(
                new HttpRequestException(
                    HttpRequestError.SecureConnectionError,
                    "certificate not trusted")),
            model: "gpt-test");

        Assert.Equal(SetupConnectivityStatus.TlsFailure, result.Status);

    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    public async Task Dns_and_connection_failures_report_unreachable(HttpRequestError error)
    {

        SetupConnectivityResult result = await Probe(
            new ThrowingHandler(new HttpRequestException(error, "no route")),
            model: "gpt-test");

        Assert.Equal(SetupConnectivityStatus.Unreachable, result.Status);

    }

    [Fact]
    public async Task A_stalled_endpoint_reports_a_timeout_within_the_probe_budget()
    {

        SetupConnectivityResult result = await Probe(
            new ThrowingHandler(new TaskCanceledException("timed out")),
            model: "gpt-test");

        Assert.Equal(SetupConnectivityStatus.Timeout, result.Status);

    }

    [Fact]
    public async Task An_endpoint_rejected_by_the_outbound_guard_is_never_contacted()
    {

        ScriptedHandler handler = new(Json(ModelListJson));

        SetupProviderProbe probe = new(new StubHttpMessageHandlerFactory(handler));

        SetupConnectivityResult result = await probe.ProbeAsync(
            "not-a-url",
            "gpt-test",
            apiKey: null,
            CancellationToken.None);

        Assert.Equal(SetupConnectivityStatus.EndpointRejected, result.Status);

        Assert.Null(handler.RequestUri);

    }

    [Fact]
    public async Task Each_probe_disposes_the_egress_handler_it_was_handed()
    {

        ScriptedHandler handler = new(Json(ModelListJson));

        _ = await Probe(handler, model: "gpt-test");

        Assert.True(handler.Disposed);

    }

    [Fact]
    public void The_probe_timeout_stays_within_the_documented_boundary()
    {

        Assert.Equal(TimeSpan.FromSeconds(5), SetupProviderProbe.ProbeTimeout);

    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1", SetupEndpointClass.Loopback)]
    [InlineData("http://localhost:11434/v1", SetupEndpointClass.Loopback)]
    [InlineData("http://[::1]:11434/v1", SetupEndpointClass.Loopback)]
    [InlineData("http://192.168.1.20:8080/v1", SetupEndpointClass.PrivateNetwork)]
    [InlineData("http://10.0.0.4/v1", SetupEndpointClass.PrivateNetwork)]
    [InlineData("http://172.16.9.9/v1", SetupEndpointClass.PrivateNetwork)]
    [InlineData("https://api.openai.com/v1", SetupEndpointClass.Public)]
    [InlineData("not-a-url", SetupEndpointClass.Unknown)]
    public void Endpoint_class_is_derived_without_disclosing_the_endpoint(
        string endpoint,
        SetupEndpointClass expected)
    {

        Assert.Equal(expected, SetupProviderProbe.ClassifyEndpoint(endpoint));

    }

    private static Task<SetupConnectivityResult> Probe(
        HttpMessageHandler handler,
        string model,
        string? apiKey = null)
    {

        SetupProviderProbe probe = new(new StubHttpMessageHandlerFactory(handler));

        return probe.ProbeAsync(
            "https://provider.test/v1",
            model,
            apiKey,
            CancellationToken.None);

    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {

            Content = new StringContent(body, Encoding.UTF8, "application/json"),

        };

    private sealed class StubHttpMessageHandlerFactory(HttpMessageHandler handler)
        : ISetupProbeHandlerFactory
    {

        public HttpMessageHandler Create() => handler;

    }

    private sealed class ScriptedHandler(HttpResponseMessage response) : HttpMessageHandler
    {

        public Uri? RequestUri { get; private set; }

        public HttpMethod? Method { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            RequestUri = request.RequestUri;

            Method = request.Method;

            Authorization = request.Headers.Authorization;

            return Task.FromResult(response);

        }

        protected override void Dispose(bool disposing)
        {

            Disposed = true;

            base.Dispose(disposing);

        }

    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);

    }

}
