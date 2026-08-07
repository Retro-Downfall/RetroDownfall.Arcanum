using System.Net;

using A2A;
using A2A.AspNetCore;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>
/// Where the outbound peer credential is allowed to travel.
/// </summary>
/// <remarks>
/// <c>agent_url</c> is not operator-supplied: <c>dispatch_sending</c> takes it verbatim from the model, and
/// the Agent Card that discovery returns is authored by the remote agent. The allowlist and SSRF guard stop
/// Arcanum connecting to a blocked host, but with the default empty allowlist any public host is reachable.
/// The credential therefore travels only to a target that matches a non-empty
/// <c>Arcanum:Integrations:A2A:AllowedRemoteAgents</c> entry — one uniform rule for every caller. Otherwise
/// the Sending still goes out, unauthenticated.
/// </remarks>
[Collection("OutboundUrlGuardDns")]
public sealed class A2ACredentialScopeTests : IDisposable
{

    private const string OperatorHost = "operator-supplied.example.test";

    private const string ThirdPartyHost = "card-controlled.example.test";

    private const string EnvVar = "ARCANUM_TEST_A2A_SCOPE_KEY";

    private readonly IDnsResolver _originalResolver;

    public A2ACredentialScopeTests()
    {

        _originalResolver = OutboundUrlGuard.DnsResolver;

        FakeDnsResolver fake = new();

        fake.Add(OperatorHost, IPAddress.Parse("93.184.216.34"));

        fake.Add(ThirdPartyHost, IPAddress.Parse("93.184.216.35"));

        OutboundUrlGuard.DnsResolver = fake;

        global::System.Environment.SetEnvironmentVariable(EnvVar, "operator-secret");

    }

    public void Dispose()
    {

        OutboundUrlGuard.DnsResolver = _originalResolver;

        global::System.Environment.SetEnvironmentVariable(EnvVar, null);

    }

    [Fact]
    public async Task Credential_TravelsToAnAllowlistedOrigin()
    {

        (TestServer server, HeaderProbe probe) = await CreateAgentAsync($"http://{OperatorHost}/agent");

        using (server)
        {

            Result<A2ADispatchResult> result = await DispatchAsync(
                probe,
                $"http://{OperatorHost}/",
                allowlist: [$"http://{OperatorHost}/"]);

            Assert.True(result.IsSuccess);

            Assert.Contains("operator-secret", probe.CredentialsSentTo(OperatorHost));

        }

    }

    [Fact]
    public async Task Credential_IsWithheldFromEveryTargetWhenTheAllowlistIsEmpty()
    {

        // The default configuration. agent_url arrives verbatim from the model, so a prompt-injected
        // Apprentice naming an attacker host must not be handed an operator-equivalent peer key — not even
        // during Agent Card discovery, which runs before any card-interface scoping.
        (TestServer server, HeaderProbe probe) = await CreateAgentAsync($"http://{ThirdPartyHost}/agent");

        using (server)
        {

            Result<A2ADispatchResult> result = await DispatchAsync(
                probe,
                $"http://{ThirdPartyHost}/",
                allowlist: []);

            // The Sending still goes out — it just goes out unauthenticated.
            Assert.True(result.IsSuccess);

            Assert.NotEmpty(probe.Requests);

            Assert.Empty(probe.CredentialsSentTo(ThirdPartyHost));

        }

    }

