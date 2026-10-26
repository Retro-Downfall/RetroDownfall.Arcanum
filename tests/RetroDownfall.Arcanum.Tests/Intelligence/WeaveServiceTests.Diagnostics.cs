using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WeaveServiceTests
{
    [Theory]
    [InlineData(true, 401, "credential", "credentials_rejected")]
    [InlineData(false, 401, "credential", "credentials_rejected")]
    [InlineData(true, 403, "permission", "permission_refused")]
    [InlineData(false, 403, "permission", "permission_refused")]
    [InlineData(true, 404, "not found", "model_not_found")]
    [InlineData(false, 404, "not found", "model_not_found")]
    [InlineData(true, 413, "too large", "request_too_large")]
    [InlineData(false, 413, "too large", "request_too_large")]
    [InlineData(true, 400, "unavailable", "provider_unavailable")]
    [InlineData(false, 400, "unavailable", "provider_unavailable")]
    [InlineData(true, 503, "unavailable", "provider_unavailable")]
    [InlineData(false, 503, "unavailable", "provider_unavailable")]
    public async Task ProviderDiagnostics_RealStatusTypesHaveFixedActionableMessagesAndActualLeaseAttribution(bool sdk, int status, string message, string reason)
    {
        TestCapturingLogger<WeaveService> logger = new();

        FakeEmbeddingGeneratorFactory factory = new() { Provenance = new("provider-alpha", "model-alpha") };

        factory.Generator.ThrowOnGenerate = ProviderFailure(sdk, status);

        var result = await CreateService(DiagnosticSettings(), factory, logger: logger).EmbedAsync("private-input-canary", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Embeddings.ProviderUnavailable, result.Error.Code);

        Assert.Contains(message, result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("arcanum doctor", result.Error.Message);

        Assert.DoesNotContain("private-", result.Error.Message);

        Assert.DoesNotContain("model-alpha", result.Error.Message);

        TestLogEntry warning = Assert.Single(logger.Entries);

        AssertSafeProviderWarning(warning);

        Assert.Equal(reason, DiagnosticProperty(warning, "Reason"));

        Assert.Equal("generate", DiagnosticProperty(warning, "Stage"));

        Assert.Equal(status, DiagnosticProperty(warning, "HttpStatus"));

        Assert.Equal("provider-alpha", DiagnosticProperty(warning, "Provider"));

        Assert.Equal("model-alpha", DiagnosticProperty(warning, "Model"));

        Assert.Equal("provider-alpha", DiagnosticProperty(warning, "ConfiguredProvider"));

        Assert.Equal("model-alpha", DiagnosticProperty(warning, "ConfiguredModel"));

        Assert.Equal("resolved_lease", DiagnosticProperty(warning, "Provenance"));

        Assert.Equal(factory.Generator.ThrowOnGenerate.GetType().FullName, DiagnosticProperty(warning, "ExceptionType"));

        Assert.Equal(1, factory.ResolveCount);

        Assert.Single(factory.Generator.CallSizes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderDiagnostics_ResolutionFailureCannotInventActualLeaseUnderReload(bool sdk)
    {
        TestCapturingLogger<WeaveService> logger = new();

        MutableTestOptionsMonitor<ArcanumSettings> options = new(DiagnosticSettings());

        FakeEmbeddingGeneratorFactory factory = new()
        {
            ResolveException = ProviderFailure(sdk, 401),
            BeforeResolve = () => options.CurrentValue = DiagnosticSettings("provider-beta", "model-beta"),
        };

        var result = await CreateService(options.CurrentValue, factory, logger: logger, options: options).EmbedAsync("private-input-canary", CancellationToken.None);

        Assert.True(result.IsFailure);

        TestLogEntry warning = Assert.Single(logger.Entries);

        AssertSafeProviderWarning(warning);

        Assert.Equal("resolve", DiagnosticProperty(warning, "Stage"));

        Assert.Equal("unknown", DiagnosticProperty(warning, "Provider"));

        Assert.Equal("unknown", DiagnosticProperty(warning, "Model"));

        Assert.Equal("unknown", DiagnosticProperty(warning, "Provenance"));

        Assert.Equal("provider-alpha", DiagnosticProperty(warning, "ConfiguredProvider"));

        Assert.Equal("model-alpha", DiagnosticProperty(warning, "ConfiguredModel"));

        Assert.DoesNotContain("provider-beta", warning.Message);

        Assert.DoesNotContain("model-beta", warning.Message);

        Assert.Empty(factory.Generator.CallSizes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderDiagnostics_GenerationUsesLeaseOrHonestUnknownInsteadOfHotReloadOrProviderMetadata(bool stamped)
    {
        TestCapturingLogger<WeaveService> logger = new();

        MutableTestOptionsMonitor<ArcanumSettings> options = new(DiagnosticSettings());

        FakeEmbeddingGeneratorFactory factory = new() { Provenance = stamped ? new("provider-alpha", "model-alpha") : null };

        factory.Generator.BeforeGenerate = () => options.CurrentValue = DiagnosticSettings("provider-beta", "model-beta");

        factory.Generator.ProviderProvenance = new("private-forged-provider", "private-forged-model");

        factory.Generator.ThrowOnGenerate = new IOException("private-exception HTTP 401 credential-canary", new Exception("private-inner-canary"));

        var result = await CreateService(options.CurrentValue, factory, logger: logger, options: options).EmbedAsync("private-input-canary", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.DoesNotContain("credential", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        TestLogEntry warning = Assert.Single(logger.Entries);

        AssertSafeProviderWarning(warning);

        Assert.Equal("provider_unavailable", DiagnosticProperty(warning, "Reason"));

        Assert.Null(DiagnosticProperty(warning, "HttpStatus"));

        Assert.Equal(stamped ? "provider-alpha" : "unknown", DiagnosticProperty(warning, "Provider"));

        Assert.Equal(stamped ? "model-alpha" : "unknown", DiagnosticProperty(warning, "Model"));

        Assert.Equal(stamped ? "resolved_lease" : "unknown", DiagnosticProperty(warning, "Provenance"));

        Assert.DoesNotContain("beta", warning.Message);
    }

    [Theory]
    [InlineData("/private/path-canary")]
    [InlineData("https://private-endpoint-canary/?key=private-credential-canary")]
    [InlineData("name\nprivate-credential-canary")]
    [InlineData("private-credential-canary@provider")]
    [InlineData("oversized")]
    public async Task ProviderDiagnostics_UnsafeIdentifiersAreRedactedAsAWhole(string identifier)
    {
        if (identifier == "oversized") identifier = new string('x', 129) + "private-tail-canary";

        TestCapturingLogger<WeaveService> logger = new();

        FakeEmbeddingGeneratorFactory factory = new() { Provenance = new(identifier, identifier) };

        factory.Generator.ThrowOnGenerate = ProviderFailure(true, 404);

        var result = await CreateService(DiagnosticSettings(identifier, identifier), factory, logger: logger).EmbedAsync("private-input-canary", CancellationToken.None);

        Assert.True(result.IsFailure);

        TestLogEntry warning = Assert.Single(logger.Entries);

        AssertSafeProviderWarning(warning);

        foreach (string property in new[] { "Provider", "Model", "ConfiguredProvider", "ConfiguredModel" }) Assert.Equal("redacted", DiagnosticProperty(warning, property));

        Assert.True(warning.Message.Length <= 1200);

        Assert.DoesNotContain("private-", result.Error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderDiagnostics_CardinalityMismatchHasSafeActualAttributionAndRetainsPaidBatch(bool matching)
    {
        TestCapturingLogger<WeaveService> logger = new();

        FakeEmbeddingGeneratorFactory factory = new() { Provenance = new("provider-alpha", "model-alpha") };

        factory.Generator.ReturnVectorCount = matching ? 2 : 1;

        RecordingTurnRunWriter writer = new();

        var result = await CreateService(DiagnosticSettings(), factory, writer, logger: logger).EmbedBatchAsync(["private-input-a", "private-input-b"], CancellationToken.None);

        Assert.Equal(matching, result.IsSuccess);

        Assert.Single(writer.Operations);

        if (matching) Assert.Empty(logger.Entries);
        else
        {
            TestLogEntry warning = Assert.Single(logger.Entries);

            AssertSafeProviderWarning(warning);

            Assert.Equal("cardinality_mismatch", DiagnosticProperty(warning, "Reason"));

            Assert.Equal("provider-alpha", DiagnosticProperty(warning, "Provider"));

            Assert.Equal("model-alpha", DiagnosticProperty(warning, "Model"));

            Assert.Equal(1, DiagnosticProperty(warning, "ActualCount"));

            Assert.Equal(2, DiagnosticProperty(warning, "ExpectedCount"));
        }
    }

    [Fact]
    public async Task ProviderDiagnostics_LocalLedgerFailureCannotClaimProviderHttpCredentialsWereRejected()
    {
        TestCapturingLogger<WeaveService> logger = new();

        FakeEmbeddingGeneratorFactory factory = new() { Provenance = new("provider-alpha", "model-alpha") };

        RecordingTurnRunWriter writer = new() { RecordException = ProviderFailure(true, 401) };

        var result = await CreateService(DiagnosticSettings(), factory, writer, logger: logger).EmbedAsync("private-input-canary", CancellationToken.None);

        Assert.True(result.IsFailure);

        TestLogEntry warning = Assert.Single(logger.Entries);

        AssertSafeProviderWarning(warning);

        Assert.Equal("accounting", DiagnosticProperty(warning, "Stage"));

        Assert.Equal("accounting_write_failed", DiagnosticProperty(warning, "Reason"));

        Assert.Null(DiagnosticProperty(warning, "HttpStatus"));

        Assert.Contains("storage", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("credential", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Single(factory.Generator.CallSizes);

        Assert.Empty(writer.Operations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderDiagnostics_CallerCancellationKeepsTokenAndEmitsNoWarning(bool resolving)
    {
        TestCapturingLogger<WeaveService> logger = new();

        using CancellationTokenSource caller = new();

        OperationCanceledException canceled = new("private-cancellation-canary", caller.Token);

        FakeEmbeddingGeneratorFactory factory = new() { Provenance = new("provider-alpha", "model-alpha") };

        if (resolving)
        {
            factory.BeforeResolve = caller.Cancel;

            factory.ResolveException = canceled;
        }
        else
        {
            factory.Generator.BeforeGenerate = caller.Cancel;

            factory.Generator.ThrowOnGenerate = canceled;
        }

        Task<Result<Microsoft.Extensions.AI.Embedding<float>>> running = CreateService(DiagnosticSettings(), factory, logger: logger).EmbedAsync("private-input-canary", caller.Token);

        Assert.Same(canceled, await Assert.ThrowsAsync<OperationCanceledException>(() => running));

        Assert.True(running.IsCanceled);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ProviderDiagnostics_LaterResolutionFailureCannotBorrowPreviousBatchLeaseIdentity()
    {
        TestCapturingLogger<WeaveService> logger = new();

        FakeEmbeddingGeneratorFactory factory = new() { Provenance = new("provider-alpha", "model-alpha") };

        factory.BeforeResolve = () =>
        {
            if (factory.ResolveCount == 2) factory.ResolveException = new IOException("private-next-resolution-canary");
        };

        RecordingTurnRunWriter writer = new();

        int size = ArcanumSettingClamps.EmbeddingsBatchSize(ArcanumRuntimeDefaults.Embeddings.BatchSize);

        var result = await CreateService(DiagnosticSettings(), factory, writer, logger: logger).EmbedBatchAsync(Enumerable.Repeat("aaaa", size + 1).ToArray(), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Single(factory.Generator.CallSizes);

        Assert.Single(writer.Operations);

        TestLogEntry warning = Assert.Single(logger.Entries);

        AssertSafeProviderWarning(warning);

        Assert.Equal("resolve", DiagnosticProperty(warning, "Stage"));

        Assert.Equal("unknown", DiagnosticProperty(warning, "Provider"));

        Assert.Equal("unknown", DiagnosticProperty(warning, "Model"));
    }

    private static ArcanumSettings DiagnosticSettings(string provider = "provider-alpha", string model = "model-alpha") => EnabledSettings() with
    {
        Integrations = new() { Embeddings = new() { Provider = provider, Model = model } },
    };

    private static object? DiagnosticProperty(TestLogEntry entry, string name) => entry.Properties!.Single(property => property.Key == name).Value;

    private static void AssertSafeProviderWarning(TestLogEntry entry)
    {
        Assert.Equal(LogLevel.Warning, entry.Level);

        Assert.Null(entry.Exception);

        Assert.DoesNotContain("private-", entry.Message);

        Assert.DoesNotContain("/private/", entry.Message);

        Assert.DoesNotContain("https://", entry.Message);

        Assert.DoesNotContain("private-", string.Join(" ", (entry.Properties ?? []).Select(property => property.Value)));

        Assert.DoesNotContain(entry.Properties ?? [], property => property.Value is Exception);
    }

    private static Exception ProviderFailure(bool sdk, int status) => sdk
        ? new ClientResultException("private-provider-message-canary", new DiagnosticResponse(status), new IOException("private-inner-credential-canary"))
        : new HttpRequestException("private-provider-message-canary", new IOException("private-inner-credential-canary"), (HttpStatusCode)status);

    private sealed class DiagnosticResponse(int status) : PipelineResponse
    {
        public override int Status => status;

        public override string ReasonPhrase => "private-reason-canary";

        protected override PipelineResponseHeaders HeadersCore { get; } = new DiagnosticHeaders();

        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => BinaryData.FromString("private-response-body-canary");

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Content);

        public override void Dispose() { }
    }

    private sealed class DiagnosticHeaders : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, [NotNullWhen(true)] out string? value)
        {
            value = "private-header-credential-canary";

            return true;
        }

        public override bool TryGetValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = ["private-header-credential-canary"];

            return true;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() { yield return new("Authorization", "private-header-credential-canary"); }
    }
}
