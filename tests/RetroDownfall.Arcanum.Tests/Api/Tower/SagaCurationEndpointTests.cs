using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Api.Tower;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// The six curation routes under <c>/api/memory/saga/</c>, driven through the mapped HTTP surface.
/// </summary>
/// <remarks>
/// Every case here enters through the route. A test that called <c>ISagaCurationService</c> directly
/// would prove the service a second time and the routing not at all — and routing is the whole of what
/// this task added, since the engine underneath was already built and tested.
/// </remarks>
[Collection("ApiHost")]
public sealed class SagaCurationEndpointTests
{

    /// <summary>
    /// Matches ArcanumSettingClamps.EmbeddingsDimensions' 64-dimension floor, so SagaMemoryStore's
    /// dimension-validation guard does not reject these inserts.
    /// </summary>
    private const int TestDimensions = 64;

    private const string OriginalContent = "the operator prefers tabs";

    private const string CorrectedContent = "the operator prefers spaces";

    [SkippableFact]
    public async Task An_operator_corrects_a_memory_and_a_later_search_returns_the_corrected_text()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // The acceptance criterion end to end: the corrected text is what retrieval reflects, not the
        // text the operator rejected.
        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-correct", OriginalContent);

        using HttpResponseMessage corrected = await PostCorrectAsync(
            client,
            "mem-correct",
            new SagaCorrectRequest(Hash(OriginalContent), CorrectedContent));

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        SagaMemoryDetail detail = await ReadWriteResultAsync(corrected, SagaCurationOutcomeKind.Applied);

        Assert.Equal(CorrectedContent, detail.Memory.Content);

        Assert.Null(detail.Lifecycle.RetiredAtUtc);

        Assert.Equal(SagaRetrievalEligibility.Eligible, detail.Eligibility);

        SagaMemoryDto[] hits = await DivineAsync(client);

