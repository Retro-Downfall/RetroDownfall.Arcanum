using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LongRestEndpointTests
{
    private const string ApplyPath = "/api/memory/saga/long-rest";

    private const string ReceiptPath = "/api/memory/saga/long-rest/receipts/receipt-1";

    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    public static TheoryData<string, HttpStatusCode> ServiceFailures => new()
    {
        { "LongRest.InvalidRequest", HttpStatusCode.BadRequest },

        { "LongRest.StaleInput", HttpStatusCode.Conflict },

        { "LongRest.NotFound", HttpStatusCode.NotFound },

        { "LongRest.MissingClaim", HttpStatusCode.Conflict },

        { "LongRest.Unavailable", HttpStatusCode.ServiceUnavailable },

        { "LongRest.IntegrityFailure", HttpStatusCode.ServiceUnavailable },
    };

    [SkippableTheory]
    [InlineData("POST", ApplyPath)]
    [InlineData("GET", ReceiptPath)]
    public async Task Long_rest_requires_the_host_api_key(string method, string path)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = WithService(new ReceiptService());

        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(new HttpMethod(method), path);

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertRefusalAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Auth.Unauthorized);
    }

    [SkippableFact]
    public async Task Malformed_long_rest_json_returns_a_typed_refusal_envelope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = WithService(new ReceiptService());

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync(ApplyPath, Json("{ not json"));

        await AssertRefusalAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation.InvalidBody);
    }

    [SkippableFact]
    public async Task A_non_json_long_rest_body_returns_a_typed_media_refusal()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = WithService(new ReceiptService());

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync(
            ApplyPath,
            new StringContent("{}", Encoding.UTF8, "text/plain"));

        await AssertRefusalAsync(response, HttpStatusCode.UnsupportedMediaType, ErrorCodes.Validation.UnsupportedMediaType);
    }

    [SkippableTheory]
    [InlineData("ExactDuplicates", "Applied", "None", null)]
    [InlineData("EquivalentObservations", "NoChange", "Pinned", null)]
    [InlineData("Supersession", "Applied", "None", "version-b")]
    public async Task An_exact_declaration_returns_the_content_free_receipt(
        string kind,
        string outcome,
        string reason,
        string? survivorVersionId)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ReceiptService service = new(kind, outcome, reason, survivorVersionId);

        await using ArcanumWebApplicationFactory factory = WithService(service);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync(ApplyPath, Json(RequestBody(kind, survivorVersionId)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(body.RootElement.GetProperty("isSuccess").GetBoolean());

        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));

        AssertReceipt(body.RootElement.GetProperty("data"), kind, outcome, reason);
    }

    [SkippableFact]
    public async Task Receipt_inspection_returns_retained_exact_coordinates_without_content()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = WithService(new ReceiptService());

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync(ReceiptPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(body.RootElement.GetProperty("isSuccess").GetBoolean());

        AssertReceipt(body.RootElement.GetProperty("data"), "ExactDuplicates", "Applied", "None");
    }

    [SkippableFact]
    public async Task Persisted_search_keeps_a_consolidated_source_visible_and_names_its_survivor_and_receipt()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { Annals = true, Saga = true, Embeddings = true },

                Integrations = settings.Integrations with
                {
                    Embeddings = settings.Integrations.Embeddings with
                    {
                        Provider = "test",

                        Model = "test-embed",

                        Dimensions = 64,
                    },
                },
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        LongRestTarget[] targets = new LongRestTarget[2];

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            ISagaMemoryStore store = scope.ServiceProvider.GetRequiredService<ISagaMemoryStore>();

            IAnnalsStore annals = scope.ServiceProvider.GetRequiredService<IAnnalsStore>();

            float[] embedding = new float[64];

            embedding[0] = 1;

            DateTimeOffset createdAt = DateTimeOffset.UtcNow;

            for (int index = 0; index < targets.Length; index++)
            {
                string id = index == 0 ? "long-rest-a" : "long-rest-b";

                Assert.Equal(SagaMemoryWriteOutcome.Written, await store.InsertAsync(
                    id,
                    "one retained conclusion",
                    createdAt,
                    null,
                    null,
                    "extraction",
                    embedding,
                    CancellationToken.None));

                AnnalClaimHead? claim = await annals.GetClaimAsync(AnnalSubjectStore.Saga, id, CancellationToken.None);

                Assert.NotNull(claim);

                targets[index] = new LongRestTarget(
                    id,
                    claim.CurrentVersionId,
                    Convert.ToHexString(AnnalContentDigest.ForSagaMemory("one retained conclusion")));
            }
        }

        LongRestRequest declaration = new(LongRestTransformationKind.ExactDuplicates, targets);

        using HttpResponseMessage applied = await client.PostAsync(
            ApplyPath,
            Json(JsonSerializer.Serialize(declaration, ArcanumJsonContext.Default.LongRestRequest)));

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);

        ApiResponse<LongRestReceipt>? receipt = JsonSerializer.Deserialize(
            await applied.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseLongRestReceipt);

        Assert.Equal(LongRestOutcome.Applied, receipt!.Data!.Outcome);

        using HttpResponseMessage inspected = await client.PostAsync(
            "/api/memory/search",
            Json("""{"query":"retained","scope":"saga"}"""));

        Assert.Equal(HttpStatusCode.OK, inspected.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await inspected.Content.ReadAsStringAsync());

        JsonElement[] results = body.RootElement.GetProperty("data").GetProperty("results").EnumerateArray().ToArray();

        Assert.Equal(2, results.Length);

        JsonElement source = Assert.Single(results, row => row.GetProperty("sourceId").GetString() == "long-rest-b");

        Assert.Equal("one retained conclusion", source.GetProperty("content").GetString());

        Assert.Equal("Consolidated", source.GetProperty("sagaEligibility").GetString());

        string provenance = source.GetProperty("provenance").GetString()!;

        Assert.Contains("consolidated into memory long-rest-a", provenance, StringComparison.Ordinal);

        Assert.Contains($"version {targets[0].ExpectedVersionId}", provenance, StringComparison.Ordinal);

        Assert.Contains($"Long Rest receipt {receipt.Data.ReceiptId}", provenance, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [MemberData(nameof(ServiceFailures))]
    public async Task Long_rest_service_refusals_keep_their_public_status_and_error_code(string code, HttpStatusCode status)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = WithService(new ReceiptService(failure: code));

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage applied = await client.PostAsync(ApplyPath, Json(RequestBody()));

        await AssertRefusalAsync(applied, status, code);

        using HttpResponseMessage inspected = await client.GetAsync(ReceiptPath);

        await AssertRefusalAsync(inspected, status, code);
    }

    [SkippableTheory]
    [InlineData("null")]
    [InlineData("{\"kind\":1,\"targets\":[]}")]
    public async Task A_null_or_numeric_kind_body_is_refused_as_invalid_json(string payload)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = WithService(new ReceiptService());

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync(ApplyPath, Json(payload));

        await AssertRefusalAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation.InvalidBody);
    }

    [Fact]
    public void Long_rest_wire_contracts_have_source_generated_metadata_and_string_only_enums()
    {
        Assert.NotNull(ArcanumJsonContext.Default.GetTypeInfo(typeof(LongRestRequest)));

        Assert.NotNull(ArcanumJsonContext.Default.GetTypeInfo(typeof(ApiResponse<LongRestReceipt>)));

        string json = JsonSerializer.Serialize(
            new ReceiptService().Receipt,
            ArcanumJsonContext.Default.GetTypeInfo(typeof(LongRestReceipt))!);

        using JsonDocument body = JsonDocument.Parse(json);

        AssertReceipt(body.RootElement, "ExactDuplicates", "Applied", "None");
    }

    private static ArcanumWebApplicationFactory WithService(ReceiptService service) => new()
    {
        ServiceOverrides = services =>
        {
            services.RemoveAll<ILongRestService>();

            services.AddScoped<ILongRestService>(_ => service);
        },
    };

    private static string RequestBody(string kind = "ExactDuplicates", string? survivorVersionId = null) =>
        $$"""{"kind":"{{kind}}","targets":[{"memoryId":"memory-a","expectedVersionId":"version-a","expectedContentHash":"{{HashA}}"},{"memoryId":"memory-b","expectedVersionId":"version-b","expectedContentHash":"{{HashA}}"}],"survivorVersionId":{{(survivorVersionId is null ? "null" : $"\"{survivorVersionId}\"")}}}""";

    private static void AssertReceipt(JsonElement receipt, string kind, string outcome, string reason)
    {
        Assert.Equal("receipt-1", receipt.GetProperty("receiptId").GetString());

        Assert.Equal(1, receipt.GetProperty("policyVersion").GetInt32());

        Assert.Equal(kind, receipt.GetProperty("kind").GetString());

        Assert.Equal(outcome, receipt.GetProperty("outcome").GetString());

        Assert.Equal(reason, receipt.GetProperty("reason").GetString());

        Assert.Equal(HashA, receipt.GetProperty("inputHash").GetString());

        Assert.Equal(HashB, receipt.GetProperty("outputHash").GetString());

        Assert.Equal(outcome == "Applied" ? "memory-b" : null, receipt.GetProperty("survivorMemoryId").GetString());

        Assert.Equal(outcome == "Applied" ? "claim-b" : null, receipt.GetProperty("survivorClaimId").GetString());

        Assert.Equal(outcome == "Applied" ? "version-b" : null, receipt.GetProperty("survivorVersionId").GetString());

        if (outcome == "Applied")
        {
            Assert.Equal("version-a", Assert.Single(receipt.GetProperty("supersededVersionIds").EnumerateArray()).GetString());
        }
        else
        {
            Assert.Equal(0, receipt.GetProperty("supersededVersionIds").GetArrayLength());
        }

        Assert.Equal(2, receipt.GetProperty("targets").GetArrayLength());

        JsonElement target = receipt.GetProperty("targets")[0];

        Assert.Equal("memory-a", target.GetProperty("memoryId").GetString());

        Assert.Equal("claim-a", target.GetProperty("claimId").GetString());

        Assert.Equal("version-a", target.GetProperty("versionId").GetString());

        Assert.Equal(2, target.GetProperty("revision").GetInt32());

        Assert.Equal(1, target.GetProperty("contentHashFormat").GetInt32());

        Assert.Equal(HashA, target.GetProperty("contentHash").GetString());

        Assert.False(receipt.TryGetProperty("content", out _));

        Assert.False(target.TryGetProperty("content", out _));
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static async Task AssertRefusalAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        Assert.Equal(status, response.StatusCode);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(body.RootElement.GetProperty("isSuccess").GetBoolean());

        Assert.Equal(errorCode, body.RootElement.GetProperty("error").GetProperty("code").GetString());

        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
    }

    private sealed class ReceiptService(
        string kind = "ExactDuplicates",
        string outcome = "Applied",
        string reason = "None",
        string? survivorVersionId = null,
        string? failure = null) : ILongRestService
    {
        public LongRestReceipt Receipt { get; } = new(
            "receipt-1",
            1,
            Enum.Parse<LongRestTransformationKind>(kind),
            Enum.Parse<LongRestOutcome>(outcome),
            Enum.Parse<LongRestReason>(reason),
            HashA,
            HashB,
            [
                new LongRestReceiptTarget("memory-a", "claim-a", "version-a", 2, (AnnalContentHashFormat)1, HashA),
                new LongRestReceiptTarget("memory-b", "claim-b", "version-b", 3, (AnnalContentHashFormat)1, HashA),
            ],
            outcome == "Applied" ? "memory-b" : null,
            outcome == "Applied" ? "claim-b" : null,
            outcome == "Applied" ? "version-b" : null,
            outcome == "Applied" ? ["version-a"] : []);

        public Task<Result<LongRestReceipt>> ApplyAsync(LongRestRequest request, CancellationToken cancellationToken)
        {
            if (request.Kind != Receipt.Kind || request.SurvivorVersionId != survivorVersionId
                || request.Targets is not [
                    { MemoryId: "memory-a", ExpectedVersionId: "version-a", ExpectedContentHash: HashA },
                    { MemoryId: "memory-b", ExpectedVersionId: "version-b", ExpectedContentHash: HashA }])
            {
                return Task.FromResult(Result<LongRestReceipt>.Failure(new Error("LongRest.InvalidRequest", "The exact declaration was not preserved.")));
            }

            return Answer();
        }

        public Task<Result<LongRestReceipt>> GetReceiptAsync(string receiptId, CancellationToken cancellationToken) =>
            receiptId == "receipt-1"
                ? Answer()
                : Task.FromResult(Result<LongRestReceipt>.Failure(new Error("LongRest.NotFound", "No receipt has that identity.")));

        private Task<Result<LongRestReceipt>> Answer() => Task.FromResult(failure is null
            ? Result<LongRestReceipt>.Success(Receipt)
            : Result<LongRestReceipt>.Failure(new Error(failure, "The declared transformation was refused.")));
    }
}
