using System.CommandLine;
using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Commands.Tower;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class MemoryReviewCommandTests
{
    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public void Every_durable_store_exposes_review_list_and_apply(string store)
    {
        using ServiceProvider services = Services();

        RootCommand root = CliCommandTree.Build(services, out _);

        Command review = Descend(root, "memory", store, "review");

        Assert.Equal(
            ["apply", "list"],
            review.Subcommands.Select(static command => command.Name).Order(StringComparer.Ordinal));

        Command apply = Descend(review, "apply");

        Assert.Contains(apply.Options, static option => option.Name == "--file" && option.Required);
    }

    [Fact]
    public async Task Declined_json_review_apply_writes_one_cancellation_document_and_never_applies()
    {
        Guid requestId = Guid.NewGuid();

        MemoryReviewBulkPlanDto plan = new(
            MemoryReviewStore.Saga,
            requestId,
            MemoryReviewAction.Confirm,
            [new MemoryReviewBulkPlanItemDto(1, "memory-1", "version-1", true, "AgentExtracted", "session-1", "Global")],
            DateTimeOffset.Parse("2026-09-28T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-28T12:05:00Z"),
            "prepared-plan");

        RecordingHandler handler = new(plan);

        CliInvocationOptions options = new(Json: true, Plain: false, Yes: false);

        StringWriter output = new();

        StringWriter error = new();

        ConsoleDispatcher dispatcher = new(output, error, options);

        ArcanumApiClient apiClient = new(
            new SingleHandlerFactory(handler),
            ArcanumApiCredentialLeaseTestFactory.Create(FixedSecretStore.Key));

        MemoryCommands commands = new(
            apiClient,
            themePalette: null!,
            dispatcher,
            new FixedConfirmation(confirmed: false));

        string requestPath = Path.Combine(Path.GetTempPath(), $"arcanum-review-{Guid.NewGuid():N}.json");

        try
        {
            SagaReviewBulkPrepareRequest request = new(
                requestId,
                SagaMemoryScopeKind.Global,
                CampaignId: null,
                MemoryReviewAction.Confirm,
                [new SagaReviewDecision("observation", ReplacementContent: null)]);

            File.WriteAllText(
                requestPath,
                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.SagaReviewBulkPrepareRequest));

            using IDisposable invocation = CliInvocationContext.Push(options);

            int exitCode = await commands.SagaReviewApply(requestPath, CancellationToken.None);

            Assert.True(
                exitCode == (int)CliExitCode.Success,
                $"Expected a successful cancellation, but stderr contained: {error}");

            using JsonDocument document = JsonDocument.Parse(output.ToString());

            Assert.True(document.RootElement.GetProperty("cancelled").GetBoolean());
            Assert.Equal("Saga", document.RootElement.GetProperty("store").GetString());
            Assert.Equal(requestId, document.RootElement.GetProperty("requestId").GetGuid());
            Assert.Equal("Confirm", document.RootElement.GetProperty("action").GetString());
            Assert.Single(handler.Paths);
            Assert.Equal("/api/memory/saga/review/prepare", handler.Paths[0]);
        }
        finally
        {
            File.Delete(requestPath);
        }
    }

    [Fact]
    public async Task Invalid_json_review_lane_writes_one_error_document()
    {
        CliInvocationOptions options = new(Json: true, Plain: false, Yes: false);

        StringWriter output = new();

        StringWriter error = new();

        MemoryCommands commands = CreateCommands(options, output, error);

        using IDisposable invocation = CliInvocationContext.Push(options);

        int exitCode = await commands.CovenantReviewList(
            campaignId: null,
            lane: "retired",
            limit: 25,
            cursor: null,
            CancellationToken.None);

        AssertJsonInputError(
            output,
            error,
            exitCode,
            "--lane must be confirmed or proposed.");
    }

    [Fact]
    public async Task Malformed_json_review_file_writes_one_error_document()
    {
        CliInvocationOptions options = new(Json: true, Plain: false, Yes: true);

        StringWriter output = new();

        StringWriter error = new();

        MemoryCommands commands = CreateCommands(options, output, error);

        string requestPath = Path.Combine(Path.GetTempPath(), $"arcanum-review-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(requestPath, "{");

            using IDisposable invocation = CliInvocationContext.Push(options);

            int exitCode = await commands.SagaReviewApply(requestPath, CancellationToken.None);

            AssertJsonInputError(
                output,
                error,
                exitCode,
                "Saga review decisions are not valid JSON for this store.");
        }
        finally
        {
            File.Delete(requestPath);
        }
    }

    private static MemoryCommands CreateCommands(
        CliInvocationOptions options,
        StringWriter output,
        StringWriter error)
    {
        MemoryReviewBulkPlanDto unusedPlan = new(
            MemoryReviewStore.Saga,
            Guid.NewGuid(),
            MemoryReviewAction.Confirm,
            [],
            DateTimeOffset.Parse("2026-09-28T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-28T12:05:00Z"),
            "unused-plan");

        ArcanumApiClient apiClient = new(
            new SingleHandlerFactory(new RecordingHandler(unusedPlan)),
            ArcanumApiCredentialLeaseTestFactory.Create(FixedSecretStore.Key));

        return new MemoryCommands(
            apiClient,
            themePalette: null!,
            new ConsoleDispatcher(output, error, options),
            new FixedConfirmation(confirmed: false));
    }

    private static void AssertJsonInputError(
        StringWriter output,
        StringWriter error,
        int exitCode,
        string expectedMessage)
    {
        Assert.Equal((int)CliExitCode.ConfigurationError, exitCode);

        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(expectedMessage, document.RootElement.GetProperty("error").GetString());
        Assert.Equal((int)CliExitCode.ConfigurationError, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains(expectedMessage, error.ToString(), StringComparison.Ordinal);
    }

    private static Command Descend(Command root, params string[] path)
    {
        Command current = root;

        foreach (string name in path)
        {
            current = Assert.Single(
                current.Subcommands,
                candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        }

        return current;
    }

    private static ServiceProvider Services()
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.AddSingleton<ISecretStore>(new EmptySecretStore());

        return services.BuildServiceProvider();
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class FixedConfirmation(bool confirmed) : IConfirmationPrompt
    {
        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken) =>
            Task.FromResult(confirmed);
    }

    private sealed class RecordingHandler(MemoryReviewBulkPlanDto plan) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                ApiResponse<MemoryReviewBulkPlanDto>.FromResult(
                    Result<MemoryReviewBulkPlanDto>.Success(plan)),
                ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(json),
            });
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };
    }

    private sealed class FixedSecretStore : ISecretStore
    {
        public const string Key = "arc_test_0123456789abcdef0123456789abcdef";

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(Key);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(Key));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }
}
