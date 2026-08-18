using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>POST /v1/embeddings</c>. Most tests share a single isolated <see cref="ArcanumWebApplicationFactory"/>
/// (created once in <see cref="InitializeAsync"/>) rather than the shared "ApiHost" collection fixture,
/// because every test needs custom embedding feature/integration config and a <see cref="FakeWeaveService"/>
/// substitute; sharing one instance across the whole class (instead of one per test) keeps concurrent
/// Grimoire bootstrap load bounded — xUnit already runs the methods within a class sequentially by
/// default, so a single shared host is safe and avoids the SQLCipher-bootstrap contention a
/// per-test-isolated-host pattern causes at this test count. Two tests (<see cref="PostEmbeddings_OversizedInput_Returns400"/>,
/// <see cref="PostEmbeddings_EmbeddingsDisabled_Returns503"/>) need settings the shared host cannot
/// provide and get their own dedicated (short-lived) factories.
/// </summary>
/// <remarks>
/// Tagged <c>[Collection("ApiHost")]</c> — like every other test class that constructs its own
/// isolated <see cref="ArcanumWebApplicationFactory"/> (see <c>ModelsProvidersEndpointTests</c>,
/// <c>OpenAiV1EndpointTests</c>) — even though this class does not consume the shared collection
/// fixture instance. <c>ApiHostCollection</c> disables parallelization across every class in the
/// collection, which is required here: <c>ArcanumWebApplicationFactory</c> isolates its temp profile
/// via process-wide environment variables (<c>HOME</c>/<c>APPDATA</c>/<c>XDG_DATA_HOME</c>), so two
/// isolated factories racing to start concurrently on different threads can read back the wrong
/// profile mid-construction. Running this class outside the collection reproduces that race as a
/// spurious <c>PidFileService</c> "Another Arcanum process is already running" failure.
/// </remarks>
[Collection("ApiHost")]
public sealed class OpenAiV1EmbeddingsEndpointTests : IAsyncLifetime
{

    private ArcanumWebApplicationFactory _factory = null!;

    private FakeWeaveService _fake = null!;

    public Task InitializeAsync()
    {

        _fake = new FakeWeaveService();

        _factory = CreateEnabledFactory(_fake);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [SkippableFact]
    public async Task PostEmbeddings_WithStringInput_ReturnsFloatEmbedding()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiEmbeddingResponse body = await PostAndReadAsync(client, """{"model":"test-embed","input":"hello world"}""");

        Assert.Equal("list", body.ObjectKind);

        Assert.Equal("test-embed", body.Model);

        OpenAiEmbeddingData entry = Assert.Single(body.Data);

        Assert.Equal("embedding", entry.ObjectKind);

        Assert.Equal(0, entry.Index);

        Assert.NotNull(entry.Embedding.Values);

        Assert.Equal(_fake.Dimensions, entry.Embedding.Values!.Length);

        Assert.Null(entry.Embedding.Base64);

        Assert.True(body.Usage.PromptTokens > 0);

        Assert.Equal(body.Usage.PromptTokens, body.Usage.TotalTokens);

    }

    [SkippableFact]
    public async Task PostEmbeddings_WithArrayInput_ReturnsOneEmbeddingPerInputInOrder()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiEmbeddingResponse body = await PostAndReadAsync(client, """{"input":["alpha","beta","gamma"]}""");

        Assert.Equal(3, body.Data.Count);

        for (int i = 0; i < 3; i++)
        {

            Assert.Equal(i, body.Data[i].Index);

        }

