using System.Net;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// Every route that accepts an opaque token answers one the decoder cannot read with its documented
/// 400, never an unhandled exception.
/// </summary>
/// <remarks>
/// <para>The framework's throwing base64url decoder raises <see cref="FormatException"/> instead of
/// reporting failure for a length no decoder can read and for a final character that sets bits no
/// encoder emits. Both are valid characters of the alphabet at a plausible length, so a character filter
/// lets them through, and the token comes from whoever calls the route. An escape would be a 500 where
/// the contract promises a 400, and would also single the token out from every other refusal.</para>
///
/// <para>The respelled token is a real, signed one with one unused trailing bit set: it decodes to the
/// bytes that were signed, so only a canonical decode can refuse it.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class NonCanonicalTokenRouteTests
{
    private static readonly MemoryReviewDigest ScopeDigest = Digest(0x10);

    private static readonly Guid MarkerGeneration = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [SkippableFact]
    public async Task The_review_routes_answer_an_unreadable_cursor_or_plan_token_with_a_400_InvalidToken()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            covenant: true);

        MemoryErasureRouteDriver driver = new(host.CreateAuthenticatedClient());

        IMemoryReviewTokenCodec codec = host.Services.GetRequiredService<IMemoryReviewTokenCodec>();

        string cursor = codec.IssueCursor(CursorFacts()).Value;

        string prepared = codec.IssuePreparedPlan(PreparedPlanFacts()).Value;

        foreach (string token in (string[])
                 [
                     "AB",
                     "AAAAA",
                     NonCanonicalBase64Url.WithUnusedBitSet(cursor),
                     NonCanonicalBase64Url.WithUnusedBitSet(prepared),
                 ])
        {
            await AssertRefusedAsync(
                driver,
                "/api/memory/saga/review/list",
                new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 50, token),
                ArcanumJsonContext.Default.SagaReviewListRequest,
                ErrorCodes.MemoryReview.InvalidToken);

            await AssertRefusedAsync(
                driver,
                "/api/memory/lexicon/review/list",
                new LexiconReviewListRequest(new LexiconCurationScope(LexiconScopeKind.Global, null), 50, token),
                ArcanumJsonContext.Default.LexiconReviewListRequest,
                ErrorCodes.MemoryReview.InvalidToken);

            await AssertRefusedAsync(
                driver,
                "/api/memory/covenant/review/list",
                new CovenantReviewListRequest(CovenantScope.Global, null, CovenantLane.Confirmed, 50, token),
                ArcanumJsonContext.Default.CovenantReviewListRequest,
                ErrorCodes.MemoryReview.InvalidToken);

            await AssertRefusedAsync(
                driver,
                "/api/memory/saga/review/apply",
                new SagaReviewBulkApplyRequest(
                    new SagaReviewBulkPrepareRequest(
                        Guid.CreateVersion7(),
                        SagaMemoryScopeKind.Global,
                        null,
                        MemoryReviewAction.Retire,
                        [new SagaReviewDecision(token, null)]),
                    token),
                ArcanumJsonContext.Default.SagaReviewBulkApplyRequest,
                ErrorCodes.MemoryReview.InvalidToken);
        }
    }

    [SkippableFact]
    public async Task The_key_reset_route_answers_an_unreadable_token_with_a_400_InvalidPreflight()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            covenant: true);

        MemoryErasureRouteDriver driver = new(host.CreateAuthenticatedClient());

        foreach (string token in (string[])["AB", "AAB", "AAAAA", "AAAAAB"])
        {
            await AssertRefusedAsync(
                driver,
                "/api/memory/erasure/reset-key",
                new MemoryErasureKeyResetRequest(token),
                ArcanumJsonContext.Default.MemoryErasureKeyResetRequest,
                ErrorCodes.MemoryErasure.InvalidPreflight);
        }
    }

    [SkippableFact]
    public async Task The_covenant_routes_answer_an_unreadable_cursor_or_preflight_token_with_a_400_InvalidCursor()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            covenant: true);

        MemoryErasureRouteDriver driver = new(host.CreateAuthenticatedClient());

        // A full-length token, so the long path through the decoder answers the way the short ones do.
        string longest = new string('A', 217) + "B";

        foreach (string token in (string[])["AB", "AAB", "AAAAA", "AAAAAB", longest])
        {
            await AssertRefusedAsync(
                driver,
                "/api/memory/covenant/list",
                new CovenantListRequest(
                    CovenantCursorScopeSelection.Global,
                    null,
                    null,
                    CovenantLifecycle.Set,
                    null,
                    50,
                    token),
                ArcanumJsonContext.Default.CovenantListRequest,
                ErrorCodes.Covenant.InvalidCursor);

            await AssertRefusedAsync(
                driver,
                "/api/memory/covenant/retire",
                new CovenantRetireRequest(
                    CovenantScope.Global,
                    null,
                    "route.token",
                    CovenantLane.Confirmed,
                    1,
                    Guid.CreateVersion7(),
                    token),
                ArcanumJsonContext.Default.CovenantRetireRequest,
                ErrorCodes.Covenant.InvalidCursor);
        }
    }

    private static async Task AssertRefusedAsync<TRequest>(
        MemoryErasureRouteDriver driver,
        string path,
        TRequest body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> info,
        string code)
    {
        using HttpResponseMessage refused = await driver.PostAsync(path, body, info);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        Assert.Equal(code, await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));
    }

    private static MemoryReviewCursorTokenFacts CursorFacts() => new(
        MemoryReviewStore.Saga,
        ScopeDigest,
        MarkerGeneration,
        MarkerRevision: 7,
        FrozenLowerEventSequence: 100,
        FrozenUpperEventSequence: 900,
        KeysetEventSequence: 650,
        KeysetVersionIdentity: Digest(0x20));

    private static MemoryReviewPreparedPlanTokenFacts PreparedPlanFacts() => new(
        MemoryReviewStore.Saga,
        ScopeDigest,
        MarkerGeneration,
        MarkerRevision: 9,
        FrozenLowerEventSequence: 102,
        FrozenUpperEventSequence: 902,
        OrderedRequestDigest: Digest(0x40));

    private static MemoryReviewDigest Digest(byte first)
    {
        byte[] bytes = new byte[MemoryReviewDigest.Size];

        bytes[0] = first;

        bytes[^1] = (byte)(first + 1);

        return new MemoryReviewDigest(bytes);
    }
}
