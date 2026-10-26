using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Security;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WeaveServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddingIdentity_RealFactoryCannotMixLeasesAndLedgerOriginalPricingAcrossBatches(bool changeProvider)
    {
        MutableTestOptionsMonitor<ArcanumSettings> options = new(IdentitySettings());

        using ScriptedEmbeddingHttpFactory http = new();

        http.AfterSend = count =>
        {
            if (count == 1) options.CurrentValue = IdentitySettings(changeProvider ? "provider-beta" : "provider-alpha", changeProvider ? "model-alpha" : "model-beta");
        };

        EmbeddingGeneratorFactory factory = new(http, options);

        RecordingTurnRunWriter writer = new();

        RecordingBudgetReservationService reservations = new();

        int batchSize = ArcanumSettingClamps.EmbeddingsBatchSize(ArcanumRuntimeDefaults.Embeddings.BatchSize);

        string[] inputs = Enumerable.Repeat("aaaa", batchSize + 1).ToArray();

        var result = await CreateService(options.CurrentValue, factory, writer, reservations, options: options).EmbedBatchAsync(inputs, CancellationToken.None);

        string stamps = result.IsSuccess ? string.Join(",", result.Value.Select(vector => (EmbeddingProvenance)vector.AdditionalProperties![EmbeddingProvenance.MetadataKey]!).Select(proof => proof.Provider + "/" + proof.Model).Distinct()) : result.Error.Code;

        string rows = string.Join(",", writer.Operations.Select(row => FormattableString.Invariant($"{row.Provider}/{row.Model}:{row.InputTokens}:{row.ActualCostUsd}")));

        Assert.True(result.IsFailure, FormattableString.Invariant($"Physical SDK sends={http.RequestedModels.Count}; requested models={string.Join(",", http.RequestedModels)}; returned stamps={stamps}; ledger={rows}; original reservation={reservations.LastRequest?.ReservedUsd}"));

        Assert.Single(http.RequestedModels);

        var billed = Assert.Single(writer.Operations);

        Assert.Equal("provider-alpha", billed.Provider);

        Assert.Equal("model-alpha", billed.Model);

        Assert.Equal((decimal)batchSize, billed.ActualCostUsd);

        Assert.Equal((decimal)(batchSize + 1), reservations.LastRequest?.ReservedUsd);

        Assert.Equal((decimal)batchSize, reservations.ReconciledUsd);

        Assert.Equal(1, reservations.ReconcileCount);
    }

    [Fact]
    public async Task EmbeddingIdentity_RealFactoryChangeBeforeFirstLeaseRefusesBeforeAnyPaidGenerate()
    {
        MutableTestOptionsMonitor<ArcanumSettings> options = new(IdentitySettings());

        using ScriptedEmbeddingHttpFactory http = new();

        EmbeddingGeneratorFactory real = new(http, options);

        IEmbeddingGeneratorFactory factory = new BeforeResolveFactory(real, () => options.CurrentValue = IdentitySettings("provider-alpha", "model-beta"));

        RecordingTurnRunWriter writer = new();

        var result = await CreateService(options.CurrentValue, factory, writer, options: options).EmbedAsync("aaaa", CancellationToken.None);

        Assert.True(result.IsFailure, $"SDK sends={http.RequestedModels.Count}; ledger={string.Join(",", writer.Operations.Select(row => row.Model))}");

        Assert.Empty(http.RequestedModels);

        Assert.Empty(writer.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddingIdentity_CancellationAfterResolutionWinsBeforeIdentityRefusalOrPaidGenerate(bool mismatch)
    {
        using CancellationTokenSource caller = new();

        TestCapturingLogger<WeaveService> logger = new();

        FakeEmbeddingGeneratorFactory factory = new()
        {
            Provenance = new("provider-alpha", mismatch ? "model-beta" : "model-alpha"),
            BeforeResolve = caller.Cancel,
        };

        RecordingTurnRunWriter writer = new();

        RecordingBudgetReservationService reservations = new();

        Task<Result<Microsoft.Extensions.AI.Embedding<float>>> running = CreateService(IdentitySettings(), factory, writer, reservations, logger: logger).EmbedAsync("aaaa", caller.Token);

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Equal(caller.Token, failure.CancellationToken);

        Assert.True(running.IsCanceled);

        Assert.Empty(factory.Generator.CallSizes);

        Assert.Empty(writer.Operations);

        Assert.Equal(1, reservations.ReleaseCount);

        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddingIdentity_SameNamedEndpointOrCredentialRotationRetainsExistingPolicyAndPaidCounts(bool credential)
    {
        MutableTestOptionsMonitor<ArcanumSettings> options = new(IdentitySettings());

        using ScriptedEmbeddingHttpFactory http = new();

        RotatingEmbeddingKey keys = new();

        http.AfterSend = count =>
        {
            if (count != 1) return;

            if (credential) keys.Key = "private-rotated-key";
            else options.CurrentValue = options.CurrentValue with
            {
                Providers = [new() { Name = "provider-alpha", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://beta.fixture.invalid/v1", Models = ["model-alpha"] }],
            };
        };

        EmbeddingGeneratorFactory factory = new(http, options, keys);

        RecordingTurnRunWriter writer = new();

        RecordingBudgetReservationService reservations = new();

        int size = ArcanumSettingClamps.EmbeddingsBatchSize(ArcanumRuntimeDefaults.Embeddings.BatchSize);

        var result = await CreateService(options.CurrentValue, factory, writer, reservations, options: options).EmbedBatchAsync(Enumerable.Repeat("aaaa", size + 1).ToArray(), CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(2, http.RequestedModels.Count);

        Assert.All(result.Value, vector => Assert.Equal(new EmbeddingProvenance("provider-alpha", "model-alpha"), vector.AdditionalProperties![EmbeddingProvenance.MetadataKey]));

        Assert.Equal(2, writer.Operations.Count);

        Assert.Equal((decimal)(size + 1), writer.Operations.Sum(row => row.ActualCostUsd));

        Assert.Equal((decimal)(size + 1), reservations.ReconciledUsd);
    }

    [Fact]
    public async Task EmbeddingIdentity_InitialSnapshotSuppliesBothReservationAndBilledPricing()
    {
        ArcanumSettings first = IdentitySettings();

        ArcanumSettings replacement = first with
        {
            Cost = new() { Pricing = new() { DefaultPricing = new() { InputPer1M = 10_000_000m }, ModelPricing = { ["model-alpha"] = new() { InputPer1M = 10_000_000m } } } },
        };

        SwitchingEmbeddingOptions options = new(first, replacement);

        FakeEmbeddingGeneratorFactory factory = new() { Provenance = new("provider-alpha", "model-alpha") };

        RecordingTurnRunWriter writer = new();

        RecordingBudgetReservationService reservations = new();

        var result = await CreateService(first, factory, writer, reservations, options: options).EmbedAsync("aaaa", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(1, options.Reads);

        Assert.Equal(1m, reservations.LastRequest?.ReservedUsd);

        Assert.Equal(1m, Assert.Single(writer.Operations).ActualCostUsd);
    }

    private sealed class SwitchingEmbeddingOptions(ArcanumSettings first, ArcanumSettings replacement) : IOptionsMonitor<ArcanumSettings>
    {
        internal int Reads { get; private set; }
        public ArcanumSettings CurrentValue => ++Reads == 1 ? first : replacement;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }

    private sealed class RotatingEmbeddingKey : IProviderApiKeyResolver
    {
        internal string Key { get; set; } = "private-original-key";

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(Key);
    }

    private static ArcanumSettings IdentitySettings(string provider = "provider-alpha", string model = "model-alpha") => DiagnosticSettings(provider, model) with
    {
        Providers =
        [
            new() { Name = "provider-alpha", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://alpha.fixture.invalid/v1", Models = ["model-alpha", "model-beta"] },
            new() { Name = "provider-beta", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://beta.fixture.invalid/v1", Models = ["model-alpha", "model-beta"] },
        ],
        Cost = new()
        {
            Pricing = new()
            {
                DefaultPricing = new() { InputPer1M = 1_000_000m },
                ModelPricing =
                {
                    ["model-alpha"] = new() { InputPer1M = 1_000_000m },
                    ["model-beta"] = new() { InputPer1M = 10_000_000m },
                },
            },
        },
    };

    private sealed class BeforeResolveFactory(IEmbeddingGeneratorFactory inner, Action beforeResolve) : IEmbeddingGeneratorFactory
    {
        public Task<EmbeddingGeneratorLease> ResolveGeneratorAsync(CancellationToken cancellationToken)
        {
            beforeResolve();

            return inner.ResolveGeneratorAsync(cancellationToken);
        }
    }

    private sealed class ScriptedEmbeddingHttpFactory : IHttpClientFactory, IDisposable
    {
        private readonly List<HttpClient> _clients = [];

        internal Action<int>? AfterSend { get; set; }
        internal List<string> RequestedModels { get; } = [];

        public HttpClient CreateClient(string name)
        {
            HttpClient client = new(new EmbeddingResponseHandler(this));

            _clients.Add(client);

            return client;
        }

        public void Dispose()
        {
            foreach (HttpClient client in _clients) client.Dispose();
        }

        private sealed class EmbeddingResponseHandler(ScriptedEmbeddingHttpFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));

                string model = body.RootElement.GetProperty("model").GetString()!;

                int count = body.RootElement.GetProperty("input").GetArrayLength();

                owner.RequestedModels.Add(model);

                owner.AfterSend?.Invoke(owner.RequestedModels.Count);

                string vector = model == "model-beta" || request.RequestUri!.Host.StartsWith("beta", StringComparison.Ordinal) ? "[0,1,0]" : "[1,0,0]";

                string data = string.Join(",", Enumerable.Range(0, count).Select(index => FormattableString.Invariant($"{{\"object\":\"embedding\",\"embedding\":{vector},\"index\":{index}}}")));

                string json = FormattableString.Invariant($"{{\"object\":\"list\",\"data\":[{data}],\"model\":\"{model}\",\"usage\":{{\"prompt_tokens\":{count},\"total_tokens\":{count}}}}}");

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
        }
    }
}
