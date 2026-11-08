using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Conclave_legacy_cast_refuses_in_Guild_mode_without_an_authenticated_Quest_request()
    {
        ConclaveArchmage coordinator = new(null!, new MutableTestOptionsMonitor<ArcanumSettings>(new()
        {
            Features = new() { Guild = true },
        }));

        var result = await coordinator.CastAsync(new("Compute a result", WorkspacePath: "/workspace"), default);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guild.Unavailable, result.Error.Code);
    }

    [Fact]
    public async Task Conclave_coordinator_commits_before_scheduling_and_replays_one_identity()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.UseLocalAdmission(new LocalAdmission());

        RecordingScheduler scheduler = new();

        ConclaveArchmage coordinator = new(null!, new MutableTestOptionsMonitor<ArcanumSettings>(new() { Features = new() { Guild = true } }), f.Store, scheduler);

        GuildMutationIdentity mutation = new(f.Request.IdempotencyKey, GuildQuestIntent.Compute(f.Request).Value);

        Result<GuildQuestDto> first = await coordinator.IssueAsync(f.Caller, mutation, f.Request, default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(first.Value.Id, Assert.Single(scheduler.Quests).Id);

        Assert.Equal(1L, await f.CountAsync("guild_local_executions"));

        Assert.Equal(1L, await f.CountAsync("guild_quest_start_intents"));

        Assert.Equal(1L, await f.CountAsync("LongRunningOperations"));

        Result<GuildQuestDto> replay = await coordinator.IssueAsync(f.Caller, mutation, f.Request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(first.Value.Id, replay.Value.Id);

        Assert.Equal(first.Value.AgentId, replay.Value.AgentId);

        Assert.True(replay.Value.Receipt!.Replayed);

        Assert.Equal(1L, await f.CountAsync("Apprentices"));

        Result<GuildQuestDto> changed = await coordinator.IssueAsync(f.Caller,
            new(f.Request.IdempotencyKey, GuildQuestIntent.Compute(f.Request with { Goal = "different" }).Value),
            f.Request with { Goal = "different" }, default);

        Assert.True(changed.IsFailure);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, changed.Error.Code);
    }

    [Fact]
    public async Task Conclave_coordinator_child_preserves_canonical_lineage_and_stable_card()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.UseLocalAdmission(new LocalAdmission());

        RecordingScheduler scheduler = new();

        ConclaveArchmage coordinator = new(null!, new MutableTestOptionsMonitor<ArcanumSettings>(new() { Features = new() { Guild = true } }), f.Store, scheduler);

        Result<GuildQuestDto> parent = await coordinator.IssueAsync(f.Caller,
            new(f.Request.IdempotencyKey, GuildQuestIntent.Compute(f.Request).Value), f.Request, default);

        Assert.True(parent.IsSuccess, parent.Error.Message);

        GuildQuestStored row = (await f.Store.GetAsync(f.Caller, parent.Value.Id, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        GuildCallerContext caller = new(GuildCallerKind.Run, f.Caller.PrincipalId!.Value, Guid.NewGuid(), f.Host.InstallationGeneration, driver.Execution);

        GuildIssueChildRequest request = new(Guid.NewGuid(), row.Quest.Revision, null, "Child", "Compute a contribution", new());

        GuildMutationIdentity mutation = new(request.IdempotencyKey, GuildQuestIntent.Compute(row.Quest.Id, request).Value);

        Result<GuildQuestDto> child = await coordinator.IssueAsync(caller, mutation, row.Quest.Id, request, default);

        Assert.True(child.IsSuccess, child.Error.Message);

        Assert.Equal(parent.Value.Id, child.Value.ParentQuestId);

        Assert.Equal(parent.Value.RootQuestId, child.Value.RootQuestId);

        Assert.Equal(parent.Value.CampaignId, child.Value.CampaignId);

        Assert.NotEqual(parent.Value.SessionId, child.Value.SessionId);

        Result<GuildQuestDto> replay = await coordinator.IssueAsync(caller, mutation, row.Quest.Id, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(child.Value.AgentId, replay.Value.AgentId);

        Assert.Equal(child.Value.CardId, replay.Value.CardId);

        Assert.Equal(child.Value.Id, replay.Value.Id);

        Assert.Equal(2L, await f.CountAsync("Apprentices"));

        Assert.Equal(2L, await f.CountAsync("LongRunningOperations"));
    }

    [Fact]
    public async Task Conclave_coordinator_refuses_forged_intent_and_missing_components_without_legacy_fallback()
    {
        await using Fixture f = await Fixture.CreateAsync();

        RecordingScheduler scheduler = new();

        MutableTestOptionsMonitor<ArcanumSettings> settings = new(new() { Features = new() { Guild = true } });

        ConclaveArchmage coordinator = new(null!, settings, f.Store, scheduler);

        Result<GuildQuestDto> forged = await coordinator.IssueAsync(f.Caller, new(f.Request.IdempotencyKey, new string('0', 64)), f.Request, default);

        Assert.True(forged.IsFailure);

        Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, forged.Error.Code);

        ConclaveArchmage incomplete = new(null!, settings);

        Result<GuildQuestDto> unavailable = await incomplete.IssueAsync(f.Caller,
            new(f.Request.IdempotencyKey, GuildQuestIntent.Compute(f.Request).Value), f.Request, default);

        Assert.True(unavailable.IsFailure);

        Assert.Equal(ErrorCodes.Guild.Unavailable, unavailable.Error.Code);

        Assert.Empty(scheduler.Quests);

        Assert.Equal(0L, await f.CountAsync("guild_quests"));
    }

    [Fact]
    public async Task Conclave_continuation_commits_exact_answer_before_waking_same_Quest()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.UseLocalAdmission(new LocalAdmission());

        RecordingScheduler scheduler = new();

        ConclaveArchmage coordinator = new(null!, new MutableTestOptionsMonitor<ArcanumSettings>(new() { Features = new() { Guild = true } }), f.Store, scheduler);

        Result<GuildQuestDto> issued = await coordinator.IssueAsync(f.Caller,
            new(f.Request.IdempotencyKey, GuildQuestIntent.Compute(f.Request).Value), f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestStored row = (await f.Store.GetAsync(f.Caller, issued.Value.Id, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid continuation = Guid.NewGuid();

        Result<GuildQuestStored> waiting = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), continuation, default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        GuildContinueRequest request = new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Use the first option.");

        GuildMutationIdentity mutation = new(request.IdempotencyKey, GuildQuestIntent.Compute(row.Quest.Id, request).Value);

        Result<GuildQuestDto> resumed = await coordinator.ContinueAsync(f.Caller, mutation, row.Quest.Id, request, default);

        Assert.True(resumed.IsSuccess, resumed.Error.Message);

        Assert.Equal(row.Quest.Id, resumed.Value.Id);

        Assert.Equal(1L, await f.CountAsync("guild_continuations"));

        Assert.Equal(2, scheduler.Quests.Count);

        Assert.Equal(row.Quest.Id, scheduler.Quests[^1].Id);

        Result<GuildQuestDto> replay = await coordinator.ContinueAsync(f.Caller, mutation, row.Quest.Id, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Receipt!.Replayed);

        Assert.Equal(1L, await f.CountAsync("guild_continuations"));

        Assert.Equal(1L, await f.CountAsync("LongRunningOperations"));
    }

    [Fact]
    public async Task Conclave_cancellation_survives_disable_and_wakes_drain_without_new_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.UseLocalAdmission(new LocalAdmission());

        RecordingScheduler scheduler = new();

        MutableTestOptionsMonitor<ArcanumSettings> settings = new(new() { Features = new() { Guild = true } });

        ConclaveArchmage coordinator = new(null!, settings, f.Store, scheduler);

        Result<GuildQuestDto> issued = await coordinator.IssueAsync(f.Caller,
            new(f.Request.IdempotencyKey, GuildQuestIntent.Compute(f.Request).Value), f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        settings.CurrentValue.Features.Guild = false;

        GuildCancelQuestRequest request = new(Guid.NewGuid(), issued.Value.Revision, GuildCancellationReason.OperatorRequested);

        Result<GuildCancellationDto> canceled = await coordinator.RequestCancellationAsync(f.Caller,
            new(request.IdempotencyKey, GuildQuestIntent.Compute(issued.Value.Id, request).Value), issued.Value.Id, request, default);

        Assert.True(canceled.IsSuccess, canceled.Error.Message);

        Assert.True(canceled.Value.CancellationRequested);

        Assert.True(scheduler.Quests[^1].CancellationRequested);

        Assert.Equal(issued.Value.Id, scheduler.Quests[^1].Id);

        Assert.Equal(1L, await f.CountAsync("LongRunningOperations"));
    }

    private sealed class RecordingScheduler : IGuildLocalQuestScheduler
    {
        internal List<GuildQuestDto> Quests { get; } = [];

        public bool IsAvailable => true;

        public void Schedule(GuildQuestDto quest) => Quests.Add(quest);
    }
}
