using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Exit 3 is the automation signal for "the host was unreachable". Reporting a server-side domain
/// failure or a missing credential as 3 tells a wrapper to retry the network instead of surfacing a
/// fault the operator has to fix.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class BudgetAndConclaveExitCodeTests
{

    [Fact]
    public void Budget_reports_a_server_side_failure_as_a_generic_error_not_a_network_error()
    {

        RecordingHandler handler = new(static _ => CreateErrorResponse(
            new Error("Budget.Unavailable", "Budget state could not be read."),
            ArcanumJsonContext.Default.ApiResponseBudgetSummaryDto));

        CliTestResult result = RunCommand(handler, ["budget"]);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains("Budget state could not be read.", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Budget_reports_a_missing_api_key_as_a_configuration_error_not_a_network_error()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(
            handler,
            ["budget"],
            apiKey: null);

        Assert.NotEqual((int)CliExitCode.NetworkError, result.ExitCode);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public void Conclave_status_reports_a_server_side_failure_as_a_generic_error_not_a_network_error()
    {

        RecordingHandler handler = new(static _ => CreateErrorResponse(
            new Error("Conclave.Unavailable", "Conclave state could not be read."),
            ArcanumJsonContext.Default.ApiResponseConclaveStatusDto));

        CliTestResult result = RunCommand(handler, ["conclave", "status"]);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains("Conclave state could not be read.", result.Error, StringComparison.Ordinal);

    }

    private static CliTestResult RunCommand(
        RecordingHandler handler,
        string[] args,
        string? apiKey = "test-key")
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore(apiKey));

        return CliTestHarness.Run(services, args);

    }

    private static HttpResponseMessage CreateErrorResponse<T>(
        Error error,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ApiResponse<T>(default!, false, error),
            typeInfo);

        return new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new ByteArrayContent(json),
        };

    }

    private sealed class FakeSecretStore(string? apiKey) : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(
                apiKey is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(apiKey));

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
