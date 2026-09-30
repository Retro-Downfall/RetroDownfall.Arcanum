using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

/// <summary>
/// Agent deletion of curated Lexicon entries: an agent may remove an active, unpinned entry, while a
/// retired or pinned one is the operator's to manage, and the operator's own delete is unchanged.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconAgentDeletionTests(GrimoireFixture fixture)
{
    [SkippableTheory]
    [InlineData("retired")]
    [InlineData("pinned")]
    public async Task Agent_origin_delete_refuses_curated_entries_inside_its_transaction(string state)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using CorrectionFixture owner = new(fixture, annals: true);

        await CurateAsync(owner, state);

        string[] snapshot = await owner.SnapshotAsync();

        Result<bool> deleted = await owner.Concrete.DeleteByNameAsync("Entity", LexiconScope.Global, LexiconDeletionOrigin.Agent);

        Assert.Equal(state == "retired" ? ErrorCodes.Lexicon.RetiredMutationRefused : ErrorCodes.Lexicon.PinnedMutationRefused, deleted.Error.Code);

        Assert.Equal(state == "retired" ? LexiconAgentRefusals.RetiredDeletion : LexiconAgentRefusals.OperatorManaged, deleted.Error.Message);

        Assert.Equal(snapshot, await owner.SnapshotAsync());
    }

    [SkippableTheory]
    [InlineData("retired")]
    [InlineData("pinned")]
    public async Task Operator_origin_delete_still_removes_retired_and_pinned_entries(string state)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using CorrectionFixture owner = new(fixture, annals: true);

        await CurateAsync(owner, state);

        Assert.NotEqual(0L, await owner.ScalarAsync("SELECT count(*) FROM annal_claims"));

        Result<bool> deleted = await owner.Concrete.DeleteByNameAsync("Entity", LexiconScope.Global, LexiconDeletionOrigin.Operator);

        Assert.True(deleted.IsSuccess, deleted.Error.Message);

        Assert.True(deleted.Value);

        Assert.Equal(0L, await owner.ScalarAsync("SELECT count(*) FROM lexicon_entries"));

        Assert.Equal(0L, await owner.ScalarAsync("SELECT count(*) FROM annal_claims"));
    }

    [SkippableFact]
    public async Task Agent_deletion_target_reports_exact_scope_lifecycle()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using CorrectionFixture owner = new(fixture, annals: true);

        LexiconEntryDetail before = await owner.SeedAsync();

        ILexiconService service = owner.Concrete;

        Assert.Equal(new LexiconAgentDeletionTarget(before.Entry.Id, false, false), await FindAsync(service, " entity ", LexiconScope.Global));

        var pinned = await owner.Service.PinAsync(before.Target, null);

        Assert.True(pinned.IsSuccess, pinned.Error.Message);

        Assert.Equal(new LexiconAgentDeletionTarget(before.Entry.Id, false, true), await FindAsync(service, "Entity", LexiconScope.Global));

        var unpinned = await owner.Service.UnpinAsync(pinned.Value.Entry.Target, null);

        Assert.True(unpinned.IsSuccess, unpinned.Error.Message);

        var retired = await owner.Service.RetireAsync(unpinned.Value.Entry.Target, null);

        Assert.True(retired.IsSuccess, retired.Error.Message);

        Assert.Equal(new LexiconAgentDeletionTarget(before.Entry.Id, true, false), await FindAsync(service, "ENTITY", LexiconScope.Global));

        Assert.Null(await FindAsync(service, "Missing", LexiconScope.Global));

        Guid campaign = Guid.NewGuid();

        Result<LexiconEntryDto> scoped = await owner.Concrete.UpsertAsync("Scoped", "Person", ["alpha"], LexiconScope.ForCampaign(campaign));

        Assert.True(scoped.IsSuccess, scoped.Error.Message);

        Assert.Null(await FindAsync(service, "Scoped", LexiconScope.Global));

        Assert.Equal(new LexiconAgentDeletionTarget(scoped.Value.Id, false, false), await FindAsync(service, "Scoped", LexiconScope.ForCampaign(campaign)));
    }

    private static async Task<LexiconAgentDeletionTarget?> FindAsync(ILexiconService service, string name, LexiconScope scope)
    {
        Result<LexiconAgentDeletionTarget?> found = await service.FindAgentDeletionTargetAsync(name, scope);

        Assert.True(found.IsSuccess, found.Error.Message);

        return found.Value;
    }

    private static async Task CurateAsync(CorrectionFixture owner, string state)
    {
        LexiconEntryDetail before = await owner.SeedAsync();

        Result<LexiconCurationResult> curated = state switch
        {
            "retired" => await owner.Service.RetireAsync(before.Target, null),
            "pinned" => await owner.Service.PinAsync(before.Target, null),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown curation state."),
        };

        Assert.True(curated.IsSuccess, curated.Error.Message);
    }
}
