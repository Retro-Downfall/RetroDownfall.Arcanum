using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class LoreCommandBindingTests
{

    /// <summary>
    /// W10-3: every <c>Result.IsFailure</c> exit in this file returned the generic exit code, so a
    /// server-down failure was indistinguishable from a real domain failure. Routed through
    /// <c>CliFailureExit</c>, a <c>Connection.*</c> failure now exits 3 and names the address tried.
    /// This is the literal repro the finding named: <c>lore list</c> with the server down.
    /// </summary>
    [Fact]
    public void Lore_list_reports_a_network_failure_and_names_the_configured_base_address()
    {

        RecordingHandler handler = new(_ => throw new HttpRequestException("Connection refused"));

        CliTestResult result = RunCommand(handler, ["lore", "list"]);

        Assert.Equal((int)CliExitCode.NetworkError, result.ExitCode);

        string expectedAddress = ArcanumLocalApiAddress.ResolveBaseUrl(new HostSettings());

        Assert.Contains(expectedAddress, result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Lore_set_binds_key_and_value_arguments()
    {

        RecordingHandler handler = CreateLoreHandler();

        CliTestResult result = RunCommand(handler, ["lore", "set", "ward.color", "cobalt"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/lore", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"key\":\"ward.color\"", body, StringComparison.Ordinal);

        Assert.Contains("\"value\":\"cobalt\"", body, StringComparison.Ordinal);

    }

    [Fact]
    public void Lore_get_binds_key_argument()
    {

        RecordingHandler handler = CreateLoreHandler();

        CliTestResult result = RunCommand(handler, ["lore", "get", "ward.color"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/lore/ward.color", request.RequestUri!.AbsolutePath);

    }

    /// <summary>
    /// A stored lore value is read back to be used, not to be looked at. Rendering it inside a
    /// Spectre panel drew a border box around it and re-flowed every line at the 80-column profile
    /// width redirection falls back to, so `VALUE=$(arcanum lore get k)` captured box art and a
    /// multi-line value could not survive a set/get round-trip.
    /// </summary>
    [Fact]
    public void Lore_get_emits_the_stored_value_verbatim_without_panel_chrome()
    {

        string value = string.Join(
            "\n",
            new string('a', 120),
            "second line with  doubled  spaces",
            "third");

        RecordingHandler handler = new(_ => CreateLoreResponse(new ApiResponse<LoreDto>(
            new LoreDto("deploy.notes", value, DateTime.UtcNow),
            true,
            null)));

        CliTestResult result = RunCommand(handler, ["lore", "get", "deploy.notes"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(value, result.Output.TrimEnd('\r', '\n'));

    }

    /// <summary>
    /// Writing the value verbatim fixed the panel, but only for the text mode. Under <c>--output-format json</c> anything left on stdout still falls through the legacy text wrapper, which strips every ESC-introduced sequence out of the middle of the buffer and trims the trailing newlines off the end — so the mode that exists for machines was the one that could not reproduce the stored value. A structured document keeps it out of that wrapper, exactly as <c>workspace read</c> already does.
    /// </summary>
    [Fact]
    public void Lore_get_json_reproduces_the_stored_value_byte_for_byte()
    {

        string value = "\u001b[31mred\u001b[0m literal escape\nplain line\n\n";

        RecordingHandler handler = new(_ => CreateLoreResponse(new ApiResponse<LoreDto>(
            new LoreDto("deploy.notes", value, DateTime.UtcNow),
            true,
            null)));

        CliTestResult result = RunCommand(handler, ["lore", "get", "deploy.notes", "--json"]);

        Assert.Equal(0, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal("deploy.notes", document.RootElement.GetProperty("key").GetString());

        Assert.Equal(value, document.RootElement.GetProperty("value").GetString());

    }

    [Fact]
    public void Lore_delete_binds_key_argument()
    {

        RecordingHandler handler = new(_ => CreateBooleanResponse(new ApiResponse<bool>(true, true, null)));

        CliTestResult result = RunCommand(handler, ["--yes", "lore", "delete", "ward.color"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);

        Assert.Equal("/api/lore/ward.color", request.RequestUri!.AbsolutePath);

    }

    /// <summary>W10-2: an irreversible delete must ask before it acts.</summary>
    [Fact]
    public void Lore_delete_requires_confirmation_before_sending_request()
    {

        RecordingHandler handler = new(_ => CreateBooleanResponse(new ApiResponse<bool>(true, true, null)));

        CliTestResult result = RunCommand(handler, ["lore", "delete", "ward.color"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--yes", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Daemon_initiative_binds_job_name_and_minutes()
    {

        RecordingHandler handler = new(_ => CreateDaemonResponse(
            new ApiResponse<UnseenServantJobStatusDto>(
                new UnseenServantJobStatusDto("heartbeat", "spell", 5, 15, true),
                true,
                null)));

        CliTestResult result = RunCommand(handler, ["daemon", "initiative", "heartbeat", "15"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/unseen-servant/jobs/heartbeat/initiative", request.RequestUri!.AbsolutePath);

        Assert.Contains("\"intervalMinutes\":15", ReadBody(request), StringComparison.Ordinal);

    }

    [Fact]
    public void Daemon_alert_binds_message_and_options()
    {

        RecordingHandler handler = new(_ => CreateBooleanResponse(new ApiResponse<bool>(true, true, null)));

        CliTestResult result = RunCommand(
            handler,
            ["daemon", "alert", "Disk full", "--title", "Ops", "--severity", "Warning", "--source", "test"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/commlink/send", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("Disk full", body, StringComparison.Ordinal);

        Assert.Contains("\"title\":\"Ops\"", body, StringComparison.Ordinal);

        Assert.Contains("\"source\":\"test\"", body, StringComparison.Ordinal);

        Assert.Contains("\"body\":\"Disk full\"", body, StringComparison.Ordinal);

    }

    private static RecordingHandler CreateLoreHandler() =>
        new(_ => CreateLoreResponse(new ApiResponse<LoreDto>(
            new LoreDto("ward.color", "cobalt", DateTime.UtcNow),
            true,
            null)));

    private static CliTestResult RunCommand(RecordingHandler handler, string[] args)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore("test-key"));

        return CliTestHarness.Run(services, args);

    }

    private static HttpResponseMessage CreateLoreResponse(ApiResponse<LoreDto> envelope)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, ArcanumJsonContext.Default.ApiResponseLoreDto);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(json),
        };

    }

    private static HttpResponseMessage CreateDaemonResponse(ApiResponse<UnseenServantJobStatusDto> envelope)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, ArcanumJsonContext.Default.ApiResponseUnseenServantJobStatusDto);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(json),
        };

    }

    private static HttpResponseMessage CreateBooleanResponse(ApiResponse<bool> envelope)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, ArcanumJsonContext.Default.ApiResponseBoolean);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(json),
        };

    }

    private static string ReadBody(HttpRequestMessage request)
    {

        if (request.Content is null)
        {

            return string.Empty;

        }

        return request.Content.ReadAsStringAsync().GetAwaiter().GetResult();

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

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {

            HttpRequestMessage snapshot = new(request.Method, request.RequestUri);

            if (request.Content is not null)
            {

                byte[] body = request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();

                snapshot.Content = new ByteArrayContent(body);

                foreach (KeyValuePair<string, IEnumerable<string>> contentHeader in request.Content.Headers)
                {

                    snapshot.Content.Headers.TryAddWithoutValidation(contentHeader.Key, contentHeader.Value);

                }

            }

            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {

                snapshot.Headers.TryAddWithoutValidation(header.Key, header.Value);

            }

            Requests.Add(snapshot);

            return Task.FromResult(responder(request));

        }

    }

}