        // Distinct inputs must not collide onto the same vector.
        Assert.NotEqual(body.Data[0].Embedding.Values, body.Data[1].Embedding.Values);

    }

    [SkippableFact]
    public async Task PostEmbeddings_WithIdempotencyKey_SecondRequestReplaysWithoutReExecuting()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        int before = _fake.EmbedCallCount + _fake.EmbedBatchCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = _factory.CreateAuthenticatedClient();

        const string payload = """{"model":"test-embed","input":"idempotent embedding request"}""";

        async Task<HttpResponseMessage> SendAsync()
        {

            HttpRequestMessage req = new(HttpMethod.Post, "/v1/embeddings")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            req.Headers.Add("Idempotency-Key", key);

            return await client.SendAsync(req);

        }

        HttpResponseMessage firstResponse = await SendAsync();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        string firstBody = await firstResponse.Content.ReadAsStringAsync();

        Assert.Equal(before + 1, _fake.EmbedCallCount + _fake.EmbedBatchCallCount);

        HttpResponseMessage secondResponse = await SendAsync();

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        string secondBody = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(firstBody, secondBody);

        Assert.Equal(before + 1, _fake.EmbedCallCount + _fake.EmbedBatchCallCount);

    }

    [SkippableFact]
    public async Task PostEmbeddings_OmittedModel_UsesConfiguredDefault()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiEmbeddingResponse body = await PostAndReadAsync(client, """{"input":"no model specified"}""");

        Assert.Equal("test-embed", body.Model);

    }

    [SkippableFact]
    public async Task PostEmbeddings_MismatchedModel_Returns404ModelNotFound()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent("""{"model":"text-embedding-3-small","input":"hello"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(error);

        Assert.Equal("model_not_found", error.Error.Code);

    }

    [SkippableFact]
    public async Task PostEmbeddings_Base64EncodingFormat_DecodesToSameVectorAsFloat()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiEmbeddingResponse floatBody = await PostAndReadAsync(client, """{"input":"same text for base64 roundtrip"}""");

        OpenAiEmbeddingResponse base64Body = await PostAndReadAsync(
            client,
            """{"input":"same text for base64 roundtrip","encoding_format":"base64"}""");

        OpenAiEmbeddingData base64Entry = Assert.Single(base64Body.Data);

        Assert.Null(base64Entry.Embedding.Values);

        Assert.NotNull(base64Entry.Embedding.Base64);

        float[] decoded = EmbeddingBlobCodec.Decode(Convert.FromBase64String(base64Entry.Embedding.Base64!));

        Assert.Equal(floatBody.Data[0].Embedding.Values, decoded);

    }

    [SkippableFact]
    public async Task PostEmbeddings_InvalidEncodingFormat_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent("""{"input":"hello","encoding_format":"hex"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.Equal("invalid_value", error!.Error.Code);

    }

    [SkippableFact]
    public async Task PostEmbeddings_TokenArrayInput_DecodesAndEmbeds()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        // A small, arbitrary sequence of o200k_base token ids — the exact decoded text does not
        // matter for this test, only that pre-tokenized input is accepted and produces one
        // embedding (mirrors OpenAI's int[] input shape for a single pre-tokenized document).
        OpenAiEmbeddingResponse body = await PostAndReadAsync(client, """{"input":[15339,1917]}""");

        Assert.Single(body.Data);

    }

    [SkippableFact]
    public async Task PostEmbeddings_TokenArrayOfArrays_ProducesOneEmbeddingPerArray()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiEmbeddingResponse body = await PostAndReadAsync(client, """{"input":[[15339],[1917,11]]}""");

        Assert.Equal(2, body.Data.Count);

    }

    [SkippableFact]
    public async Task PostEmbeddings_EmptyInput_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent("""{"input":""}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.Equal("invalid_value", error!.Error.Code);

    }

    [SkippableFact]
    public async Task PostEmbeddings_MissingInput_Returns400MissingRequiredParameter()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.Equal("missing_required_parameter", error!.Error.Code);

    }

    [SkippableTheory]
    [InlineData("""{"input":""")]
    [InlineData("""{"input":42}""")]
    [InlineData("""{"input":["a",1]}""")]
    [InlineData("""{"dimensions":"512","input":"hi"}""")]
    public async Task PostEmbeddings_UnparsableBody_Returns400WithTheOpenAiErrorEnvelope(string payload)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // §8.23: every /v1 failure answers with the OpenAI error envelope, never a body-less
        // framework 400 the SDK cannot read a message, type or code out of.
        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(error);

        Assert.Equal("invalid_request_error", error!.Error.Type);

        Assert.Equal("invalid_json", error.Error.Code);

        Assert.False(string.IsNullOrWhiteSpace(error.Error.Message));

    }

    [SkippableFact]
    public async Task PostEmbeddings_NonJsonContentType_Returns415WithTheOpenAiErrorEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent("""{"input":"hi"}""", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(error);

        Assert.Equal("unsupported_media_type", error!.Error.Code);

    }

    [SkippableFact]
    public async Task PostEmbeddings_LongInputExceedingChunkSize_MeanPoolsIntoUnitVector()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        // Comfortably over the default Arcanum:Embeddings:ChunkSizeChars (1,000), so the endpoint
        // routes this through IWeaveService.ChunkAsync + mean-pooling instead of a single EmbedBatchAsync call.
        string longText = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 30));

        OpenAiEmbeddingRequest request = new(Model: null, Input: new OpenAiEmbeddingInput { Strings = [longText] });

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.OpenAiEmbeddingRequest);

        OpenAiEmbeddingResponse body = await PostAndReadAsync(client, payload);

        OpenAiEmbeddingData entry = Assert.Single(body.Data);

        Assert.NotNull(entry.Embedding.Values);

        // Mean-pooled + L2-renormalized, so the resulting vector should have (approximately) unit norm.
        double normSquared = entry.Embedding.Values!.Sum(v => (double)v * v);

        Assert.InRange(Math.Sqrt(normSquared), 0.99, 1.01);

    }

    [SkippableFact]
    public async Task PostEmbeddings_ProviderReturnsFewerVectorsThanInputs_Returns503()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // An OpenAI-compatible embedding server (Ollama, vLLM, a proxy) can answer a batch with fewer
        // `data` entries than inputs, and Microsoft.Extensions.AI does not enforce the 1:1 contract —
        // so the endpoint must reject the short batch as a sanitized 503 rather than indexing off the
        // end of the array and surfacing an unhandled 500.
        _fake.BatchVectorLimit = 1;

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent("""{"input":["alpha","beta","gamma"]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.Equal("embedding_provider_unavailable", error!.Error.Code);

    }

    [SkippableFact]
    public async Task PostEmbeddings_ProviderReturnsRaggedVectorsForShortInputs_Returns503()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // The short-input path never throws on ragged widths — it just emits one response whose
        // data[] rows have different vector lengths, which violates the OpenAI embeddings contract
        // silently. Same provider fault, same sanitized 503.
        _fake.TrailingVectorDimensionDelta = 2;

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent("""{"input":["alpha","beta","gamma"]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.Equal("embedding_provider_unavailable", error!.Error.Code);

    }

    [SkippableTheory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task PostEmbeddings_ProviderReturnsRaggedVectorsForLongInput_Returns503(int trailingDelta)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // WeaveService issues one round trip per BatchSize sub-batch, so a load-balanced or
        // mid-rollout provider pool can answer one long input with vectors of two different widths
        // while every individual HTTP response is well-formed. A narrower trailing vector reads off
        // the end of the array (500); a wider one is silently truncated and mean-pooled into a
        // plausible-looking but wrong unit vector (200). Both must be the same sanitized 503 the
        // count-mismatch guard beside it produces.
        _fake.TrailingVectorDimensionDelta = trailingDelta;

        HttpClient client = _factory.CreateAuthenticatedClient();

        string longText = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 30));

        OpenAiEmbeddingRequest request = new(Model: null, Input: new OpenAiEmbeddingInput { Strings = [longText] });

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent(
                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.OpenAiEmbeddingRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        OpenAiErrorResponse? raggedError = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.Equal("embedding_provider_unavailable", raggedError!.Error.Code);

    }

    [SkippableTheory]
    [InlineData(-2)]
    [InlineData(2)]
    public async Task PostEmbeddings_ProviderWidthChangesBetweenBatches_Returns503(int subsequentBatchDelta)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // The per-batch width guards each pass here: the short-input batch is internally consistent, and
        // so is the long input's chunk batch. The request still ends up ragged, because those are two
        // different round trips and a load-balanced pool mid-rollout can answer them at two model
        // versions — the exact scenario the per-batch guards were written for, one scope up. Without a
        // check across the assembled response this answers 200 with data[] rows of two widths, which is
        // the OpenAI contract violation the guards exist to prevent, so it must be the same sanitized 503.
        _fake.SubsequentBatchDimensionDelta = subsequentBatchDelta;

        HttpClient client = _factory.CreateAuthenticatedClient();

        string longText = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 30));

        OpenAiEmbeddingRequest request = new(
            Model: null,
            Input: new OpenAiEmbeddingInput { Strings = ["alpha", longText] });

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent(
                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.OpenAiEmbeddingRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.Equal("embedding_provider_unavailable", error!.Error.Code);

    }

    /// <summary>
    /// The cross-batch guard must reject only genuine raggedness. A request mixing a short and a long
    /// input against an honest provider spans the same two round trips and must still answer 200 with one
    /// equal-width vector per input, in request order.
    /// </summary>
    [SkippableFact]
    public async Task PostEmbeddings_MixedShortAndLongInputs_ReturnsEqualWidthVectorsPerInput()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string longText = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 30));

        OpenAiEmbeddingRequest request = new(
            Model: null,
            Input: new OpenAiEmbeddingInput { Strings = ["alpha", longText] });

        OpenAiEmbeddingResponse body = await PostAndReadAsync(
            client,
            JsonSerializer.Serialize(request, ArcanumJsonContext.Default.OpenAiEmbeddingRequest));

        Assert.Equal(2, body.Data.Count);

        Assert.Equal([0, 1], body.Data.Select(static entry => entry.Index));

        Assert.Equal(
            _fake.Dimensions,
            Assert.Single(body.Data.Select(static entry => entry.Embedding.Values!.Length).Distinct()));

    }

    [SkippableFact]
    public async Task PostEmbeddings_ProviderReturnsFewerVectorsThanChunksForLongInput_Returns503()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Same provider misbehavior on the long-input path: a short chunk batch must not be
        // mean-pooled (a silently wrong vector), and an empty one must not throw.
        _fake.BatchVectorLimit = 0;

        HttpClient client = _factory.CreateAuthenticatedClient();

        string longText = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 30));

        OpenAiEmbeddingRequest request = new(Model: null, Input: new OpenAiEmbeddingInput { Strings = [longText] });

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent(
                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.OpenAiEmbeddingRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.Equal("embedding_provider_unavailable", error!.Error.Code);

    }

    [SkippableFact]
    public async Task PostEmbeddings_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent("""{"input":"hello"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    [SkippableFact]
    public async Task PostEmbeddings_OversizedInput_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();
        int maxInputChars = ArcanumSettingClamps.EmbeddingsMaxEmbeddingInputChars(
            ArcanumRuntimeDefaults.Embeddings.MaxEmbeddingInputChars);
        string oversizedInput = new('a', maxInputChars + 1);

        OpenAiEmbeddingRequest request = new(Model: null, Input: new OpenAiEmbeddingInput { Strings = [oversizedInput] });

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent(
                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.OpenAiEmbeddingRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.Equal("invalid_value", error!.Error.Code);

    }

    [SkippableFact]
    public async Task PostEmbeddings_EmbeddingsDisabled_Returns503()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Needs a dedicated factory (distinct from the shared one) because it uses the real
        // WeaveService (disabled) rather than the FakeWeaveService substitute.
        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { Embeddings = false },
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent("""{"input":"hello"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.Equal("embedding_provider_unavailable", error!.Error.Code);

    }

    private static ArcanumWebApplicationFactory CreateEnabledFactory(
        FakeWeaveService weaveService) =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { Embeddings = true },
                Integrations = settings.Integrations with
                {
                    Embeddings = settings.Integrations.Embeddings with
                    {
                        Provider = "test",
                        Model = "test-embed",
                    },
                },
            },
            ServiceOverrides = services =>
            {

                services.RemoveAll<IWeaveService>();

                services.AddSingleton<IWeaveService>(weaveService);

            },
        };

    private static async Task<OpenAiEmbeddingResponse> PostAndReadAsync(HttpClient client, string jsonBody)
    {

        HttpResponseMessage response = await client.PostAsync(
            "/v1/embeddings",
            new StringContent(jsonBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiEmbeddingResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiEmbeddingResponse);

        Assert.NotNull(body);

        return body;

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public bool Available { get; set; } = true;

        public int Dimensions { get; set; } = 4;

        public bool IsAvailable => Available;

        /// <summary>Counts <see cref="EmbedAsync"/> calls — lets idempotency-replay tests assert the handler was not re-executed on a cache hit.</summary>
        public int EmbedCallCount { get; private set; }

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {

            EmbedCallCount++;

            return Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(MakeVector(text))));

        }

        /// <summary>Counts <see cref="EmbedBatchAsync"/> calls — see <see cref="EmbedCallCount"/>.</summary>
        public int EmbedBatchCallCount { get; private set; }

        /// <summary>
        /// Caps how many vectors a batch answer carries, reproducing an OpenAI-compatible provider
        /// that returns fewer <c>data</c> entries than inputs. <see cref="int.MaxValue"/> (the
        /// default) keeps the honest one-vector-per-input behavior.
        /// </summary>
        public int BatchVectorLimit { get; set; } = int.MaxValue;

        /// <summary>
        /// Widens or narrows every vector after the first by this many components, reproducing a
        /// load-balanced or mid-rollout provider pool that answers one batch at two model versions.
        /// Zero (the default) keeps every vector the same width.
        /// </summary>
        public int TrailingVectorDimensionDelta { get; set; }

        /// <summary>
        /// Widens or narrows every vector of every batch after the first by this many components, each
        /// batch staying internally consistent. This is the same pool fault as
        /// <see cref="TrailingVectorDimensionDelta"/> seen at request scope rather than batch scope: one
        /// <c>/v1/embeddings</c> request issues one round trip for the short inputs and another per long
        /// input, so a rollout that flips between them answers each batch honestly and the request
        /// ragged. Zero (the default) keeps every batch the same width.
        /// </summary>
        public int SubsequentBatchDimensionDelta { get; set; }

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {

            EmbedBatchCallCount++;

            int batchDimensions = EmbedBatchCallCount == 1
                ? Dimensions
                : Dimensions + SubsequentBatchDimensionDelta;

            int count = Math.Min(texts.Count, BatchVectorLimit);

            Embedding<float>[] result = new Embedding<float>[count];

            for (int i = 0; i < count; i++)
            {

                int width = i == 0 ? batchDimensions : batchDimensions + TrailingVectorDimensionDelta;

                result[i] = new Embedding<float>(MakeVector(texts[i], width));

            }

            return Task.FromResult(Result<Embedding<float>[]>.Success(result));

        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken)
        {

            const int chunkSize = 100;

            List<(string Chunk, int Offset)> chunks = [];

            for (int offset = 0; offset < text.Length; offset += chunkSize)
            {

                int length = Math.Min(chunkSize, text.Length - offset);

                chunks.Add((text.Substring(offset, length), offset));

            }

            return Task.FromResult(Result<(string Chunk, int Offset)[]>.Success(chunks.ToArray()));

        }

        private float[] MakeVector(string text) => MakeVector(text, Dimensions);

        private static float[] MakeVector(string text, int dimensions)
        {

            Random random = new(text.GetHashCode());

            float[] vector = new float[dimensions];

            for (int i = 0; i < dimensions; i++)
            {

                vector[i] = (float)(random.NextDouble() * 2.0 - 1.0);

            }

            return vector;

        }

    }

}
