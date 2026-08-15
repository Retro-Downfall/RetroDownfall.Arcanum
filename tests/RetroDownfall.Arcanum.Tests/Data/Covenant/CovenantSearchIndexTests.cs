using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Eligible FTS search, its deterministic order, and its coverage rules.
/// </summary>
public sealed class CovenantSearchIndexTests
{

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    private static readonly Guid CampaignTwo = CovenantOperationGateFixture.CampaignTwo;

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task An_eligible_index_answers_in_deterministic_class_order()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "pnpm",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Unrelated body.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "pnpm.workspace",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Another body.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "tooling",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Prefer pnpm over npm.",
            Token);

        await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        CovenantSearchPage page = await SearchAsync(fixture, "pnpm", CampaignOne);

        Assert.Equal(CovenantSearchExecutionMode.Fts, page.ExecutionMode);

        Assert.Equal(3, page.Hits.Length);

        // Exact key first, then key prefix, then ranked body matches.
        Assert.Equal(CovenantSearchMatchClass.ExactKey, page.Hits[0].MatchClass);

        Assert.Equal("pnpm", page.Hits[0].NormalizedKey);

        Assert.Equal(CovenantSearchMatchClass.KeyPrefix, page.Hits[1].MatchClass);

        Assert.Equal("pnpm.workspace", page.Hits[1].NormalizedKey);

        Assert.Equal(CovenantSearchMatchClass.Ranked, page.Hits[2].MatchClass);

        Assert.Equal("tooling", page.Hits[2].NormalizedKey);

    }

    [Fact]
    public async Task A_dirty_applied_tuple_falls_back_to_canonical()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Findable text.",
            Token);

        // No synchronization pass: canonical has moved and the accelerator has not.
        CovenantSearchPage page = await SearchAsync(fixture, "findable", null);

        Assert.Equal(CovenantSearchExecutionMode.CanonicalFallback, page.ExecutionMode);

        Assert.Equal(CovenantSearchRebuildGuidance.WaitForSynchronization, page.Guidance);

        _ = Assert.Single(page.Hits);

    }

    [Fact]
    public async Task An_absent_accelerator_still_answers_from_canonical()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Findable text.",
            Token);

        CovenantSearchPage page = await SearchAsync(fixture, "findable", null);

        Assert.Equal(CovenantSearchExecutionMode.CanonicalFallback, page.ExecutionMode);

        Assert.Equal(CovenantSearchRebuildGuidance.AcceleratorUnavailable, page.Guidance);

        _ = Assert.Single(page.Hits);

    }

    [Fact]
    public async Task Pages_continue_through_their_keyset()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        for (int index = 0; index < 5; index++)
        {

            _ = await fixture.SeedHeadAsync(
                CovenantScope.Global,
                null,
                $"shared.key{index}",
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                "Shared marker text.",
                Token);

        }

        await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        CovenantSearchPage first = await SearchAsync(fixture, "marker", null, pageSize: 2);

        Assert.Equal(2, first.Hits.Length);

        Assert.NotNull(first.NextKeyset);

        CovenantSearchPage second = await SearchAsync(fixture, "marker", null, pageSize: 2, after: first.NextKeyset);

        Assert.Equal(2, second.Hits.Length);

        Assert.Empty(first.Hits.Select(static hit => hit.EntryId).Intersect(second.Hits.Select(static hit => hit.EntryId)));

        CovenantSearchPage third = await SearchAsync(fixture, "marker", null, pageSize: 2, after: second.NextKeyset);

        _ = Assert.Single(third.Hits);

        Assert.Null(third.NextKeyset);

    }

    [Fact]
    public async Task Scope_filters_are_honoured_in_both_modes()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        await fixture.AddCampaignAsync(CampaignTwo, "two", Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "campaign.one",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Shared marker.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignTwo,
            "campaign.two",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Shared marker.",
            Token);

        await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        CovenantSearchPage scoped = await SearchAsync(fixture, "marker", CampaignOne);

        CovenantSearchHit only = Assert.Single(scoped.Hits);

        Assert.Equal(CampaignOne, only.CampaignId);

        CovenantSearchPage all = await SearchAllScopesAsync(fixture, "marker");

        Assert.Equal(2, all.Hits.Length);

    }

    [Fact]
    public async Task An_all_scopes_search_requires_the_installation_lease()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease scoped =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantSearchPage> refused = await new CovenantSearchIndex(
                new FixedCovenantConnectionSource(fixture.Connection))
            .SearchAsync(Query("marker", CovenantCursorScopeSelection.AllScopes, null), scoped, Token);

        Assert.Equal("Covenant.ForbiddenAuthority", refused.Error.Code);

    }

    [Fact]
    public async Task A_scoped_search_refuses_a_lease_over_another_campaign()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease other = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CampaignTwo),
            Token)).Value;

        Result<CovenantSearchPage> refused = await new CovenantSearchIndex(
                new FixedCovenantConnectionSource(fixture.Connection))
            .SearchAsync(Query("marker", CovenantCursorScopeSelection.Campaign, CampaignOne), other, Token);

        Assert.Equal("Covenant.ForbiddenAuthority", refused.Error.Code);

    }

    [Fact]
    public async Task A_released_lease_cannot_search()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantReadLease lease = (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        await lease.DisposeAsync();

        Result<CovenantSearchPage> refused = await new CovenantSearchIndex(
                new FixedCovenantConnectionSource(fixture.Connection))
            .SearchAsync(Query("marker", CovenantCursorScopeSelection.Global, null), lease, Token);

        Assert.Equal("Covenant.StaleSnapshot", refused.Error.Code);

    }

    [Fact]
    public async Task A_retired_head_indexes_its_key_but_not_its_text()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        SeededHead head = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "retiring.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Secret marker text.",
            Token);

        await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        Assert.Single((await SearchAsync(fixture, "marker", null)).Hits);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "retiring.key",
            CovenantLane.Confirmed,
            CovenantOperation.Retire,
            null,
            Token,
            entryId: head.EntryId,
            laneRevision: 2,
            predecessorVersionId: head.VersionId);

        await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        // The tombstone is still findable by key, but the text it replaced is gone from the index.
        Assert.Empty((await SearchAsync(fixture, "marker", null)).Hits);

        CovenantSearchPage byKey = await SearchAsync(fixture, "retiring.key", null, lifecycle: CovenantLifecycle.Any);

        CovenantSearchHit tombstone = Assert.Single(byKey.Hits);

        Assert.Equal(CovenantLifecycle.Retired, tombstone.Lifecycle);

    }

    [Fact]
    public async Task The_page_reports_the_sources_it_was_answered_from()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Marker text.",
            Token);

        await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        CovenantSearchPage page = await SearchAsync(fixture, "marker", null);

        Assert.Equal(await fixture.ReadDatasetGenerationAsync(Token), page.Sources.DatasetGeneration);

        Assert.True(page.Sources.AcceleratorEligible);

        Assert.Equal(page.Sources.CanonicalSearchSequence, page.Sources.AppliedSearchSequence);

    }

    internal static CovenantSearchQuery Query(
        string text,
        CovenantCursorScopeSelection selection,
        Guid? campaignId,
        int pageSize = 50,
        CovenantSearchKeyset? after = null,
        CovenantLifecycle lifecycle = CovenantLifecycle.Set)
    {

        Result<CovenantCompiledSearchTerms> compiled = new CovenantSearchQueryCompiler().Compile(text);

        Assert.True(compiled.IsSuccess);

        return new CovenantSearchQuery(compiled.Value, selection, campaignId, null, lifecycle, pageSize, after);

    }

    private static async Task<CovenantSearchPage> SearchAsync(
        CovenantCanonicalFixture fixture,
        string text,
        Guid? campaignId,
        int pageSize = 50,
        CovenantSearchKeyset? after = null,
        CovenantLifecycle lifecycle = CovenantLifecycle.Set)
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantOperationScope scope = campaignId is { } id
            ? CovenantOperationScope.ForCampaign(id)
            : CovenantOperationScope.Global;

        await using CovenantReadLease lease = (await gate.AcquireReadAsync(scope, Token)).Value;

        Result<CovenantSearchPage> page = await new CovenantSearchIndex(
                new FixedCovenantConnectionSource(fixture.Connection))
            .SearchAsync(
                Query(
                    text,
                    campaignId is null ? CovenantCursorScopeSelection.Global : CovenantCursorScopeSelection.Campaign,
                    campaignId,
                    pageSize,
                    after,
                    lifecycle),
                lease,
                Token);

        Assert.True(page.IsSuccess, page.IsFailure ? page.Error.Message : null);

        return page.Value;

    }

    private static async Task<CovenantSearchPage> SearchAllScopesAsync(CovenantCanonicalFixture fixture, string text)
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantInstallationReadLease lease = (await gate.AcquireInstallationReadAsync(Token)).Value;

        Result<CovenantSearchPage> page = await new CovenantSearchIndex(
                new FixedCovenantConnectionSource(fixture.Connection))
            .SearchAsync(Query(text, CovenantCursorScopeSelection.AllScopes, null), lease, Token);

        Assert.True(page.IsSuccess, page.IsFailure ? page.Error.Message : null);

        return page.Value;

    }

}
