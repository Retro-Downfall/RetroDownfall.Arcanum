using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class DoctorCommandJsonTests
{

    [Fact]
    public async Task DoctorJson_EmitsDoctorReportToStdout()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<ISecretStore>(new NullSecretStore());

        CliTestResult result = await CliTestHarness.RunAsync(services, ["doctor", "--json"]);

        DoctorReport? report = JsonSerializer.Deserialize(result.Output, DoctorReportJsonContext.Default.DoctorReport);

        Assert.NotNull(report);

        Assert.NotEmpty(report.Checks);

        Assert.Contains(report.Checks, c => c.Name == "Version");

        Assert.Contains(report.Checks, c => c.Name == "Paths");

        Assert.Contains(report.Checks, c => c.Name == "Configuration");

        Assert.Contains(report.Checks, c => c.Name == "MCP");

        Assert.Contains(report.Checks, c => c.Name == "Tokenizer");

        Assert.Contains(report.Checks, c => c.Name == "API Health");

    }

    [Fact]
    public void DoctorJson_WithApiHealth_ProducesWarnStatusWhenServerUnreachable()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<ISecretStore>(new NullSecretStore());

        // Deterministic: do not depend on whether a local arcanum serve is listening.
        services.AddSingleton<IHttpClientFactory>(
            new ConnectionRefusedHttpClientFactory());

        CliTestResult result = CliTestHarness.Run(services, ["doctor", "--json"]);

        DoctorReport? report = JsonSerializer.Deserialize(result.Output, DoctorReportJsonContext.Default.DoctorReport);

        Assert.NotNull(report);

        DoctorCheck apiHealth = Assert.Single(report.Checks, c => c.Name == "API Health");

        Assert.Equal("warn", apiHealth.Status);

    }

    private sealed class NullSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class ConnectionRefusedHttpClientFactory : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(new ConnectionRefusedHandler(), disposeHandler: true)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };

    }

    private sealed class ConnectionRefusedHandler : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(
                "Connection refused",
                new SocketException((int)SocketError.ConnectionRefused));

    }

}
