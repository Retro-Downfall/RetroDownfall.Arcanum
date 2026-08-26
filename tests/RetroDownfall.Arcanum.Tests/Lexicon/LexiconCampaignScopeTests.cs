using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

/// <summary>
/// Two Campaigns holding an entity of the same name, and a turn resolving its own.
/// </summary>
/// <remarks>
/// The Lexicon's scope is optional in a way Saga's is not. Every existing entity is installation-global
/// authored content, and stays exactly that: the scope column is <c>NOT NULL DEFAULT ''</c>, so an
/// upgrade needs no sweep and a turn that names no Campaign sees precisely what it saw before.
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconCampaignScopeTests : IAsyncLifetime
{

    private static readonly Guid CampaignA = new("A0000000-0000-4000-8000-0000000000AA");

    private static readonly Guid CampaignB = new("B0000000-0000-4000-8000-0000000000BB");

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private LexiconService? _service;

    public LexiconCampaignScopeTests(GrimoireFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _service = new LexiconService(_db, NullLogger<LexiconService>.Instance);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    /// <summary>
    /// The acceptance criterion: one name, two Campaigns, different facts, and each turn resolves its
    /// own.
    /// </summary>
    [Fact]
    public async Task Two_campaigns_hold_the_same_name_with_different_facts_and_each_resolves_its_own()
    {

        _ = await UpsertAsync("config", LexiconScope.ForCampaign(CampaignA), "ships from a TOML file");

        _ = await UpsertAsync("config", LexiconScope.ForCampaign(CampaignB), "ships from environment variables");

        Assert.Equal(
            ["ships from a TOML file"],
            await FactsAsync("config", LexiconScope.ForCampaign(CampaignA)));

        Assert.Equal(
            ["ships from environment variables"],
            await FactsAsync("config", LexiconScope.ForCampaign(CampaignB)));

    }

    /// <summary>
    /// A Campaign entity shadows the global one of the same name, so the model is never handed two
    /// contradictory answers to one term and left to choose.
    /// </summary>
    [Fact]
    public async Task A_campaign_entity_shadows_the_global_entity_of_the_same_name()
    {

        _ = await UpsertAsync("config", LexiconScope.Global, "the installation-wide answer");

        _ = await UpsertAsync("config", LexiconScope.ForCampaign(CampaignA), "this campaign's answer");

        Assert.Equal(
            ["this campaign's answer"],
            await FactsAsync("config", LexiconScope.ForCampaign(CampaignA)));

    }

    /// <summary>
    /// Shadowing is per name, not per turn: a global entity the Campaign has not overridden is still the
    /// answer for that name.
    /// </summary>
    [Fact]
    public async Task A_global_entity_the_campaign_has_not_overridden_still_matches()
    {

        _ = await UpsertAsync("deploy", LexiconScope.Global, "the installation-wide answer");

        _ = await UpsertAsync("config", LexiconScope.ForCampaign(CampaignA), "this campaign's answer");

        Result<IReadOnlyList<LexiconEntryDto>> matched = await _service!.MatchEntitiesAsync(
            ["config", "deploy"],
            limit: 10,
            LexiconScope.ForCampaign(CampaignA),
            CancellationToken.None);

        Assert.True(matched.IsSuccess);

        Assert.Equal(
            ["config", "deploy"],
            matched.Value.Select(static entry => entry.Name).Order(StringComparer.Ordinal));

    }

    /// <summary>
    /// A Campaign never reaches another Campaign's entity, even when nothing global answers the name.
    /// </summary>
    [Fact]
    public async Task A_campaign_never_matches_another_campaigns_entity()
    {

        _ = await UpsertAsync("config", LexiconScope.ForCampaign(CampaignA), "campaign A's answer");

        Result<IReadOnlyList<LexiconEntryDto>> matched = await _service!.MatchEntitiesAsync(
            ["config"],
            limit: 10,
            LexiconScope.ForCampaign(CampaignB),
            CancellationToken.None);

        Assert.True(matched.IsSuccess);

        Assert.Empty(matched.Value);

    }

    /// <summary>
    /// The default is the guarantee: a global-scoped turn sees the global entity and nothing a Campaign
    /// has written, which is exactly what it saw before scopes existed.
    /// </summary>
    [Fact]
    public async Task A_global_turn_sees_the_global_entity_and_no_campaign_entity()
    {

        _ = await UpsertAsync("config", LexiconScope.Global, "the installation-wide answer");

        _ = await UpsertAsync("config", LexiconScope.ForCampaign(CampaignA), "this campaign's answer");

        Assert.Equal(
            ["the installation-wide answer"],
            await FactsAsync("config", LexiconScope.Global));

    }

    /// <summary>
    /// An upsert appends to the entity in its own scope rather than creating a second row there, and it
    /// leaves the other scope's entity untouched.
    /// </summary>
    [Fact]
    public async Task An_upsert_appends_within_its_own_scope_and_leaves_the_other_scope_alone()
    {

        _ = await UpsertAsync("config", LexiconScope.Global, "the installation-wide answer");

        _ = await UpsertAsync("config", LexiconScope.ForCampaign(CampaignA), "first campaign fact");

        _ = await UpsertAsync("config", LexiconScope.ForCampaign(CampaignA), "second campaign fact");

        Assert.Equal(
            ["first campaign fact", "second campaign fact"],
            await FactsAsync("config", LexiconScope.ForCampaign(CampaignA)));

        Assert.Equal(
            ["the installation-wide answer"],
            await FactsAsync("config", LexiconScope.Global));

    }

    /// <summary>
    /// Deletion is per scope too. A Forbidden Art aimed at one Campaign's entity must not take the
    /// installation's with it.
    /// </summary>
    [Fact]
    public async Task Deleting_a_campaign_entity_leaves_the_global_entity_standing()
    {

        _ = await UpsertAsync("config", LexiconScope.Global, "the installation-wide answer");

        _ = await UpsertAsync("config", LexiconScope.ForCampaign(CampaignA), "this campaign's answer");

        Result<bool> deleted = await _service!.DeleteByNameAsync(
            "config",
            LexiconScope.ForCampaign(CampaignA),
            CancellationToken.None);

        Assert.True(deleted.IsSuccess);

        Assert.True(deleted.Value);

        Assert.Equal(
            ["the installation-wide answer"],
            await FactsAsync("config", LexiconScope.Global));

    }

    /// <summary>
    /// Inspection has to be able to say which scope an entity belongs to; a listing that could not would
    /// show two identically named rows and no way to tell them apart.
    /// </summary>
    [Fact]
    public async Task A_listing_reports_the_scope_each_entity_belongs_to()
    {

        _ = await UpsertAsync("config", LexiconScope.Global, "the installation-wide answer");

        _ = await UpsertAsync("config", LexiconScope.ForCampaign(CampaignA), "this campaign's answer");

        Result<IReadOnlyList<LexiconEntryDto>> listed = await _service!.ListAsync(CancellationToken.None);

        Assert.True(listed.IsSuccess);

        Assert.Equal(
            [null, CampaignA],
            listed.Value
                .Where(static entry => entry.Name == "config")
                .Select(static entry => entry.ScopeCampaignId)
                .OrderBy(static id => id));

    }

    private Task<Result<LexiconEntryDto>> UpsertAsync(string name, LexiconScope scope, string fact) =>
        _service!.UpsertAsync(name, "Concept", [fact], scope, CancellationToken.None);

    private async Task<string[]> FactsAsync(string name, LexiconScope scope)
    {

        Result<IReadOnlyList<LexiconEntryDto>> matched = await _service!.MatchEntitiesAsync(
            [name],
            limit: 10,
            scope,
            CancellationToken.None);

        Assert.True(matched.IsSuccess);

        return [.. matched.Value.SelectMany(static entry => entry.Facts)];

    }

}
