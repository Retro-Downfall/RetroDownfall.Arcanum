using System.Net;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Cli.Diagnostics;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Issue #33 — every doctor finding names the action that actually helps. The probe state that says
/// "something answered, just not as an Arcanum host" is the one where the generic "start the host"
/// advice is worst: a second host cannot bind a port another process already holds.
/// </summary>
public sealed class HostHealthDiagnosticsTests
{

    [Fact]
    public async Task Inspection_peeks_the_key_and_fails_closed_when_the_store_is_corrupt()
    {

        RecordingHandler handler = new();

        HostHealthComponentsCheck check = new(
            Options.Create(new ArcanumSettings()),
            new StubHttpClientFactory(handler),
            new PeekOnlyCorruptSecretStore());

        DoctorFinding finding = await check.InspectAsync(CancellationToken.None);

        Assert.Equal(DoctorOutcome.Unhealthy, finding.Outcome);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.False(request.Headers.Contains("X-Arcanum-Key"));

    }

    [Fact]
    public void An_unexpected_responder_is_not_reported_as_a_host_that_never_answered()
    {

        DoctorFinding finding = HostHealthComponentsCheck.Describe(
            new HealthProbeResult(
                HealthProbeState.UnexpectedResponder,
                null,
                TimeSpan.Zero,
                "The response ended prematurely."));

        Assert.Equal(DoctorOutcome.Unhealthy, finding.Outcome);

        Assert.DoesNotContain("did not answer", finding.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(
            DoctorRemedyCommands.Serve,
            (finding.Remedies ?? []).Select(static remedy => remedy.Command));

    }

    [Theory]
    [InlineData(HealthProbeState.ConnectionRefused)]
    [InlineData(HealthProbeState.NetworkUnreachable)]
    [InlineData(HealthProbeState.DnsFailure)]
    public void A_host_that_never_answered_still_recommends_starting_it(HealthProbeState state)
    {

        DoctorFinding finding = HostHealthComponentsCheck.Describe(
            new HealthProbeResult(state, null, TimeSpan.Zero, "no listener"));

        Assert.Equal(DoctorOutcome.Unavailable, finding.Outcome);

        Assert.Contains(
            DoctorRemedyCommands.Serve,
            (finding.Remedies ?? []).Select(static remedy => remedy.Command));

    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {

                BaseAddress = new Uri("http://localhost:5001/"),

            };

    }

    private sealed class RecordingHandler : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            Requests.Add(request);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        }

    }

    private sealed class PeekOnlyCorruptSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            throw new InvalidOperationException("Host health must use Peek.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            throw new InvalidOperationException("Host health must use Peek.");

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Corrupted("ambiguous OS credential read"));

        public Task SaveApiKeyAsync(string apiKey) =>
            throw new InvalidOperationException("Host health must not persist credentials.");

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

}
