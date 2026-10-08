using System.Net;

using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Commands.Daemon;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Hosting;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// <c>daemon alert</c> advertises a closed severity set. The parser has to enforce exactly that set:
/// a value the operator cannot see in the help text must never reach the Comm Link dispatcher.
/// </summary>
[Collection("GlobalConsole")]
public sealed class DaemonCommandTests
{
    [Theory]
    [InlineData("9")]
    [InlineData("-1")]
    [InlineData("1")]
    [InlineData("bogus")]
    public void Alert_rejects_a_severity_outside_the_documented_set_without_calling_the_api(string severity)
    {
        RecordingHandler handler = new();

        CliTestResult result = RunCommand(
            handler,
            ["daemon", "alert", "disk full", "--severity", severity]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--severity", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Info")]
    [InlineData("warning")]
    [InlineData("CRITICAL")]
    public void Alert_accepts_the_documented_severity_names(string severity)
    {
        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<bool>(true, true, null),
            ArcanumJsonContext.Default.ApiResponseBoolean));

        CliTestResult result = RunCommand(
            handler,
            ["daemon", "alert", "disk full", "--severity", severity]);

        Assert.Equal(0, result.ExitCode);

        _ = Assert.Single(handler.Requests);
    }

    /// <summary>
    /// R-340: the daemon verbs run on every platform, so their progress is a platform-neutral diagnostic
    /// on stderr rather than "launchd" text on stdout. The command's result stays on stdout.
    /// </summary>
    [Theory]
    [InlineData("install", "Daemon installed and bootstrapped.")]
    [InlineData("uninstall", "Daemon uninstall finished.")]
    [InlineData("status", "Daemon is running (stub).")]
    public void Install_progress_is_diagnostic_and_platform_neutral(string verb, string expectedResult)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.RemoveAll<IDaemonManager>();

        services.AddSingleton<IDaemonManager>(new StubDaemonManager());

        CliTestResult result = CliTestHarness.Run(services, "daemon", verb);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains(expectedResult, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("launchd", result.Output + result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("\u2026", result.Output, StringComparison.Ordinal);

        Assert.Contains("daemon", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_on_a_manager_that_needs_an_account_hands_it_the_prompted_credential()
    {
        StubDaemonManager manager = new(requiresServiceAccount: true);

        StubServiceAccountPrompt prompt = new(
            Result<DaemonServiceCredential>.Success(new DaemonServiceCredential(@"HOST\me", "pw")));

        CliTestResult result = RunInstall(manager, prompt);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(1, prompt.Reads);

        DaemonInstallRequest request = Assert.Single(manager.InstallRequests);

        Assert.Equal(@"HOST\me", request.ServiceAccount?.AccountName);

        Assert.Equal("pw", request.ServiceAccount?.Password);

        Assert.DoesNotContain("pw", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_stops_with_a_configuration_error_and_creates_nothing_when_no_account_can_be_read()
    {
        StubDaemonManager manager = new(requiresServiceAccount: true);

        StubServiceAccountPrompt prompt = new(
            Result<DaemonServiceCredential>.Failure(
                new Error("DaemonServiceAccountUnavailable", "No route to ask for the account.")));

        CliTestResult result = RunInstall(manager, prompt);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(manager.InstallRequests);

        Assert.Contains("No route to ask for the account.", result.Error + result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_does_not_ask_for_an_account_when_the_daemon_runs_as_the_invoking_user()
    {
        StubDaemonManager manager = new(requiresServiceAccount: false);

        StubServiceAccountPrompt prompt = new(
            Result<DaemonServiceCredential>.Failure(new Error("Unexpected", "must not be asked")));

        CliTestResult result = RunInstall(manager, prompt);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(0, prompt.Reads);

        Assert.Null(Assert.Single(manager.InstallRequests).ServiceAccount);
    }

    private static CliTestResult RunInstall(StubDaemonManager manager, StubServiceAccountPrompt prompt)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.RemoveAll<IDaemonManager>();

        services.AddSingleton<IDaemonManager>(manager);

        services.RemoveAll<IDaemonServiceAccountPrompt>();

        services.AddSingleton<IDaemonServiceAccountPrompt>(prompt);

        return CliTestHarness.Run(services, "daemon", "install");
    }

    private sealed class StubServiceAccountPrompt(Result<DaemonServiceCredential> outcome) : IDaemonServiceAccountPrompt
    {
        public int Reads { get; private set; }

        public Task<Result<DaemonServiceCredential>> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;

            return Task.FromResult(outcome);
        }
    }

    private sealed class StubDaemonManager(bool requiresServiceAccount = false) : IDaemonManager
    {
        public bool RequiresServiceAccount => requiresServiceAccount;

        public List<DaemonInstallRequest> InstallRequests { get; } = [];

        public Task<Result> InstallAsync(DaemonInstallRequest request, CancellationToken cancellationToken)
        {
            InstallRequests.Add(request);

            return Task.FromResult(Result.Success());
        }

        public Task<Result> UninstallAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<string>> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<string>.Success("Daemon is running (stub)."));
    }

    private static CliTestResult RunCommand(RecordingHandler handler, string[] args)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore("test-key"));

        CliTestHarness.AddKeyedArcanumResponder(
            services,
            "test-key");

        return CliTestHarness.Run(services, args);
    }

    private static HttpResponseMessage CreateResponse<T>(
        ApiResponse<T> envelope,
        JsonTypeInfo<ApiResponse<T>> typeInfo)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(json),
        };
    }

    private sealed class FakeSecretStore(string apiKey) : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class FakeHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));

            HttpResponseMessage response = responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(request);

            return Task.FromResult(response);
        }
    }
}
