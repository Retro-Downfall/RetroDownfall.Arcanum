using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Run_locator_refuses_another_root_with_the_same_native_operator()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        var own = await f.IssueAsync(caller);

        var other = await f.IssueAsync(caller, f.Request with { IdempotencyKey = Guid.NewGuid() });

        Assert.True(own.IsSuccess, own.Error.Message);

        Assert.True(other.IsSuccess, other.Error.Message);

        GuildCallerContext run = await RunCallerAsync(f, caller, own.Value);

        Assert.True((await f.Store.GetAsync(run, own.Value.Quest.Id, default)).IsSuccess);

        Assert.True((await f.Store.GetAsync(run, other.Value.Quest.Id, default)).IsFailure);

        Assert.True((await f.Store.GetAsync(caller, other.Value.Quest.Id, default)).IsSuccess);
    }

    [Fact]
    public async Task Run_locator_allows_own_descendants_but_refuses_sibling_subtrees()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        var root = await f.IssueAsync(caller);

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext parent = await RunCallerAsync(f, caller, root.Value);

        GuildQuestStored first = await IssueChildAsync(f, parent, root.Value);

        GuildQuestStored sibling = await IssueChildAsync(f, parent, root.Value);

        GuildCallerContext child = await RunCallerAsync(f, caller, first);

        GuildQuestStored descendant = await IssueChildAsync(f, child, first);

        Assert.True((await f.Store.GetAsync(parent, first.Quest.Id, default)).IsSuccess);

        Assert.True((await f.Store.GetAsync(parent, descendant.Quest.Id, default)).IsSuccess);

        Assert.True((await f.Store.GetAsync(child, descendant.Quest.Id, default)).IsSuccess);

        Assert.True((await f.Store.GetAsync(child, sibling.Quest.Id, default)).IsFailure);
    }

    [Fact]
    public async Task Root_memory_grants_cannot_cross_the_canonical_campaign_boundary()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        Guid ownCampaign = Guid.NewGuid();

        Guid foreignCampaign = Guid.NewGuid();

        await f.AddCampaignAsync(ownCampaign);

        await f.AddCampaignAsync(foreignCampaign);

        GuildGrant allowed = f.Request.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Campaign, ownCampaign))] };

        var own = await f.IssueAsync(caller, f.Request with { CampaignId = ownCampaign, Grant = allowed });

        Assert.True(own.IsSuccess, own.Error.Message);

        GuildGrant foreign = allowed with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Campaign, foreignCampaign))] };

        Assert.True((await f.IssueAsync(caller, f.Request with { IdempotencyKey = Guid.NewGuid(), CampaignId = ownCampaign, Grant = foreign })).IsFailure);

        Assert.True((await f.IssueAsync(caller, f.Request with { IdempotencyKey = Guid.NewGuid(), CampaignId = null, Grant = foreign })).IsFailure);
    }

    [Fact]
    public async Task Root_session_memory_scope_requires_the_same_canonical_campaign()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        Guid ownCampaign = Guid.NewGuid();

        Guid foreignCampaign = Guid.NewGuid();

        await f.AddCampaignAsync(ownCampaign);

        await f.AddCampaignAsync(foreignCampaign);

        Guid ownSession = Guid.NewGuid();

        Guid foreignSession = Guid.NewGuid();

        await f.AddSessionAsync(ownSession, ownCampaign, binding: true);

        await f.AddSessionAsync(foreignSession, foreignCampaign, binding: true);

        GuildGrant allowed = f.Request.Grant with { Memory = [new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Session, ownSession))] };

        var own = await f.IssueAsync(caller, f.Request with { CampaignId = ownCampaign, Grant = allowed });

        Assert.True(own.IsSuccess, own.Error.Message);

        GuildGrant foreign = allowed with { Memory = [new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Session, foreignSession))] };

        Assert.True((await f.IssueAsync(caller, f.Request with { IdempotencyKey = Guid.NewGuid(), CampaignId = ownCampaign, Grant = foreign })).IsFailure);
    }

    private static async Task<GuildCallerContext> RunCallerAsync(Fixture f, GuildCallerContext caller, GuildQuestStored quest)
    {
        Result<GuildRecoveryContext> recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Quest.Id,
            quest.Quest.OperationId, quest.Quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        var run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, Guid.NewGuid(), default);

        Assert.True(run.IsSuccess, run.Error.Message);

        return run.Value;
    }

    private static async Task<GuildQuestStored> IssueChildAsync(Fixture f, GuildCallerContext caller, GuildQuestStored parent)
    {
        Guid key = Guid.NewGuid();

        Result<GuildQuestStored> issued = await f.Store.IssueAsync(caller,
            new(f.Generation, parent.Quest.Id, null, key, key), parent.Quest.Id,
            new(key, parent.Quest.Revision, null, "Child", "Compute a contribution", new()), default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        return issued.Value;
    }
}
