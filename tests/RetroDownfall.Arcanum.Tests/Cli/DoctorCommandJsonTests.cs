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
public sealed class DoctorCommandJsonTests : IDisposable
{

    private readonly string _testHome =
        Path.Combine(Path.GetTempPath(), "arcanum-tests", $"doctor-{Guid.NewGuid():N}");

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    public DoctorCommandJsonTests()
    {

        Directory.CreateDirectory(_testHome);

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _testHome);

    }

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

        Assert.Contains(report.Checks, c => c.Name == "ProviderCredentials");

        Assert.Contains(report.Checks, c => c.Name == "MCP");

        Assert.Contains(report.Checks, c => c.Name == "Tokenizer");

        Assert.Contains(report.Checks, c => c.Name == "FileEncryption");

        Assert.Contains(report.Checks, c => c.Name == "API Health");

    }

    [Fact]
    public async Task GlobalJson_before_doctor_emits_only_json_to_stdout()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<ISecretStore>(new NullSecretStore());

        CliTestResult result = await CliTestHarness.RunAsync(services, ["--json", "doctor"]);

        DoctorReport? report = JsonSerializer.Deserialize(
            result.Output,
            DoctorReportJsonContext.Default.DoctorReport);

        Assert.NotNull(report);

        Assert.Empty(result.Error);

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

    /// <summary>
    /// Something answered on the host's port, and answered as no Arcanum host would. "Not reachable"
    /// is the one verdict that is wrong here: it points the operator at starting a host, and a second
    /// host cannot bind a port another process already holds. The registered health check already
    /// reports this correctly, so the legacy probe row must not contradict it.
    /// </summary>
    [Fact]
    public async Task DoctorJson_reports_a_foreign_responder_on_the_port_rather_than_an_unreachable_host()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<ISecretStore>(new NullSecretStore());

        services.AddSingleton<IHttpClientFactory>(new UnexpectedResponderHttpClientFactory());

        CliTestResult result = await CliTestHarness.RunAsync(services, ["doctor", "--json"]);

        DoctorReport? report = JsonSerializer.Deserialize(result.Output, DoctorReportJsonContext.Default.DoctorReport);

        Assert.NotNull(report);

        DoctorCheck apiHealth = Assert.Single(report.Checks, c => c.Name == "API Health");

        Assert.Equal("fail", apiHealth.Status);

        Assert.DoesNotContain("Not reachable", apiHealth.Detail ?? string.Empty, StringComparison.Ordinal);

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

    private sealed class UnexpectedResponderHttpClientFactory : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(new UnexpectedResponderHandler(), disposeHandler: true)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };

    }

    /// <summary>
    /// A peer that accepted the connection and then answered as nothing HTTP would — the shape a
    /// foreign service holding the configured port produces.
    /// </summary>
    private sealed class UnexpectedResponderHandler : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(
                HttpRequestError.InvalidResponse,
                "The response ended prematurely.");

    }

    public void Dispose()
    {

        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {

            global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

        }

        if (Directory.Exists(_testHome))
        {

            Directory.Delete(_testHome, recursive: true);

        }

    }

    private void SetEnvironment(string name, string value)
    {

        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);

    }

}
