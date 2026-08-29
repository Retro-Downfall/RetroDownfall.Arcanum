using System.Data;

using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Api.Tower;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Weave;

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
    /// A retired memory is reinstated before it is corrected. This one stays a refusal where retire's
    /// own already-retired outcome does not, and the difference is whether the verb gave the operator
    /// what they named: a retire did, and a correction never touched the memory at all, so answering
    /// 200 would tell them a correction landed that did not.
    /// </summary>
    [SkippableFact]
    public async Task Correcting_a_retired_memory_is_refused_because_it_is_reinstated_before_it_is_corrected()
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

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await AssertRefusalAsync(response, ErrorCodes.Saga.AlreadyRetired);

        // And the refusal is honest about the store: the memory really was left untouched. This is
        // evidence that nothing was written, not the reason for the refusal -- the reason is the
        // retirement, which is checked before either content comparison.
        using HttpResponseMessage shown = await client.GetAsync("/api/memory/saga/mem-gone");

        Assert.Equal(OriginalContent, (await ReadDetailAsync(shown)).Memory.Content);

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
    /// A memory whose embedding row is gone is corrected, and the correction publishes an embedding for
    /// the new text rather than reporting a success that left the memory unretrievable.
    /// </summary>
    /// <remarks>
    /// <para>The missing row is not cut by hand here. It is left behind by
    /// <c>BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync</c>, the production writer of this
    /// state: a Grimoire restored from a backup taken under a different configured embedding width keeps
    /// every <c>saga_memories</c> row and drops the base-table vector behind it, to be recomputed on
    /// demand. It does not touch the vec0 mirror, so what a restore hands the operator is already the
    /// two tables disagreeing — and a correction is one of the verbs that is supposed to settle it.</para>
    /// <para>The detail route is read before the correction as well as after, so a failure here is about
    /// the correction rather than about the arrangement: the first read is what proves the memory
    /// reached the state this case is about.</para>
    /// <para>The seeded vector is deliberately not the one <see cref="FakeWeaveService"/> answers, so
    /// every assertion below can tell the vector the correction published apart from the vector the
    /// insert wrote. Without that they are the same bytes and a correction that wrote neither table
    /// would still look right.</para>
    /// </remarks>
    [SkippableFact]
    public async Task Correcting_a_memory_whose_embedding_row_is_gone_publishes_one_rather_than_reporting_a_hollow_success()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await EnableTheVectorMirrorAsync(factory);

        await SeedMemoryAsync(factory, "mem-unembedded", OriginalContent, Vec(0f, 1f));

        byte[]? extracted = await EmbeddingAsync(factory, "saga_memory_embeddings", "mem-unembedded");

        Assert.NotNull(extracted);

        // The production insert filled the mirror, not this case. An assertion about a table nothing
        // production wrote to would hold just as well on a build where the mirror write never ran.
        Assert.Equal(extracted, await EmbeddingAsync(factory, "saga_memory_embeddings_vec", "mem-unembedded"));

        await DropEmbeddingsTakenUnderAnotherWidthAsync(factory);

        Assert.Null(await EmbeddingAsync(factory, "saga_memory_embeddings", "mem-unembedded"));

        Assert.Equal(extracted, await EmbeddingAsync(factory, "saga_memory_embeddings_vec", "mem-unembedded"));

        using HttpResponseMessage before = await client.GetAsync("/api/memory/saga/mem-unembedded");

        Assert.Equal(
            SagaRetrievalEligibility.EmbeddingMissing,
            (await ReadDetailAsync(before)).Eligibility);

        using HttpResponseMessage corrected = await PostCorrectAsync(
            client,
            "mem-unembedded",
            new SagaCorrectRequest(Hash(OriginalContent), CorrectedContent));

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        SagaMemoryDetail detail = await ReadWriteResultAsync(corrected, SagaCurationOutcomeKind.Applied);

        Assert.Equal(CorrectedContent, detail.Memory.Content);

        Assert.Equal(SagaRetrievalEligibility.Eligible, detail.Eligibility);

        // The same answer through the route an operator reads it through, which is where a correction
        // that published nothing goes on reporting EmbeddingMissing after reporting success.
        using HttpResponseMessage shown = await client.GetAsync("/api/memory/saga/mem-unembedded");

        Assert.Equal(SagaRetrievalEligibility.Eligible, (await ReadDetailAsync(shown)).Eligibility);

        byte[]? published = await EmbeddingAsync(factory, "saga_memory_embeddings", "mem-unembedded");

        Assert.NotNull(published);

        // The corrected text's vector rather than the one the restore stranded in the mirror.
        Assert.NotEqual(extracted, published);

        // And both tables hold it. Their disagreeing is the whole of what this defect cost: a
        // correction that answered Applied while the two described different memories.
        Assert.Equal(published, await EmbeddingAsync(factory, "saga_memory_embeddings_vec", "mem-unembedded"));

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
    /// The retirement is the answer even when the correction carries the text already stored.
    /// </summary>
    /// <remarks>
    /// This is the cell that makes "they asked for new text" the wrong reason to attach to this refusal:
    /// here they asked for the text the memory already holds. <c>SagaMemoryStore.CorrectAsync</c> checks
    /// the retirement before it compares either the expected digest or the new content, so the answer is
    /// the retirement rather than <c>Unchanged</c> — and the reason written on the code has to survive
    /// this case, not only the ordinary one.
    /// </remarks>
    [SkippableFact]
    public async Task Correcting_a_retired_memory_to_the_text_it_already_holds_is_still_the_retirement()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-samegone", OriginalContent);

        using HttpResponseMessage retired = await PostRetireAsync(
            client,
            "mem-samegone",
            new SagaRetireRequest(Hash(OriginalContent)));

        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        using HttpResponseMessage response = await PostCorrectAsync(
            client,
            "mem-samegone",
            new SagaCorrectRequest(Hash(OriginalContent), OriginalContent));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await AssertRefusalAsync(response, ErrorCodes.Saga.AlreadyRetired);

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

    /// <summary>
    /// A correction carrying blank content is accepted, because a caller that sent it meant it.
    /// </summary>
    /// <remarks>
    /// Pinned deliberately rather than left to chance. <c>arcanum memory saga correct</c> refuses empty
    /// and whitespace-only content before it builds a request, and that divergence is the point: a
    /// <c>--file</c> path can lie about what it holds and the command cannot tell a mistyped one from a
    /// deliberate blank, whereas a request body arrived exactly as its caller wrote it. Hardening this
    /// route to match the command would take that choice away from the caller who has already made it,
    /// and <c>retire</c> is the verb for "stop this reaching retrieval" — blanking is not a synonym for
    /// it.
    /// </remarks>
    [SkippableFact]
    public async Task A_correction_to_blank_content_is_accepted_because_the_caller_that_sent_it_meant_it()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-blank", OriginalContent);

        using HttpResponseMessage response = await PostCorrectAsync(
            client,
            "mem-blank",
            new SagaCorrectRequest(Hash(OriginalContent), string.Empty));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SagaMemoryDetail detail = await ReadWriteResultAsync(response, SagaCurationOutcomeKind.Applied);

        Assert.Equal(string.Empty, detail.Memory.Content);

        // Still a live memory rather than a retired one: blanking and retiring are different acts.
        Assert.Null(detail.Lifecycle.RetiredAtUtc);

    }

    /// <summary>
    /// The digest the detail route publishes is the one the write verbs accept.
    /// </summary>
    /// <remarks>
    /// This is what makes <c>ContentHash</c> worth carrying rather than leaving each client to
    /// reproduce <see cref="AnnalContentDigest.ForSagaMemory"/>. Driven with content chosen to break a
    /// client that guessed at the encoding — an astral surrogate pair, a CRLF, a leading byte-order
    /// mark, and a trailing newline — and the hash is taken verbatim off the projection rather than
    /// computed here, so what passes is the round trip and not this test's own arithmetic.
    /// </remarks>
    [SkippableFact]
    public async Task The_digest_the_detail_route_publishes_is_the_one_a_write_verb_accepts()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string awkward = "\uFEFFprefers \U0001F600 tabs\r\nand spaces\n";

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-digest", awkward);

        using HttpResponseMessage shown = await client.GetAsync("/api/memory/saga/mem-digest");

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        SagaMemoryDetail detail = await ReadDetailAsync(shown);

        // The published value is the documented function's output, which is the half a client reads.
        Assert.Equal(Hash(awkward), detail.ContentHash);

        // And the half that matters: quoting it back is accepted, so a caller never has to hash anything.
        using HttpResponseMessage retired = await PostRetireAsync(
            client,
            "mem-digest",
            new SagaRetireRequest(detail.ContentHash));

        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        SagaMemoryDetail after = await ReadWriteResultAsync(retired, SagaCurationOutcomeKind.Applied);

        Assert.NotNull(after.Lifecycle.RetiredAtUtc);

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
    /// A body the route can read but cannot use is refused before the curation service is reached, as
    /// the request-shape problem it is.
    /// </summary>
    /// <remarks>
    /// Both shapes are well-formed JSON of the right media type, so they pass the parse and the
    /// media-type gates and reach the handler's own required-field check. A request carrying no body at
    /// all carries no <c>Content-Type</c> either and is answered by the media-type gate instead, which
    /// is asserted alongside the other unreadable bodies.
    /// </remarks>
    [SkippableFact]
    public async Task A_write_route_called_with_a_body_it_cannot_use_is_refused_as_an_invalid_request()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        foreach (string route in new[] { "correct", "retire", "reinstate" })
        {

            foreach (string payload in new[] { "{}", "null" })
            {

                using HttpResponseMessage response = await client.PostAsync(
                    $"/api/memory/saga/mem-absent/{route}",
                    new StringContent(payload, Encoding.UTF8, "application/json"));

                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

                await AssertRefusalAsync(response, ErrorCodes.Validation.InvalidBody);

            }

        }

    }

    /// <summary>
    /// Malformed JSON and a wrong media type answer with the envelope, not an empty body.
    /// </summary>
    /// <remarks>
    /// Minimal-API parameter binding answers both with a zero-length response — no code, no traceId —
    /// so these routes read their bodies through <c>ApiRequestJson</c> instead. An operator who sent
    /// bad JSON to a curation verb has to be told which thing was wrong.
    /// </remarks>
    [SkippableFact]
    public async Task A_write_route_names_what_was_wrong_with_a_body_it_could_not_read()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        foreach (string route in new[] { "correct", "retire", "reinstate" })
        {

            using HttpResponseMessage malformed = await client.PostAsync(
                $"/api/memory/saga/mem-absent/{route}",
                new StringContent("{ not json", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

            await AssertRefusalAsync(malformed, ErrorCodes.Validation.InvalidBody);

            using HttpResponseMessage wrongMedia = await client.PostAsync(
                $"/api/memory/saga/mem-absent/{route}",
                new StringContent("{}", Encoding.UTF8, "text/plain"));

            await AssertUnsupportedMediaTypeAsync(wrongMedia);

            // A request with no body at all carries no Content-Type, so the same gate answers it.
            using HttpResponseMessage bodyless = await client.PostAsync(
                $"/api/memory/saga/mem-absent/{route}",
                content: null);

            await AssertUnsupportedMediaTypeAsync(bodyless);

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

    /// <summary>The media-type gate's refusal, which the house helper renders in its own envelope.</summary>
    private static async Task AssertUnsupportedMediaTypeAsync(HttpResponseMessage response)
    {

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        ApiResponse<bool>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Validation.UnsupportedMediaType, body.Error?.Code);

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
    /// Builds the vec0 mirror and tells The Weave it is there, so the store's mirror writes run at all.
    /// </summary>
    /// <remarks>
    /// No schema file installs that table — it exists only where an accelerator built it — and this
    /// build ships none, so <c>WeaveIndexAvailability.IsVecAvailable</c> is false and every mirror write
    /// in <c>SagaMemoryStore</c> is skipped. The Covenant erasure and retention suites stand a plain
    /// table in for it the same way and for the same reason: a case about the two embedding tables
    /// agreeing cannot be written at all on a build where only one of them is ever written. The flag is
    /// set after the host is up because the bootstrapper clears it on its way through.
    /// </remarks>
    private static async Task EnableTheVectorMirrorAsync(ArcanumWebApplicationFactory factory)
    {

        using IServiceScope scope = factory.Services.CreateScope();

        SqliteConnection connection = await OpenGrimoireAsync(
            scope.ServiceProvider.GetRequiredService<ArcanumDbContext>());

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE "saga_memory_embeddings_vec" ("MemoryId" TEXT PRIMARY KEY, "Embedding" BLOB NOT NULL)
            """;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        factory.Services.GetRequiredService<WeaveIndexAvailability>()
            .SetAvailable(true, "Test mirror present.");

    }

    /// <summary>
    /// Empties the Saga base embedding table the way a restore does, leaving every <c>saga_memories</c>
    /// row exactly where it was and no vector behind any of them.
    /// </summary>
    /// <remarks>
    /// <c>BackupRestoreService</c> runs this same worker over every restored Grimoire, with the
    /// installation's own configured width, so an archive written under a different one lands with its
    /// memories intact and its base-table vectors gone. Passing a width no seeded vector carries
    /// reproduces that exactly, which is why no case here deletes a row on its own account.
    /// </remarks>
    private static async Task DropEmbeddingsTakenUnderAnotherWidthAsync(ArcanumWebApplicationFactory factory)
    {

        using IServiceScope scope = factory.Services.CreateScope();

        SqliteConnection connection = await OpenGrimoireAsync(
            scope.ServiceProvider.GetRequiredService<ArcanumDbContext>());

        long removed = await BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync(
            connection,
            TestDimensions + 1,
            CancellationToken.None);

        // The seeded memory's vector and nothing else: an arrangement that removed no row would leave
        // every assertion downstream describing a memory that never lost its embedding.
        Assert.Equal(1L, removed);

    }

    /// <summary>The vector one table holds for one memory, or null where it holds no row for it.</summary>
    private static async Task<byte[]?> EmbeddingAsync(
        ArcanumWebApplicationFactory factory,
        string table,
        string id)
    {

        using IServiceScope scope = factory.Services.CreateScope();

        SqliteConnection connection = await OpenGrimoireAsync(
            scope.ServiceProvider.GetRequiredService<ArcanumDbContext>());

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"""SELECT "Embedding" FROM "{table}" WHERE "MemoryId" = $id""";

        _ = command.Parameters.AddWithValue("$id", id);

        return await command.ExecuteScalarAsync(CancellationToken.None) as byte[];

    }

    /// <summary>
    /// The host's own connection into its temporary Grimoire, opened if the context handed it over
    /// closed. The caller holds the scope: this connection lives exactly as long as that context does.
    /// </summary>
    private static async Task<SqliteConnection> OpenGrimoireAsync(ArcanumDbContext db)
    {

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(CancellationToken.None);

        }

        return connection;

    }

    /// <summary>
    /// Writes the memory a case curates through the store's own insert — the production write path —
    /// rather than by putting a row in the table, so nothing a case asserts was seeded by hand.
    /// </summary>
    /// <remarks>
    /// <paramref name="embedding"/> defaults to the single vector <see cref="FakeWeaveService"/> answers
    /// for every text, which is what the retrieval cases need. A case that has to tell the vector a
    /// correction publishes apart from the vector this insert wrote names a different one.
    /// </remarks>
    private static async Task SeedMemoryAsync(
        ArcanumWebApplicationFactory factory,
        string id,
        string content,
        float[]? embedding = null)
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
            embedding ?? Vec(1f),
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
