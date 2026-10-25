using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class OperationCommandTests
{
    private const string OperationId = "3f2c9a4e-7b1d-4c58-9e0a-5d6f1a2b3c4d";

    private const string Summary = "The durable retention operation requires operator review before further maintenance can proceed.";

    [Theory]
    [InlineData(80, null, "Summary, Kind")]
    [InlineData(80, "--plain", "Summary, Kind")]
    [InlineData(80, "--json", "Summary, Kind")]
    [InlineData(120, null, "Summary")]
    [InlineData(120, "--plain", "Summary")]
    [InlineData(120, "--json", "Summary")]
    [InlineData(240, null, null)]
    [InlineData(240, "--plain", null)]
    [InlineData(240, "--json", null)]
    public async Task List_keeps_copyable_identifiers_and_full_states_at_the_available_width(
        int width,
        string? outputOption,
        string? hiddenColumns)
    {
        RecordingHandler handler = new([Operation()]);

        string[] args = ["operation", "list", "--kind", LongRunningOperationKinds.DataRetentionMutation, "--state", "ReconciliationRequired"];

        if (outputOption is not null)
        {
            args = [.. args, outputOption];
        }

        CliTestResult result = await RunCommand(handler, args, width);

        Assert.Equal(0, result.ExitCode);

        string output = ReadOutput(result, outputOption);

        string[] lines = output.ReplaceLineEndings("\n").Split('\n');

        string row = Assert.Single(lines, line => line.Contains(OperationId, StringComparison.Ordinal));

        Assert.Contains("ReconciliationRequired", row, StringComparison.Ordinal);

        Assert.DoesNotContain("…", output, StringComparison.Ordinal);

        Assert.All(lines, line => Assert.True(line.Length <= width, $"{line.Length} columns overflow {width}: {line}"));

        Assert.Contains("Attempts", output, StringComparison.Ordinal);

        if (hiddenColumns is null)
        {
            Assert.Equal(string.Empty, result.Error.Trim());

            Assert.Contains(Summary, output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"Not shown at {width} columns: {hiddenColumns}.", result.Error, StringComparison.Ordinal);

            Assert.DoesNotContain("Not shown", output, StringComparison.Ordinal);
        }

        if (width >= 120)
        {
            Assert.Contains(LongRunningOperationKinds.DataRetentionMutation, row, StringComparison.Ordinal);
        }

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/operations", request.RequestUri!.AbsolutePath);

        Assert.Contains("kind=data-retention-mutation", request.RequestUri.Query, StringComparison.Ordinal);

        Assert.Contains("state=ReconciliationRequired", request.RequestUri.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("--plain")]
    [InlineData("--json")]
    public async Task Empty_list_preserves_success_and_the_empty_state(string? outputOption)
    {
        RecordingHandler handler = new([]);

        string[] args = outputOption is null ? ["operation", "list"] : ["operation", "list", outputOption];

        CliTestResult result = await RunCommand(handler, args, 80);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("No durable operations found.", ReadOutput(result, outputOption), StringComparison.Ordinal);

        Assert.Equal(string.Empty, result.Error.Trim());
    }

    [Fact]
    public async Task List_treats_summary_terminal_controls_as_data()
    {
        RecordingHandler handler = new([Operation() with { PublicSummary = "ok\u001b]52;c;QUFBQQ==\u0007title\u001b[2J\u009b" }]);

        CliTestResult result = await RunCommand(handler, ["operation", "list", "--plain"], 240);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("oktitle", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("QUFBQQ", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain('\u001b', result.Output);

        Assert.DoesNotContain('\u0007', result.Output);

        Assert.DoesNotContain('\u009b', result.Output);
    }

    private static LongRunningOperationDto Operation() => new(
        Guid.Parse(OperationId),
        LongRunningOperationKinds.DataRetentionMutation,
        LongRunningOperationState.ReconciliationRequired,
        LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
        null, null, null, null, null, null, null,
        new DateTimeOffset(2026, 10, 24, 15, 30, 0, TimeSpan.Zero),
        null, null, null, null, null,
        2, 1, true, Summary, "Data.ReconciliationFailed", 1);

    private static string ReadOutput(CliTestResult result, string? outputOption)
    {
        if (outputOption != "--json")
        {
            return result.Output;
        }

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(0, document.RootElement.GetProperty("exitCode").GetInt32());

        return document.RootElement.GetProperty("output").GetString()!;
    }

    private static Task<CliTestResult> RunCommand(RecordingHandler handler, string[] args, int width)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore());

        CliTestHarness.AddKeyedArcanumResponder(services, "test-key");

        handler.Width = width;

        return CliTestHarness.RunAsync(services, args);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class FakeHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };
    }

    private sealed class RecordingHandler(LongRunningOperationDto[] rows) : HttpMessageHandler
    {
        public int Width { get; set; } = 80;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The invocation creates its plain/JSON console before calling the host. Set the simulated
            // terminal width on that actual console before the response is rendered.
            AnsiConsole.Profile.Width = Width;

            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<LongRunningOperationDto[]>(rows, true, null),
                ArcanumJsonContext.Default.ApiResponseLongRunningOperationDtoArray);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(json),
            });
        }
    }
}
