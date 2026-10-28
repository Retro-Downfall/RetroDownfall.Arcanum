using System.Globalization;

using System.Net;

using System.Text.Json;

using Microsoft.AspNetCore.WebUtilities;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Desktop;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class OperationPaginationCommandTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(520)]
    public void Default_listing_follows_every_page_and_emits_one_JSON_array(int count)
    {
        RecordingHandler handler = new((request, _) => PageFor(request, count));

        CliTestResult result = Run(handler, ["operation", "list", "--json"]);

        Assert.Equal(0, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);

        Assert.Equal(count, document.RootElement.GetArrayLength());

        Assert.Equal(count, document.RootElement.EnumerateArray().Select(row => row.GetProperty("id").GetGuid()).Distinct().Count());

        Assert.Equal(Math.Max(1, (count + 99) / 100), handler.Requests.Count);

        Assert.Equal(Enumerable.Range(0, handler.Requests.Count).Select(index => index * 100), handler.Requests.Select(uri => Number(uri, "offset")));

        Assert.All(handler.Requests, uri => Assert.Equal(100, Number(uri, "limit")));

        Assert.Equal(string.Empty, result.Error.Trim());
    }

    [Theory]
    [InlineData(100, 100, 100, 200)]
    [InlineData(500, 0, 500, 500)]
    [InlineData(100, 500, 20, null)]
    public void Explicit_limit_reads_one_page_and_preserves_filters_in_actionable_continuation(
        int limit, int offset, int shown, int? next)
    {
        const string kind = "custom 'kind' [red]";

        RecordingHandler handler = new((request, _) => PageFor(request, 520));

        CliTestResult result = Run(handler,
        [
            "operation", "list", "--json", "--limit", limit.ToString(CultureInfo.InvariantCulture),
            "--offset", offset.ToString(CultureInfo.InvariantCulture), "--kind", kind, "--state", "Pending",
        ]);

        Assert.Equal(0, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(shown, document.RootElement.GetArrayLength());

        Uri uri = Assert.Single(handler.Requests);

        Assert.Equal(kind, QueryHelpers.ParseQuery(uri.Query)["kind"].ToString());

        Assert.Equal("Pending", QueryHelpers.ParseQuery(uri.Query)["state"].ToString());

        Assert.Equal(offset, Number(uri, "offset"));

        Assert.Equal(limit, Number(uri, "limit"));

        if (next is { } nextOffset)
        {
            Assert.Contains($"arcanum operation list --limit {limit} --offset {nextOffset}", result.Error, StringComparison.Ordinal);

            Assert.Contains("--kind " + CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(kind), result.Error, StringComparison.Ordinal);

            Assert.Contains("--state Pending", result.Error, StringComparison.Ordinal);

            Assert.DoesNotContain("arcanum operation list", result.Output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(string.Empty, result.Error.Trim());
        }
    }

    [Fact]
    public void Offset_without_limit_follows_from_the_requested_row()
    {
        RecordingHandler handler = new((request, _) => PageFor(request, 520));

        CliTestResult result = Run(handler, ["operation", "list", "--json", "--offset", "400"]);

        Assert.Equal(0, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(120, document.RootElement.GetArrayLength());

        Assert.Equal([400, 500], handler.Requests.Select(uri => Number(uri, "offset")));
    }

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "-1")]
    [InlineData("--limit", "501")]
    [InlineData("--limit", "2147483647")]
    [InlineData("--offset", "-1")]
    [InlineData("--offset", "2147483647")]
    [InlineData("--offset", "2147483597")]
    public void Invalid_page_bounds_are_configuration_errors_without_a_request(string option, string value)
    {
        RecordingHandler handler = new((request, _) => PageFor(request, 0));

        CliTestResult result = Run(handler, ["operation", "list", option, value, "--plain"]);

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Equal(string.Empty, result.Output.Trim());

        Assert.Contains("page", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("backwards", false)]
    [InlineData("backwards", true)]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    [InlineData("oversized", false)]
    [InlineData("oversized", true)]
    [InlineData("timestamp", false)]
    [InlineData("timestamp", true)]
    [InlineData("terminal-next", false)]
    [InlineData("terminal-next", true)]
    public void Invalid_continuation_is_rejected_before_any_listing(string defect, bool singlePage)
    {
        RecordingHandler handler = new((request, _) =>
        {
            int offset = Number(request.RequestUri!, "offset");

            int limit = Number(request.RequestUri!, "limit");

            LongRunningOperationDto[] rows = defect == "empty" ? [] : Rows(offset, defect == "oversized" ? limit + 1 : 1);

            return Response(new ListPageResult<LongRunningOperationDto>(
                rows,
                defect != "terminal-next",
                defect == "missing" ? null : defect == "backwards" ? offset - 1 : offset + rows.Length,
                defect == "timestamp" ? DateTimeOffset.Parse("2026-10-27T12:00:00Z", CultureInfo.InvariantCulture) : null));
        });

        string[] args = ["operation", "list", "--offset", "100", "--plain"];

        CliTestResult result = Run(handler, singlePage ? [.. args, "--limit", "100"] : args);

        Assert.Equal(1, result.ExitCode);

        Assert.Single(handler.Requests);

        Assert.Equal(string.Empty, result.Output.Trim());

        Assert.Contains("Api.PaginationNoProgress", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_null_success_page_is_refused_without_publishing_rows(bool singlePage, bool laterPage)
    {
        RecordingHandler handler = new((request, call) => laterPage && call == 1 && !singlePage
            ? PageFor(request, 150)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":null,\"isSuccess\":true,\"error\":null}"),
            });

        string[] args = ["operation", "list", "--plain"];

        if (singlePage) args = [.. args, "--limit", "100", "--offset", laterPage ? "100" : "0"];

        CliTestResult result = Run(handler, args);

        Assert.Equal(1, result.ExitCode);

        Assert.Equal(laterPage && !singlePage ? 2 : 1, handler.Requests.Count);

        Assert.Equal(string.Empty, result.Output.Trim());

        Assert.Contains("Api.PaginationNoProgress", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_later_page_failure_does_not_print_the_successful_first_page(bool malformed)
    {
        RecordingHandler handler = new((request, call) => call == 1
            ? PageFor(request, 150)
            : malformed
                ? Response(new ListPageResult<LongRunningOperationDto>(Rows(100, 1), true, 100))
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"data\":null,\"isSuccess\":false,\"error\":{\"code\":\"Grimoire.Unavailable\",\"message\":\"Unavailable\"}}"),
                });

        CliTestResult result = Run(handler, ["operation", "list", "--plain"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Equal(2, handler.Requests.Count);

        Assert.Equal(string.Empty, result.Output.Trim());

        Assert.Contains(malformed ? "Api.PaginationNoProgress" : "Grimoire.Unavailable", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Integer_frontier_never_emits_an_unusable_continuation(bool hasMore)
    {
        int offset = int.MaxValue - 100;

        RecordingHandler handler = new((_, _) => Response(new ListPageResult<LongRunningOperationDto>(
            Rows(offset, 100), hasMore, hasMore ? int.MaxValue : null)));

        CliTestResult result = Run(handler,
            ["operation", "list", "--limit", "100", "--offset", offset.ToString(CultureInfo.InvariantCulture), "--json"]);

        Assert.Single(handler.Requests);

        if (hasMore)
        {
            Assert.Equal(1, result.ExitCode);

            Assert.Contains("Api.PaginationNoProgress", result.Error, StringComparison.Ordinal);

            Assert.DoesNotContain("publicSummary", result.Output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(0, result.ExitCode);

            using JsonDocument document = JsonDocument.Parse(result.Output);

            Assert.Equal(100, document.RootElement.GetArrayLength());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Caller_cancellation_after_a_successful_response_keeps_the_entire_payload_unpublished(int cancelAt)
    {
        using CancellationTokenSource cancellation = new();

        RecordingHandler handler = new((request, call) =>
        {
            HttpResponseMessage response = PageFor(request, 150);

            if (call == cancelAt)
            {
                string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                response.Content.Dispose();

                response.Content = new CancelOnDisposeContent(json, cancellation);
            }

            return response;
        });

        using ArcanumApiCredentialLease credentials = ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        ArcanumApiClient api = new(new ClientFactory(handler), credentials);

        using StringWriter output = new();

        using StringWriter error = new();

        CliInvocationOptions options = new(Json: true, Plain: true, Yes: false);

        using IDisposable invocation = CliInvocationContext.Push(options);

        OperationCommands command = new(
            api,
            new ConfiguredThemePalette(new ThemeSemanticColors(), new ThemeSemanticColors()),
            new ConsoleDispatcher(output, error, options));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.List(null, null, null, 0, cancellation.Token));

        Assert.Equal(cancelAt, handler.Requests.Count);

        Assert.Equal(string.Empty, output.ToString());

        Assert.Equal(string.Empty, error.ToString());
    }

    private sealed class CancelOnDisposeContent(string json, CancellationTokenSource cancellation) : StringContent(json)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing) cancellation.Cancel();
        }
    }

    private static HttpResponseMessage PageFor(HttpRequestMessage request, int total)
    {
        int offset = Number(request.RequestUri!, "offset");

        int limit = Number(request.RequestUri!, "limit");

        int count = Math.Max(0, Math.Min(limit, total - offset));

        bool more = offset + count < total;

        return Response(new ListPageResult<LongRunningOperationDto>(Rows(offset, count), more, more ? offset + count : null));
    }

    private static int Number(Uri uri, string name) =>
        int.TryParse(QueryHelpers.ParseQuery(uri.Query)[name], CultureInfo.InvariantCulture, out int value)
            ? value
            : name == "limit" ? 100 : 0;

    private static LongRunningOperationDto[] Rows(int offset, int count) =>
        Enumerable.Range(offset, count).Select(index => new LongRunningOperationDto(
            new Guid(index + 1, 0, 0, new byte[8]), "test-operation", LongRunningOperationState.Pending,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            null, null, null, null, null, null, null,
            new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero),
            null, null, null, null, null, 0, 0, false, $"Operation {index}", null, 0)).ToArray();

    private static HttpResponseMessage Response(ListPageResult<LongRunningOperationDto> page) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<ListPageResult<LongRunningOperationDto>>(page, true, null),
                ArcanumJsonContext.Default.ApiResponseListPageResultLongRunningOperationDto)),
        };

    private static CliTestResult Run(RecordingHandler handler, string[] args)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new ClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new Secrets());

        CliTestHarness.AddKeyedArcanumResponder(services, "test-key");

        return CliTestHarness.Run(services, args);
    }

    private sealed class Secrets : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class ClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);

            if (Requests.Count > 32) throw new InvalidOperationException("The paging fixture exceeded its finite request budget.");

            return Task.FromResult(response(request, Requests.Count));
        }
    }
}