        Assert.Equal(CorrectedContent, Assert.Single(hits).Content);

    }

    [SkippableFact]
    public async Task A_correction_naming_content_the_caller_never_read_is_refused_with_its_own_code()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-stale", OriginalContent);

        using HttpResponseMessage response = await PostCorrectAsync(
            client,
            "mem-stale",
            new SagaCorrectRequest(Hash("something else entirely"), CorrectedContent));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await AssertRefusalAsync(response, ErrorCodes.Saga.StaleContent);

    }

    /// <summary>
    /// Correcting a memory to the text it already holds succeeds. The store writes nothing for that
    /// case and reports <c>Unchanged</c>; the service treats it as a success rather than a refusal, so
    /// the route answers 200 — and says <c>Unchanged</c>, which is how the caller tells this apart from
    /// the correction that did the work.
    /// </summary>
    [SkippableFact]
    public async Task A_correction_restating_the_stored_text_answers_with_the_memory_rather_than_a_refusal()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-noop", OriginalContent);

        using HttpResponseMessage response = await PostCorrectAsync(
            client,
            "mem-noop",
            new SagaCorrectRequest(Hash(OriginalContent), OriginalContent));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SagaMemoryDetail detail = await ReadWriteResultAsync(response, SagaCurationOutcomeKind.Unchanged);

        Assert.Equal(OriginalContent, detail.Memory.Content);

    }

    /// <summary>
    /// A retired memory is reinstated before it is corrected, and the route says which of the two
    /// happened rather than refusing.
    /// </summary>
    /// <remarks>
    /// Nothing is written, so the stored text is asserted unchanged as well as the outcome: the kind
    /// alone would be satisfied by a route that reported it and corrected the memory anyway.
    /// </remarks>
    [SkippableFact]
    public async Task Correcting_a_retired_memory_reports_that_it_is_retired_rather_than_refusing()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-gone", OriginalContent);

        using HttpResponseMessage retired = await PostRetireAsync(
            client,
            "mem-gone",
            new SagaRetireRequest(Hash(OriginalContent)));

        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        using HttpResponseMessage response = await PostCorrectAsync(
            client,
            "mem-gone",
            new SagaCorrectRequest(Hash(OriginalContent), CorrectedContent));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SagaMemoryDetail detail = await ReadWriteResultAsync(response, SagaCurationOutcomeKind.AlreadyRetired);

        Assert.Equal(OriginalContent, detail.Memory.Content);

    }

    /// <summary>
    /// The detail view's other half — the claim and the versions behind it — reaching the wire.
    /// </summary>
    /// <remarks>
    /// A memory written while the Annals feature is off carries no claim, and a correction opens one on
    /// the way through, so this case reads the claim a correction it drove actually produced rather than
    /// one it arranged. It is also the only case that serializes <c>AnnalClaimHead</c> and
    /// <c>AnnalClaimVersion</c> through the source-generated context; without it those two shapes would
    /// be compile-verified and never exercised.
    /// </remarks>
    [SkippableFact]
    public async Task The_detail_route_carries_the_claim_a_correction_opened_and_the_versions_behind_it()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-claim", OriginalContent);

        using HttpResponseMessage corrected = await PostCorrectAsync(
            client,
            "mem-claim",
            new SagaCorrectRequest(Hash(OriginalContent), CorrectedContent));

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        using HttpResponseMessage shown = await client.GetAsync("/api/memory/saga/mem-claim");

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        SagaMemoryDetail detail = await ReadDetailAsync(shown);

        Assert.NotNull(detail.Claim);

        Assert.Equal(AnnalSubjectStore.Saga, detail.Claim!.SubjectStore);

        Assert.Equal("mem-claim", detail.Claim.SubjectId);

        Assert.Equal(AnnalOperation.Correct, detail.Claim.CurrentOperation);

        Assert.NotEmpty(detail.History);

    }

    [SkippableFact]
    public async Task A_correction_is_refused_when_the_embedding_substrate_cannot_produce_a_vector()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(
            new FakeWeaveService { Available = false });

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-embed", OriginalContent);

        using HttpResponseMessage response = await PostCorrectAsync(
            client,
            "mem-embed",
            new SagaCorrectRequest(Hash(OriginalContent), CorrectedContent));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await AssertRefusalAsync(response, ErrorCodes.Saga.EmbeddingUnavailable);

    }

    /// <summary>
    /// A hash the caller could not have read is a request-shape problem rather than a curation refusal,
    /// so it reaches the operator as a 400 and never as the 500 an unmapped code would have produced.
    /// </summary>
    [SkippableFact]
    public async Task A_malformed_expected_content_hash_is_refused_as_a_request_shape_problem()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-badhash", OriginalContent);

        using HttpResponseMessage response = await PostCorrectAsync(
            client,
            "mem-badhash",
            new SagaCorrectRequest("not-a-digest", CorrectedContent));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await AssertRefusalAsync(response, ErrorCodes.Validation.InvalidFields);

    }

    [SkippableFact]
    public async Task A_retired_memory_stops_reaching_retrieval_and_stays_visible_marked_retired()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-retire", OriginalContent);

        using HttpResponseMessage retired = await PostRetireAsync(
            client,
            "mem-retire",
            new SagaRetireRequest(Hash(OriginalContent)));

        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        SagaMemoryDetail detail = await ReadWriteResultAsync(retired, SagaCurationOutcomeKind.Applied);

        Assert.NotNull(detail.Lifecycle.RetiredAtUtc);

        Assert.Equal(SagaRetrievalEligibility.Retired, detail.Eligibility);

        Assert.Empty(await DivineAsync(client));

        SagaMemoryDto listed = Assert.Single(await ListAsync(client));

        Assert.NotNull(listed.RetiredAtUtc);

    }

    /// <summary>
    /// Retiring a memory that is already retired succeeds and says so.
    /// </summary>
    /// <remarks>
    /// This is the retry a dropped connection produces: the first attempt landed, the caller never saw
    /// the answer, and asking again must not be told its own success was a failure. The outcome is what
    /// keeps that from collapsing into "it worked" — a caller counting what it retired reads
    /// <c>Applied</c> and leaves this one out.
    /// </remarks>
    [SkippableFact]
    public async Task Retiring_a_memory_that_is_already_retired_succeeds_and_says_it_was_already_retired()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-twice", OriginalContent);

        using HttpResponseMessage first = await PostRetireAsync(
            client,
            "mem-twice",
            new SagaRetireRequest(Hash(OriginalContent)));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        _ = await ReadWriteResultAsync(first, SagaCurationOutcomeKind.Applied);

        using HttpResponseMessage second = await PostRetireAsync(
            client,
            "mem-twice",
            new SagaRetireRequest(Hash(OriginalContent)));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        SagaMemoryDetail detail = await ReadWriteResultAsync(second, SagaCurationOutcomeKind.AlreadyRetired);

        Assert.NotNull(detail.Lifecycle.RetiredAtUtc);

    }

    [SkippableFact]
    public async Task A_reinstated_memory_reaches_retrieval_again()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-back", OriginalContent);

        using HttpResponseMessage retired = await PostRetireAsync(
            client,
            "mem-back",
            new SagaRetireRequest(Hash(OriginalContent)));

        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        using HttpResponseMessage reinstated = await PostReinstateAsync(
            client,
            "mem-back",
            new SagaReinstateRequest(Hash(OriginalContent)));

        Assert.Equal(HttpStatusCode.OK, reinstated.StatusCode);

        SagaMemoryDetail detail = await ReadWriteResultAsync(reinstated, SagaCurationOutcomeKind.Applied);

        Assert.Null(detail.Lifecycle.RetiredAtUtc);

        Assert.Equal(SagaRetrievalEligibility.Eligible, detail.Eligibility);

        Assert.Equal(OriginalContent, Assert.Single(await DivineAsync(client)).Content);

    }

    /// <summary>
    /// Reinstating against content the caller never read is still refused — the one refusal reinstate
    /// keeps, and the line the operator's ruling draws: this tells them something they could not have
    /// seen, rather than telling them they already have what they asked for.
    /// </summary>
    [SkippableFact]
    public async Task Reinstating_against_content_the_caller_never_read_is_refused_with_its_own_code()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-stalereinstate", OriginalContent);

        using HttpResponseMessage retired = await PostRetireAsync(
            client,
            "mem-stalereinstate",
            new SagaRetireRequest(Hash(OriginalContent)));

        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        using HttpResponseMessage response = await PostReinstateAsync(
            client,
            "mem-stalereinstate",
            new SagaReinstateRequest(Hash("something else entirely")));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await AssertRefusalAsync(response, ErrorCodes.Saga.StaleContent);

    }

    /// <summary>Reinstating a memory that is not retired succeeds and says so, for the same reason.</summary>
    [SkippableFact]
    public async Task Reinstating_a_memory_that_was_never_retired_succeeds_and_says_it_was_not_retired()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-live", OriginalContent);

        using HttpResponseMessage response = await PostReinstateAsync(
            client,
            "mem-live",
            new SagaReinstateRequest(Hash(OriginalContent)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SagaMemoryDetail detail = await ReadWriteResultAsync(response, SagaCurationOutcomeKind.NotRetired);

        Assert.Null(detail.Lifecycle.RetiredAtUtc);

    }

    [SkippableFact]
    public async Task A_pinned_memory_reports_its_pin_on_the_detail_route()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-pin", OriginalContent);

        using HttpResponseMessage pinned = await client.PostAsync("/api/memory/saga/mem-pin/pin", content: null);

        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);

        Assert.NotNull(
            (await ReadWriteResultAsync(pinned, SagaCurationOutcomeKind.Applied)).Lifecycle.PinnedAtUtc);

        using HttpResponseMessage shown = await client.GetAsync("/api/memory/saga/mem-pin");

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        SagaMemoryDetail detail = await ReadDetailAsync(shown);

        Assert.NotNull(detail.Lifecycle.PinnedAtUtc);

        Assert.Equal(SagaRetrievalEligibility.Eligible, detail.Eligibility);

    }

    [SkippableFact]
    public async Task Unpinning_clears_the_pin_the_detail_route_reports()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-unpin", OriginalContent);

        using HttpResponseMessage pinned = await client.PostAsync("/api/memory/saga/mem-unpin/pin", content: null);

        Assert.NotNull(
            (await ReadWriteResultAsync(pinned, SagaCurationOutcomeKind.Applied)).Lifecycle.PinnedAtUtc);

        using HttpResponseMessage unpinned = await client.PostAsync("/api/memory/saga/mem-unpin/unpin", content: null);

        Assert.Equal(HttpStatusCode.OK, unpinned.StatusCode);

        Assert.Null(
            (await ReadWriteResultAsync(unpinned, SagaCurationOutcomeKind.Applied)).Lifecycle.PinnedAtUtc);

        using HttpResponseMessage shown = await client.GetAsync("/api/memory/saga/mem-unpin");

        Assert.Null((await ReadDetailAsync(shown)).Lifecycle.PinnedAtUtc);

    }

    [SkippableFact]
    public async Task The_detail_route_answers_not_found_for_a_memory_that_does_not_exist()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/memory/saga/mem-absent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await AssertDetailRefusalAsync(response, ErrorCodes.Saga.NotFound);

    }

    [SkippableFact]
    public async Task Pinning_a_memory_that_does_not_exist_is_refused_as_not_found()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/api/memory/saga/mem-absent/pin",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await AssertRefusalAsync(response, ErrorCodes.Saga.NotFound);

    }

    [SkippableFact]
    public async Task Unpinning_a_memory_that_does_not_exist_is_refused_as_not_found()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/api/memory/saga/mem-absent/unpin",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await AssertRefusalAsync(response, ErrorCodes.Saga.NotFound);

    }

    [SkippableFact]
    public async Task Retiring_a_memory_that_does_not_exist_is_refused_as_not_found()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await PostRetireAsync(
            client,
            "mem-absent",
            new SagaRetireRequest(Hash(OriginalContent)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await AssertRefusalAsync(response, ErrorCodes.Saga.NotFound);

    }

    /// <summary>
    /// A body none of the three body-taking routes could read is refused before the curation service is
    /// reached, as the request-shape problem it is.
    /// </summary>
    [SkippableFact]
    public async Task A_write_route_called_with_no_body_is_refused_as_an_invalid_request()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        foreach (string route in new[] { "correct", "retire", "reinstate" })
        {

            using HttpResponseMessage response = await client.PostAsync(
                $"/api/memory/saga/mem-absent/{route}",
                content: null);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            await AssertRefusalAsync(response, ErrorCodes.Validation.InvalidBody);

        }

    }

    [SkippableFact]
    public async Task Every_curation_route_refuses_an_unauthenticated_caller()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        using HttpClient anonymous = factory.CreateClient();

        foreach (string route in new[] { "correct", "retire", "reinstate", "pin", "unpin" })
        {

            using HttpResponseMessage response = await anonymous.PostAsync(
                $"/api/memory/saga/mem-1/{route}",
                content: null);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        }

        using HttpResponseMessage shown = await anonymous.GetAsync("/api/memory/saga/mem-1");

        Assert.Equal(HttpStatusCode.Unauthorized, shown.StatusCode);

    }

    private static string Hash(string content) =>
        Convert.ToHexString(AnnalContentDigest.ForSagaMemory(content));

    private static Task<HttpResponseMessage> PostCorrectAsync(HttpClient client, string id, SagaCorrectRequest request) =>
        client.PostAsync(
            $"/api/memory/saga/{id}/correct",
            JsonBody(JsonSerializer.Serialize(request, ArcanumJsonContext.Default.SagaCorrectRequest)));

    private static Task<HttpResponseMessage> PostRetireAsync(HttpClient client, string id, SagaRetireRequest request) =>
        client.PostAsync(
            $"/api/memory/saga/{id}/retire",
            JsonBody(JsonSerializer.Serialize(request, ArcanumJsonContext.Default.SagaRetireRequest)));

    private static Task<HttpResponseMessage> PostReinstateAsync(HttpClient client, string id, SagaReinstateRequest request) =>
        client.PostAsync(
            $"/api/memory/saga/{id}/reinstate",
            JsonBody(JsonSerializer.Serialize(request, ArcanumJsonContext.Default.SagaReinstateRequest)));

    private static StringContent JsonBody(string payload) => new(payload, Encoding.UTF8, "application/json");

    /// <summary>The detail route's body: the projection alone, with no outcome to report.</summary>
    private static async Task<SagaMemoryDetail> ReadDetailAsync(HttpResponseMessage response)
    {

        ApiResponse<SagaMemoryDetail>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.NotNull(body.Data);

        return body.Data!;

    }

    /// <summary>
    /// A write route's body, asserting the outcome it reports before handing back the projection.
    /// </summary>
    /// <remarks>
    /// The outcome is asserted by every success case rather than by one of them, because it is the only
    /// thing distinguishing "this call did the work" from "the memory was already like that" — and a
    /// change that collapsed the two would otherwise leave every one of these cases still passing.
    /// </remarks>
    private static async Task<SagaMemoryDetail> ReadWriteResultAsync(
        HttpResponseMessage response,
        SagaCurationOutcomeKind expectedOutcome)
    {

        ApiResponse<SagaCurationResult>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSagaCurationResult);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.Equal(expectedOutcome, body.Data!.Outcome);

        return body.Data.Detail;

    }

    /// <summary>A refusal from the detail route, whose envelope carries no outcome.</summary>
    private static async Task AssertDetailRefusalAsync(HttpResponseMessage response, string expectedCode)
    {

        ApiResponse<SagaMemoryDetail>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Null(body.Data);

        Assert.Equal(expectedCode, body.Error?.Code);

    }

    /// <summary>A refusal from one of the five write routes.</summary>
    private static async Task AssertRefusalAsync(HttpResponseMessage response, string expectedCode)
    {

        ApiResponse<SagaCurationResult>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSagaCurationResult);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Null(body.Data);

        Assert.Equal(expectedCode, body.Error?.Code);

    }

    /// <summary>What <c>POST /api/saga/divine</c> can currently reach, read through that route.</summary>
    private static async Task<SagaMemoryDto[]> DivineAsync(HttpClient client)
    {

        using HttpResponseMessage response = await client.PostAsync(
            "/api/saga/divine",
            JsonBody(JsonSerializer.Serialize(
                new SagaSearchRequest("what did we decide about indentation?"),
                ArcanumJsonContext.Default.SagaSearchRequest)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<SagaSearchResult>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSagaSearchResult);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        return body.Data!.Memories;

    }

    /// <summary>What <c>GET /api/saga</c> lists, which shows a retired memory rather than hiding it.</summary>
    private static async Task<SagaMemoryDto[]> ListAsync(HttpClient client)
    {

        using HttpResponseMessage response = await client.GetAsync("/api/saga");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<SagaMemoryDto[]>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        return body.Data ?? [];

    }

    private static ArcanumWebApplicationFactory CreateEnabledFactory(IWeaveService weaveService) =>
        new()
        {
            SettingsOverride = static settings => settings with
            {
                Features = settings.Features with
                {
                    Embeddings = true,
                    Saga = true,
                },
                Integrations = settings.Integrations with
                {
                    Embeddings = settings.Integrations.Embeddings with
                    {
                        Provider = "test",
                        Model = "test-embed",
                        Dimensions = TestDimensions,
                    },
                },
            },
            ServiceOverrides = services =>
            {
                services.RemoveAll<IWeaveService>();

                services.AddSingleton(weaveService);

            },
        };

    /// <summary>
    /// Writes the memory a case curates through the store's own insert — the production write path —
    /// rather than by putting a row in the table, so nothing a case asserts was seeded by hand.
    /// </summary>
    private static async Task SeedMemoryAsync(ArcanumWebApplicationFactory factory, string id, string content)
    {

        using IServiceScope scope = factory.Services.CreateScope();

        ISagaMemoryStore store = scope.ServiceProvider.GetRequiredService<ISagaMemoryStore>();

        SagaMemoryWriteOutcome outcome = await store.InsertAsync(
            id,
            content,
            DateTimeOffset.UtcNow,
            sessionId: null,
            tags: null,
            source: "extraction",
            Vec(1f),
            CancellationToken.None);

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);

    }

    /// <summary>Builds a <see cref="TestDimensions"/>-length vector with <paramref name="leading"/> in its first slots.</summary>
    private static float[] Vec(params float[] leading)
    {

        float[] result = new float[TestDimensions];

        leading.AsSpan().CopyTo(result);

        return result;

    }

    /// <summary>
    /// Answers one fixed vector for every text, so similarity is not what any case here is about — the
    /// route's behaviour is.
    /// </summary>
    private sealed class FakeWeaveService : IWeaveService
    {

        public bool Available { get; set; } = true;

        public bool IsAvailable => Available;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(Vec(1f))));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the Saga curation endpoints.");

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the Saga curation endpoints.");

    }

}
