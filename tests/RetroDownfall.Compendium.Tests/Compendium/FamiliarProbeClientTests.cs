using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Compendium.Ux.Services;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

/// <summary>
/// The probe is the one thing Compendium asks the host for, so the remediation it prints has to name
/// the failure the operator actually has. Under ListenAny the probe targets HTTPS, and Compendium's
/// own certificate generator never installs into the OS trust store — a rejected chain must not be
/// reported as a stopped host.
/// </summary>
public sealed class FamiliarProbeClientTests
{

    [Fact]
    public async Task A_rejected_certificate_is_reported_as_a_trust_problem_not_a_stopped_host()
    {

        FamiliarProbeClient client = CreateClient(
            listenAny: true,
            new HttpRequestException(
                HttpRequestError.SecureConnectionError,
                "The SSL connection could not be established, see inner exception.",
                new AuthenticationException(
                    "The remote certificate was rejected by the provided RemoteCertificateValidationCallback.")));

        Result<FamiliarProbeResult> probed = await client.ProbeAsync(
            "ClaudeCode-subscription",
            CancellationToken.None);

        Assert.True(probed.IsFailure);

        Assert.DoesNotContain("not running", probed.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("certificate", probed.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("https://localhost:5443", probed.Error.Message, StringComparison.Ordinal);

    }

    /// <summary>A refused connection is the one case where "start the host" really is the remedy.</summary>
    [Fact]
    public async Task A_refused_connection_still_tells_the_operator_to_start_the_host()
    {

        FamiliarProbeClient client = CreateClient(
            listenAny: false,
            new HttpRequestException(
                "Connection refused (localhost:5001)",
                new SocketException((int)SocketError.ConnectionRefused)));

        Result<FamiliarProbeResult> probed = await client.ProbeAsync(
            "ClaudeCode-subscription",
            CancellationToken.None);

        Assert.True(probed.IsFailure);

        Assert.Contains("not running", probed.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    private static FamiliarProbeClient CreateClient(bool listenAny, Exception transportFailure)
    {

        ArcanumSettings settings = new()
        {

            Host = new HostSettings
            {

                Port = 5001,

                ListenAny = listenAny,

                Https = new HttpsSettings { Enabled = listenAny, Port = 5443 },

            },

        };

        return new FamiliarProbeClient(
            new StubConfigurationStore(settings),
            new StubSecretStore("test-api-key"),
            new ThrowingHttpClientFactory(transportFailure));

    }

    private sealed class ThrowingHttpClientFactory(Exception failure) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new(new ThrowingHandler(failure));

    }

    private sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(failure);

    }

    private sealed class StubConfigurationStore(ArcanumSettings settings) : IArcanumConfigurationStore
    {

        public string ConfigurationFilePath => "arcanum.json";

        public event EventHandler? ExternalChange
        {

            add { }

            remove { }

        }

        public DateTimeOffset? GetLastWriteTimeUtc() => null;

        public Task<ArcanumSettings> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(settings);

        public Task<ConfigurationWriteResult> WriteAsync(
            ArcanumSettings updated,
            CancellationToken ct = default) =>
            throw new NotSupportedException("The probe never writes configuration.");

        public void Dispose()
        {

        }

    }

    private sealed class StubSecretStore(string apiKey) : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string value) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

}
