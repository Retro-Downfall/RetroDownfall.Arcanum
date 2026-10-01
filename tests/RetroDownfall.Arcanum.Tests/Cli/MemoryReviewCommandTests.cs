using System.CommandLine;
using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Commands.Tower;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
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
            new FixedConfirmation(confirmed: false),
            Options.Create(new ArcanumSettings()));

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

    /// <summary>
    /// A correction that re-creates erased content or an erased key releases its erasure fingerprint,
    /// which is the unsafe direction, so the operator is told which items will release before the
    /// question — on the diagnostic stream — and which did after the apply; an item the host could not
    /// check says so both times, and an item that releases nothing adds no line.
    /// </summary>
    [Theory]
    [InlineData("saga")]
    [InlineData("covenant")]
    public async Task Review_apply_names_each_item_that_will_release_before_the_question_and_each_that_did_after(string store)
    {
        MemoryReviewStore reviewStore = store == "saga" ? MemoryReviewStore.Saga : MemoryReviewStore.Covenant;

        Guid requestId = Guid.NewGuid();

        MemoryReviewBulkPlanDto plan = new(
            reviewStore,
            requestId,
            MemoryReviewAction.Correct,
            [
                new MemoryReviewBulkPlanItemDto(1, "memory-1", "version-1", true, "Operator", "session-1", "Global", ReleasesErasureFingerprint: true),
                new MemoryReviewBulkPlanItemDto(2, "memory-2", "version-2", true, "Operator", "session-1", "Global", ReleasesErasureFingerprint: false),
                new MemoryReviewBulkPlanItemDto(3, "memory-3", "version-3", true, "Operator", "session-1", "Global", ReleasesErasureFingerprint: null),
            ],
            DateTimeOffset.Parse("2026-09-28T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-28T12:05:00Z"),
            "prepared-plan");

        MemoryReviewBulkResultDto applied = new(
            reviewStore,
            requestId,
            MemoryReviewAction.Correct,
            [
                new MemoryReviewBulkItemResultDto(1, "memory-1", "version-1", "Corrected", "version-1b", ReleasedErasureFingerprint: true),
                new MemoryReviewBulkItemResultDto(2, "memory-2", "version-2", "Corrected", "version-2b", ReleasedErasureFingerprint: false),
                new MemoryReviewBulkItemResultDto(3, "memory-3", "version-3", "Corrected", "version-3b", ReleasedErasureFingerprint: null),
            ],
            ReviewedThroughEventSequence: 3,
            Replayed: false);

        (int exitCode, string output, string error, SnapshotConfirmation confirmation) = await ApplyCorrectionAsync(store, plan, applied, json: false);

        Assert.True(exitCode == (int)CliExitCode.Success, error);

        Assert.Contains(
            "Event 1 (memory-1) releases an erasure fingerprint: extraction or agents may write this again in its scope.",
            confirmation.ErrorBefore,
            StringComparison.Ordinal);

        Assert.Contains(
            "Event 3 (memory-3): Erasure fingerprints could not be checked; run 'arcanum memory erasure status'.",
            confirmation.ErrorBefore,
            StringComparison.Ordinal);

        // Before the question, on the diagnostic stream only.
        Assert.DoesNotContain("releases an erasure fingerprint", confirmation.OutputBefore, StringComparison.Ordinal);

        Assert.DoesNotContain("could not be checked", confirmation.OutputBefore, StringComparison.Ordinal);

        Assert.Contains("Released an erasure fingerprint for event 1 (memory-1).", output, StringComparison.Ordinal);

        Assert.Contains(
            "Event 3 (memory-3): Erasure fingerprints could not be checked; run 'arcanum memory erasure status'.",
            output,
            StringComparison.Ordinal);

        Assert.DoesNotContain("(memory-2)", output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_apply_prints_no_release_line_when_no_item_releases()
    {
        Guid requestId = Guid.NewGuid();

        MemoryReviewBulkPlanDto plan = new(
            MemoryReviewStore.Saga,
            requestId,
            MemoryReviewAction.Correct,
            [new MemoryReviewBulkPlanItemDto(1, "memory-1", "version-1", true, "Operator", "session-1", "Global", ReleasesErasureFingerprint: false)],
            DateTimeOffset.Parse("2026-09-28T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-28T12:05:00Z"),
            "prepared-plan");

        MemoryReviewBulkResultDto applied = new(
            MemoryReviewStore.Saga,
            requestId,
            MemoryReviewAction.Correct,
            [new MemoryReviewBulkItemResultDto(1, "memory-1", "version-1", "Corrected", "version-1b", ReleasedErasureFingerprint: false)],
            ReviewedThroughEventSequence: 1,
            Replayed: false);

        (int exitCode, string output, string error, _) = await ApplyCorrectionAsync("saga", plan, applied, json: false);

        Assert.True(exitCode == (int)CliExitCode.Success, error);

        Assert.DoesNotContain("erasure fingerprint", output + error, StringComparison.Ordinal);

        Assert.DoesNotContain("could not be checked", output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_review_apply_keeps_the_release_flag_and_writes_one_document()
    {
        Guid requestId = Guid.NewGuid();

        MemoryReviewBulkPlanDto plan = new(
            MemoryReviewStore.Covenant,
            requestId,
            MemoryReviewAction.Correct,
            [new MemoryReviewBulkPlanItemDto(1, "memory-1", "version-1", true, "Operator", "session-1", "Global", ReleasesErasureFingerprint: true)],
            DateTimeOffset.Parse("2026-09-28T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-28T12:05:00Z"),
            "prepared-plan");

        MemoryReviewBulkResultDto applied = new(
            MemoryReviewStore.Covenant,
            requestId,
            MemoryReviewAction.Correct,
            [new MemoryReviewBulkItemResultDto(1, "memory-1", "version-1", "Corrected", "version-1b", ReleasedErasureFingerprint: true)],
            ReviewedThroughEventSequence: 1,
            Replayed: false);

        (int exitCode, string output, string error, _) = await ApplyCorrectionAsync("covenant", plan, applied, json: true);

        Assert.True(exitCode == (int)CliExitCode.Success, error);

        using JsonDocument document = JsonDocument.Parse(output);

        Assert.True(document.RootElement.GetProperty("items")[0].GetProperty("releasedErasureFingerprint").GetBoolean());

        // The warning is still given before the question, on the diagnostic stream.
        Assert.Contains("Event 1 (memory-1) releases an erasure fingerprint", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shipped prompt treats <c>--json</c> as non-interactive by declaration, so a structured review
    /// apply without <c>--yes</c> is refused with exit 2 and never applies — it never reaches a decline.
    /// </summary>
    [Fact]
    public async Task A_json_review_apply_without_yes_is_refused_by_the_shipped_prompt_and_never_applies()
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

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.AddSingleton<IHttpClientFactory>(new SingleHandlerFactory(handler));

        services.AddSingleton<ISecretStore>(new FixedSecretStore());

        CliTestHarness.AddKeyedArcanumResponder(services, FixedSecretStore.Key);

        string requestPath = Path.Combine(Path.GetTempPath(), $"arcanum-review-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(
                requestPath,
                JsonSerializer.Serialize(
                    new SagaReviewBulkPrepareRequest(
                        requestId,
                        SagaMemoryScopeKind.Global,
                        CampaignId: null,
                        MemoryReviewAction.Confirm,
                        [new SagaReviewDecision("observation", ReplacementContent: null)]),
                    ArcanumJsonContext.Default.SagaReviewBulkPrepareRequest));

            CliTestResult result = await CliTestHarness.RunAsync(services, ["memory", "saga", "review", "apply", "--file", requestPath, "--json"]);

            Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

            Assert.Equal(["/api/memory/saga/review/prepare"], handler.Paths);

            using JsonDocument document = JsonDocument.Parse(result.Output);

            Assert.Equal((int)CliExitCode.ConfigurationError, document.RootElement.GetProperty("exitCode").GetInt32());

            Assert.False(document.RootElement.TryGetProperty("cancelled", out _));
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

    /// <summary>
    /// Runs one correction review apply through the registered command tree, approved by a prompt that
    /// snapshots what each stream had received when the question was put, against a host that answers
    /// prepare with <paramref name="plan"/> and apply with <paramref name="applied"/>.
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error, SnapshotConfirmation Confirmation)> ApplyCorrectionAsync(
        string store,
        MemoryReviewBulkPlanDto plan,
        MemoryReviewBulkResultDto applied,
        bool json)
    {
        StreamRecorder streams = new();

        SnapshotConfirmation confirmation = new(streams);

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.AddSingleton<IHttpClientFactory>(new SingleHandlerFactory(new RecordingHandler(plan) { Applied = applied }));

        services.AddSingleton<ISecretStore>(new FixedSecretStore());

        CliTestHarness.AddKeyedArcanumResponder(services, FixedSecretStore.Key);

        services.AddSingleton<IConfirmationPrompt>(confirmation);

        services.AddSingleton<IConsoleDispatcher>(provider => new RecordingDispatcher(
            new ConsoleDispatcher(provider.GetRequiredService<ICliInvocationContext>()),
            streams));

        string requestPath = Path.Combine(Path.GetTempPath(), $"arcanum-review-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(
                requestPath,
                store == "saga"
                    ? JsonSerializer.Serialize(
                        new SagaReviewBulkPrepareRequest(
                            plan.RequestId,
                            SagaMemoryScopeKind.Global,
                            CampaignId: null,
                            MemoryReviewAction.Correct,
                            [.. plan.Items.Select(static item => new SagaReviewDecision($"observation-{item.EventSequence}", "replacement"))]),
                        ArcanumJsonContext.Default.SagaReviewBulkPrepareRequest)
                    : JsonSerializer.Serialize(
                        new CovenantReviewBulkPrepareRequest(
                            plan.RequestId,
                            CovenantScope.Global,
                            CampaignId: null,
                            CovenantLane.Confirmed,
                            MemoryReviewAction.Correct,
                            [.. plan.Items.Select(static item => new CovenantReviewDecision($"observation-{item.EventSequence}", "replacement"))]),
                        ArcanumJsonContext.Default.CovenantReviewBulkPrepareRequest));

            CliTestResult result = await CliTestHarness.RunAsync(
                services,
                ["memory", store, "review", "apply", "--file", requestPath, .. json ? new[] { "--json" } : []]);

            return (result.ExitCode, result.Output, result.Error, confirmation);
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
            new FixedConfirmation(confirmed: false),
            Options.Create(new ArcanumSettings()));
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

    /// <summary>Approves, snapshotting what each stream had received when the question was put.</summary>
    private sealed class SnapshotConfirmation(StreamRecorder streams) : IConfirmationPrompt
    {
        public string OutputBefore { get; private set; } = "";

        public string ErrorBefore { get; private set; } = "";

        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken)
        {
            OutputBefore = streams.Payload.ToString();

            ErrorBefore = streams.Diagnostic.ToString();

            return Task.FromResult(true);
        }
    }

    /// <summary>What the command wrote to each stream, in order.</summary>
    private sealed class StreamRecorder
    {
        public System.Text.StringBuilder Payload { get; } = new();

        public System.Text.StringBuilder Diagnostic { get; } = new();
    }

    /// <summary>Writes through the production dispatcher, recording each line by the stream it took.</summary>
    private sealed class RecordingDispatcher(IConsoleDispatcher inner, StreamRecorder streams) : IConsoleDispatcher
    {
        public void WritePayload(string value)
        {
            streams.Payload.Append(value).Append('\n');

            inner.WritePayload(value);
        }

        public void WriteDiagnostic(string value)
        {
            streams.Diagnostic.Append(value).Append('\n');

            inner.WriteDiagnostic(value);
        }

        public void WriteVerbose(string value) => inner.WriteVerbose(value);

        public void WriteJson<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) => inner.WriteJson(value, typeInfo);

        public void WriteJson(JsonElement value) => inner.WriteJson(value);

        public void BeginJsonStream() => inner.BeginJsonStream();
    }

    private sealed class RecordingHandler(MemoryReviewBulkPlanDto plan) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        /// <summary>What an apply answers with; without one, every route answers with the plan.</summary>
        public MemoryReviewBulkResultDto? Applied { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);

            byte[] json = Applied is { } applied && request.RequestUri.AbsolutePath.EndsWith("/apply", StringComparison.Ordinal)
                ? JsonSerializer.SerializeToUtf8Bytes(
                    ApiResponse<MemoryReviewBulkResultDto>.FromResult(Result<MemoryReviewBulkResultDto>.Success(applied)),
                    ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto)
                : JsonSerializer.SerializeToUtf8Bytes(
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
