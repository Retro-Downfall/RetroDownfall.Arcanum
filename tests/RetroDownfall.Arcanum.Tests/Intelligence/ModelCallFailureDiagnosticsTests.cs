using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ModelCallFailureDiagnosticsTests
{
    [Theory]
    [InlineData(false, 404, true)]
    [InlineData(false, 429, true)]
    [InlineData(false, 503, false)]
    [InlineData(true, 404, true)]
    [InlineData(true, 429, true)]
    [InlineData(true, 503, false)]
    public async Task Failed_calls_correlate_configured_destination_and_structured_status_without_private_content(bool campaign, int status, bool sdk)
    {
        Exception failure = sdk
            ? new ClientResultException("private-provider-body", new PrivateResponse(status), new IOException("private-inner-credential"))
            : new HttpRequestException("private-provider-body", new IOException("private-inner-credential"), (HttpStatusCode)status);

        TestLogEntry entry = await FailAsync(campaign, failure);

        Dictionary<string, object?> fields = entry.Properties!.ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.Equal("configured-provider", fields["Provider"]);

        Assert.Equal("configured-model", fields["Model"]);

        Assert.Equal(status, fields["HttpStatus"]);

        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(fields["ModelCallId"])));

        Assert.Equal(campaign ? ModelCallPurpose.MainInference : ModelCallPurpose.LexiconExtraction, fields["Purpose"]);

        Assert.Contains(failure.GetType().Name, Assert.IsType<string>(fields["ExceptionType"]), StringComparison.Ordinal);

        AssertSafe(entry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_failures_record_unknown_status_without_losing_destination_or_inspecting_message(bool campaign)
    {
        TestLogEntry entry = await FailAsync(campaign, new IOException("private-provider-body HTTP404 https://private-endpoint/?key=private-credential"));

        Dictionary<string, object?> fields = entry.Properties!.ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.Equal("configured-provider", fields["Provider"]);

        Assert.Equal("configured-model", fields["Model"]);

        Assert.Null(fields["HttpStatus"]);

        AssertSafe(entry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configured_log_identifiers_are_bounded_and_cannot_inject_control_or_Unicode_lines(bool campaign)
    {
        string provider = "provider\r\n\t\u2028\u2029" + new string('p', 200);

        string model = "model\0\u001b\u2028\u2029" + new string('m', 200);

        TestLogEntry entry = await FailAsync(campaign, new IOException("private-provider-body"), provider, model);

        Dictionary<string, object?> fields = entry.Properties!.ToDictionary(pair => pair.Key, pair => pair.Value);

        foreach (string key in new[] { "Provider", "Model" })
        {
            string value = Assert.IsType<string>(fields[key]);

            Assert.InRange(value.Length, 1, 128);

            Assert.DoesNotContain(value, character => char.IsControl(character) || character is '\u2028' or '\u2029');
        }

        AssertSafe(entry);
    }

    [Fact]
    public async Task Missing_context_is_explicitly_unknown_and_does_not_log_caller_model_options()
    {
        TestCapturingLogger<ModelCallExecutor> logger = new();

        ModelCallOutcome result = await new ModelCallExecutor(logger: logger).ExecuteBufferedAsync(new FailingClient(new IOException("private-provider-body")),
            [new(ChatRole.User, "private-prompt")], new() { ModelId = "private-caller-model" }, UnrestrictedTurnBudget.Instance,
            ModelCallPurpose.LexiconExtraction, CancellationToken.None);

        Assert.True(result.IsFailure);

        TestLogEntry entry = Assert.Single(logger.Entries);

        Dictionary<string, object?> fields = entry.Properties!.ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.Equal("<unknown>", fields["Provider"]);

        Assert.Equal("<unknown>", fields["Model"]);

        Assert.Null(fields["HttpStatus"]);

        AssertSafe(entry);
    }

    [Fact]
    public async Task Owned_cancellation_remains_cancellation_without_provider_failure_log()
    {
        using CancellationTokenSource caller = new();

        TestCapturingLogger<ModelCallExecutor> logger = new();

        FailingClient client = new(new OperationCanceledException("private-cancellation"), caller.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelCallExecutor(logger: logger).ExecuteBufferedAsync(client,
            [new(ChatRole.User, "private-prompt")], new(), UnrestrictedTurnBudget.Instance, ModelCallPurpose.LexiconExtraction, caller.Token));

        Assert.Equal(1, client.Calls);

        Assert.Empty(logger.Entries);
    }

    private static async Task<TestLogEntry> FailAsync(bool campaign, Exception failure, string name = "configured-provider", string model = "configured-model")
    {
        ProviderSettings provider = new() { Name = name, Type = AiProviderKind.OpenAICompatible, Endpoint = "https://private-endpoint/?key=private-credential", Models = [new ModelEntry(model)], ContextWindowLimit = 128_000 };

        ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        TestCapturingLogger<ModelCallExecutor> logger = new();

        FailingClient client = new(failure);

        ModelCallExecutor executor = new(estimator, settings: null, logger: logger);

        Error error;

        if (campaign)
        {
            CampaignMaintenancePayload payload = new("private-system", "private-prompt");

            Result<CampaignMaintenanceCallResult> result = await executor.ExecuteCampaignMaintenanceAsync(client, payload, UnrestrictedTurnBudget.Instance,
                _ => Task.CompletedTask, CancellationToken.None, new(provider, model, 2048, 0, estimator.EstimateCampaignMaintenance(provider, model, payload)));

            Assert.True(result.IsFailure);

            error = result.Error;
        }
        else
        {
            ModelCallOutcome result = await executor.ExecuteBufferedAsync(client, [new(ChatRole.User, "private-prompt")], new(),
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.LexiconExtraction, CancellationToken.None, new(provider, model, 2048, 0));

            Assert.True(result.IsFailure);

            Assert.Same(failure, result.Failure.Cause);

            error = result.Error;
        }

        Assert.Equal(1, client.Calls);

        Assert.Equal(ErrorCodes.Hub.Error, error.Code);

        Assert.DoesNotContain("private-", error.Message, StringComparison.Ordinal);

        return Assert.Single(logger.Entries);
    }

    private static void AssertSafe(TestLogEntry entry)
    {
        Assert.Equal(LogLevel.Error, entry.Level);

        Assert.Null(entry.Exception);

        Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("https://", entry.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(entry.Properties ?? [], pair => pair.Value is Exception);

        Assert.DoesNotContain("private-", string.Join(" ", (entry.Properties ?? []).Select(pair => pair.Value)), StringComparison.Ordinal);
    }

    private sealed class FailingClient(Exception failure, Action? onSend = null) : IChatClient
    {
        internal int Calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;

            onSend?.Invoke();

            throw failure;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class PrivateResponse(int status) : PipelineResponse
    {
        public override int Status => status;

        public override string ReasonPhrase => "private-reason";

        protected override PipelineResponseHeaders HeadersCore { get; } = new PrivateHeaders();

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => BinaryData.FromString("private-provider-body");

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Content);

        public override void Dispose() { }
    }

    private sealed class PrivateHeaders : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, [NotNullWhen(true)] out string? value) { value = "private-header"; return true; }

        public override bool TryGetValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values) { values = ["private-header"]; return true; }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() { yield return new("Authorization", "private-header"); }
    }
}