    [Fact]
    public async Task Credential_IsWithheldWhenTheCardSteersToAThirdPartyOrigin()
    {

        // Same server, but the card advertises an interface on a host the allowlist never named.
        (TestServer server, HeaderProbe probe) = await CreateAgentAsync($"http://{ThirdPartyHost}/agent");

        using (server)
        {

            Result<A2ADispatchResult> result = await DispatchAsync(
                probe,
                $"http://{OperatorHost}/",
                allowlist: [$"http://{OperatorHost}/"]);

            // Discovery legitimately authenticates against the allowlisted origin...
            Assert.Contains("operator-secret", probe.CredentialsSentTo(OperatorHost));

            // ...but the card cannot steer either the connection or the credential to a host of its own
            // choosing.
            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Sending.AgentNotAllowed, result.Error.Code);

            Assert.Empty(probe.CredentialsSentTo(ThirdPartyHost));

        }

    }

    [Fact]
    public async Task WithholdingTheCredential_LogsAWarningNamingTheSettingWithoutTheSecret()
    {

        (TestServer server, HeaderProbe probe) = await CreateAgentAsync($"http://{ThirdPartyHost}/agent");

        TestCapturingLogger<A2AClientService> logger = new();

        using (server)
        {

            await DispatchAsync(probe, $"http://{ThirdPartyHost}/", allowlist: [], logger);

        }

        TestLogEntry[] withheld = [.. logger.Entries.Where(static entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("Arcanum:Integrations:A2A:AllowedRemoteAgents", StringComparison.Ordinal))];

        // Both the discovery client and the post-discovery client report the withholding.
        Assert.NotEmpty(withheld);

        Assert.All(
            withheld,
            static entry => Assert.DoesNotContain("operator-secret", entry.Message, StringComparison.Ordinal));

        Assert.All(
            logger.Entries,
            static entry => Assert.DoesNotContain("operator-secret", entry.Message, StringComparison.Ordinal));

    }

    private static async Task<Result<A2ADispatchResult>> DispatchAsync(
        HeaderProbe probe,
        string discoveryUrl,
        string[] allowlist,
        ILogger<A2AClientService>? logger = null)
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Conclave = true, A2AClient = true },
            Integrations = new IntegrationSettings
            {
                A2A = new A2AIntegrationSettings
                {
                    OutboundCredentialEnvironmentVariable = EnvVar,
                    AllowedRemoteAgents = allowlist,
                },
            },
        };

        A2AClientService client = new(
            new SingleHandlerHttpClientFactory(probe),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            logger ?? NullLogger<A2AClientService>.Instance);

        return await client.DispatchSendingAsync("do the thing", null, discoveryUrl);

    }

    private static async Task<(TestServer Server, HeaderProbe Probe)> CreateAgentAsync(string advertisedInterfaceUrl)
    {

        AgentCard card = new()
        {
            Name = "Probe agent",
            Version = "1.0.0",
            SupportedInterfaces =
            [
                new AgentInterface { Url = advertisedInterfaceUrl, ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" },
            ],
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"],
        };

        IHost host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {

                webHost.UseTestServer();

                webHost.ConfigureServices(static services => services.AddRouting());

                webHost.Configure(app =>
                {

                    app.UseRouting();

                    app.UseEndpoints(endpoints =>
                    {

                        A2AServer server = new(
                            new EchoAgent(),
                            new InMemoryTaskStore(),
                            new ChannelEventNotifier(),
                            NullLogger<A2AServer>.Instance,
                            new A2AServerOptions { AutoAppendHistory = true });

                        endpoints.MapA2A(server, "/agent");

                        endpoints.MapWellKnownAgentCard(card);

                    });

                });

            })
            .StartAsync();

        TestServer testServer = host.GetTestServer();

        return (testServer, new HeaderProbe(testServer.CreateHandler()));

    }

    private sealed class HeaderProbe(HttpMessageHandler inner) : DelegatingHandler(inner)
    {

        /// <summary>Every request seen, as <c>host => credential or "(none)"</c>.</summary>
        public System.Collections.Concurrent.ConcurrentBag<(string Host, string Credential)> Requests { get; } = [];

        public IEnumerable<string> CredentialsSentTo(string host) => Requests
            .Where(r => string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase))
            .Select(static r => r.Credential)
            .Where(static c => c != "(none)");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {

            string credential = request.Headers.TryGetValues(
                A2AClientService.DefaultOutboundCredentialHeader,
                out IEnumerable<string>? values)
                ? string.Join(",", values)
                : "(none)";

            Requests.Add((request.RequestUri?.Host ?? string.Empty, credential));

            return base.SendAsync(request, cancellationToken);

        }

    }

    private sealed class SingleHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);

    }

    private sealed class EchoAgent : IAgentHandler
    {

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.AddArtifactAsync([Part.FromText("done")], cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

    }

}
