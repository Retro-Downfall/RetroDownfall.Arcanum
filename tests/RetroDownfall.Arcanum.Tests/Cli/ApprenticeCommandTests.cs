using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Conclave;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class ApprenticeCommandTests
{
    private static readonly Guid SampleId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Apprentice_list_calls_get_apprentices()
    {
        ApprenticeSummaryDto summary = new(SampleId, null, "Task", "Do the thing", "Idle", 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<ApprenticeSummaryDto>>(new ListPageResult<ApprenticeSummaryDto>([summary], false), true, null),
            ArcanumJsonContext.Default.ApiResponseListPageResultApprenticeSummaryDto));

        CliTestResult result = RunCommand(handler, ["apprentice", "list"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/apprentices", request.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// Every <c>Result.IsFailure</c> exit in this file returned the generic exit code, so a
    /// server-down failure was indistinguishable from a real domain failure. Routed through
    /// <c>CliFailureExit</c>, a <c>Connection.*</c> failure now exits 3 and names the address tried.
    /// A non-default port is configured (rather than asserting the harness's own default address)
    /// so the assertion is load-bearing on <c>ApprenticeCommands.WriteError</c> actually reading
    /// <c>Arcanum:Host</c>, not just coinciding with a hardcoded default.
    /// </summary>
    [Fact]
    public void Apprentice_list_reports_a_network_failure_and_names_the_configured_base_address()
    {
        const int ConfiguredPort = 19999;

        RecordingHandler handler = new(_ => throw new HttpRequestException("Connection refused"));

        CliTestResult result = RunCommand(
            handler,
            ["apprentice", "list"],
            configureServices: services => services.Configure<ArcanumSettings>(s => s.Host.Port = ConfiguredPort));

        Assert.Equal((int)CliExitCode.NetworkError, result.ExitCode);

        string expectedAddress = ArcanumLocalApiAddress.ResolveBaseUrl(new HostSettings { Port = ConfiguredPort });

        Assert.Contains(expectedAddress, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Apprentice_create_posts_goal_and_derives_name()
    {
        ApprenticeDetailDto detail = new(
            SampleId, null, null, "Do the thing", "Do the thing", [], 0, "Idle", null, "/tmp/ws", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ApprenticeDetailDto>(detail, true, null),
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
            HttpStatusCode.Created));

        CliTestResult result = RunCommand(handler, ["apprentice", "create", "--goal", "Do the thing"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/apprentices", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"goal\":\"Do the thing\"", body, StringComparison.Ordinal);

        Assert.Contains("\"name\":\"Do the thing\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Apprentice_start_posts_to_start_route()
    {
        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<string>(SampleId.ToString("D"), true, null),
            ArcanumJsonContext.Default.ApiResponseString,
            HttpStatusCode.Accepted));

        CliTestResult result = RunCommand(handler, ["apprentice", "start", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal($"/api/apprentices/{SampleId:D}/start", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Apprentice_cast_surfaces_conclave_disabled_error()
    {
        Error error = new("Apprentice.ConclaveDisabled", "The Conclave is disabled.");

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ApprenticeDetailDto>(null, false, error),
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
            HttpStatusCode.Conflict));

        CliTestResult result = RunCommand(handler, ["apprentice", "cast", SampleId.ToString(), "--goal", "Sub-goal"]);

        Assert.Equal(1, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal($"/api/apprentices/{SampleId:D}/cast", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Apprentice_delete_binds_id_and_handles_no_content()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliTestResult result = RunCommand(handler, ["--yes", "apprentice", "delete", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);
    }

    /// <summary>An irreversible delete must ask before it acts.</summary>
    [Fact]
    public void Apprentice_delete_requires_confirmation_before_sending_request()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliTestResult result = RunCommand(handler, ["apprentice", "delete", SampleId.ToString()]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--yes", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancelling an Apprentice stops work in progress, so like <c>delete</c> it asks first; without
    /// <c>--yes</c> a run that cannot be asked is refused and nothing is sent.
    /// </summary>
    [Fact]
    public void Cancel_asks_for_confirmation()
    {
        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<string>("cancelled", true, null),
            ArcanumJsonContext.Default.ApiResponseString));

        CliTestResult refused = RunCommand(handler, ["apprentice", "cancel", SampleId.ToString()]);

        Assert.Equal((int)CliExitCode.ConfigurationError, refused.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--yes", refused.Error, StringComparison.Ordinal);

        CliTestResult approved = RunCommand(handler, ["--yes", "apprentice", "cancel", SampleId.ToString()]);

        Assert.Equal(0, approved.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal($"/api/apprentices/{SampleId:D}/cancel", request.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// Reweaving replaces the plan the Apprentice has left to run, so it asks first as well, after the
    /// plan itself has been validated: a malformed plan is refused before any question is put.
    /// </summary>
    [Fact]
    public void Reweave_asks_for_confirmation_after_the_plan_is_validated()
    {
        const string Plan = "[{\"index\":0,\"description\":\"Step one\"}]";

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ApprenticeDetailDto>(
                new ApprenticeDetailDto(
                    SampleId, null, null, "Do the thing", "Do the thing", [new PlanStep { Index = 0, Description = "Step one" }], 0, "Idle", null, "/tmp/ws", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto));

        CliTestResult malformed = RunCommand(handler, ["apprentice", "reweave", SampleId.ToString(), "--plan", "not json"]);

        Assert.Equal(1, malformed.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.DoesNotContain("--yes", malformed.Error, StringComparison.Ordinal);

        CliTestResult refused = RunCommand(handler, ["apprentice", "reweave", SampleId.ToString(), "--plan", Plan]);

        Assert.Equal((int)CliExitCode.ConfigurationError, refused.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--yes", refused.Error, StringComparison.Ordinal);

        CliTestResult approved = RunCommand(handler, ["--yes", "apprentice", "reweave", SampleId.ToString(), "--plan", Plan]);

        Assert.Equal(0, approved.ExitCode);

        Assert.Equal($"/api/apprentices/{SampleId:D}/reweave", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
    }

    [Fact]
    public void List_follows_hasMore_until_exhausted()
    {
        DateTimeOffset firstUpdated = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

        ApprenticeSummaryDto first = new(SampleId, null, "Task", "firstgoal", "Idle", 0, 0, firstUpdated, firstUpdated);

        Guid secondId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        ApprenticeSummaryDto second = new(secondId, null, "Task", "secondgoal", "Idle", 0, 0, firstUpdated.AddDays(-1), firstUpdated.AddDays(-1));

        RecordingHandler handler = new(request => CreateResponse(
            new ApiResponse<ListPageResult<ApprenticeSummaryDto>>(
                request.RequestUri!.Query.Contains("beforeUpdatedAt=", StringComparison.Ordinal)
                    ? new ListPageResult<ApprenticeSummaryDto>([second], false)
                    : new ListPageResult<ApprenticeSummaryDto>([first], true, NextBeforeUpdatedAt: firstUpdated),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseListPageResultApprenticeSummaryDto));

        CliTestResult result = RunCommand(handler, ["apprentice", "list"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(2, handler.Requests.Count);

        Assert.Contains("beforeUpdatedAt=", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);

        Assert.Contains(SampleId.ToString("D"), result.Output, StringComparison.Ordinal);

        Assert.Contains(secondId.ToString("D"), result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void List_with_a_limit_reads_one_page_and_says_more_exist()
    {
        DateTimeOffset updated = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

        ApprenticeSummaryDto only = new(SampleId, null, "Task", "Only page goal", "Idle", 0, 0, updated, updated);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<ApprenticeSummaryDto>>(
                new ListPageResult<ApprenticeSummaryDto>([only], true, NextBeforeUpdatedAt: updated),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseListPageResultApprenticeSummaryDto));

        CliTestResult result = RunCommand(handler, ["apprentice", "list", "--limit", "1"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Single(handler.Requests);

        Assert.Contains("omit --limit", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("apprentice list --campaign-id not-a-guid")]
    [InlineData("apprentice create --goal Do-the-thing --campaign-id not-a-guid")]
    public void Invalid_campaign_id_diagnostics_name_the_option_the_operator_typed(string commandLine)
    {
        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, commandLine.Split(' '));

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("--campaign-id", result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("--campaignId", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The list is where an operator reads the identifier to hand to <c>show</c>, <c>cancel</c> or
    /// <c>delete</c>, so what it prints must be something those verbs accept. An eight-character
    /// fragment of the identifier is none of an exact ID, a name or a name prefix.
    /// </summary>
    [Fact]
    public void List_prints_an_identifier_that_show_accepts()
    {
        ApprenticeSummaryDto summary = new(SampleId, null, "Task", "Do the thing", "Idle", 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler listHandler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<ApprenticeSummaryDto>>(new ListPageResult<ApprenticeSummaryDto>([summary], false), true, null),
            ArcanumJsonContext.Default.ApiResponseListPageResultApprenticeSummaryDto));

        CliTestResult list = RunCommand(listHandler, ["apprentice", "list"]);

        Assert.Equal(0, list.ExitCode);

        string printed = System.Text.RegularExpressions.Regex
            .Match(list.Output, "[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}")
            .Value;

        Assert.Equal(SampleId.ToString("D"), printed, ignoreCase: true);

        ApprenticeDetailDto detail = new(
            SampleId, null, null, "Do the thing", "Do the thing", [], 0, "Idle", null, "/tmp/ws", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler showHandler = new(_ => CreateResponse(
            new ApiResponse<ApprenticeDetailDto>(detail, true, null),
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto));

        CliTestResult show = RunCommand(showHandler, ["apprentice", "show", printed]);

        Assert.Equal(0, show.ExitCode);

        Assert.Equal($"/api/apprentices/{SampleId:D}", Assert.Single(showHandler.Requests).RequestUri!.AbsolutePath);
    }

    private static CliTestResult RunCommand(
        RecordingHandler handler,
        string[] args,
        Action<ServiceCollection>? configureServices = null)
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

        configureServices?.Invoke(services);

        return CliTestHarness.Run(services, args);
    }

    private static HttpResponseMessage CreateResponse<T>(
        ApiResponse<T> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);

        return new HttpResponseMessage(status)
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

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
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

            Requests.Add(snapshot);

            HttpResponseMessage response = responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(request);

            return Task.FromResult(response);
        }
    }
}
