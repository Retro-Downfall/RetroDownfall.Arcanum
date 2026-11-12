using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Cohort_snapshot_exposes_members_and_scoped_resume_watermark()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 2);

        GuildPortalStore portal = new(f.Store, f.Authority, f.Policy, TimeProvider.System);

        Result<GuildCohortSnapshotDto> result = await portal.CaptureCohortSnapshotAsync(owner, cohort.Id, cohorts, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(cohort.Id, result.Value.Summary.Id);

        Assert.Equal(members.Count, result.Value.Members.Items.Length);

        var replay = await portal.ReadScopedReplayAsync(owner, root.Quest.Id, null, cohort.Id, result.Value.ResumeCursor, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Empty(replay.Value.Page.Items);

        Assert.True(replay.Value.CaughtUp);
    }
    [Fact]
    public async Task Operator_can_snapshot_a_draining_cohort()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, _, cohorts, cohort, _) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        await f.ExecuteAsync($"UPDATE guild_cohorts SET StateCode=3,Revision=Revision+1 WHERE Id='{cohort.Id:D}'".ToUpperInvariant());

        GuildPortalStore portal = new(f.Store, f.Authority, f.Policy, TimeProvider.System);

        Result<GuildCohortSnapshotDto> result = await portal.CaptureCohortSnapshotAsync(owner, cohort.Id, cohorts, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(GuildCohortState.Draining, result.Value.Summary.State);
    }

    [Fact]
    public async Task Draining_cohort_watch_includes_current_members_admitted_work()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        await f.ExecuteAsync($"UPDATE guild_cohorts SET StateCode=3,Revision=Revision+1 WHERE Id='{cohort.Id:D}'".ToUpperInvariant());

        GuildPortalStore portal = new(f.Store, f.Authority, f.Policy, TimeProvider.System);

        Result<GuildCohortSnapshotDto> snapshot = await portal.CaptureCohortSnapshotAsync(owner, cohort.Id, cohorts, default);

        Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

        Guid source = Guid.NewGuid();

        Assert.True((await f.Authority.InQuestTransactionAsync<long>(root.Quest.Id,
            (connection, transaction, _, token) => GuildPortalEvents.AppendWithinAsync(connection, transaction,
                root.Quest.Id, members[0].Quest.Id, GuildEventKind.QuestChanged, source, 1, null, DateTimeOffset.UtcNow, token), default)).IsSuccess);

        Result<GuildPortalReplayPage> replay = await portal.ReadScopedReplayAsync(owner, root.Quest.Id, null, cohort.Id,
            snapshot.Value.ResumeCursor, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(source, Assert.Single(replay.Value.Page.Items).ObjectId);
    }

    [Fact]
    public async Task Cohort_watch_does_not_claim_terminal_while_members_are_active()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, root, cohorts, cohort, _) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        GuildPortalStore portal = new(f.Store, f.Authority, f.Policy, TimeProvider.System);

        Result<GuildCohortSnapshotDto> snapshot = await portal.CaptureCohortSnapshotAsync(owner, cohort.Id, cohorts, default);

        Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

        Result<GuildTerminalEventDto?> terminal = await portal.ReadCohortTerminalAsync(owner, root.Quest.Id,
            cohort.Id, snapshot.Value.ResumeCursor, default);

        Assert.True(terminal.IsSuccess, terminal.Error.Message);

        Assert.Null(terminal.Value);
    }

    [Fact]
    public async Task Settled_drained_cohort_produces_a_terminal_projection()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        await CommitConvergenceResultAsync(f, owner, members[0], "cohort result");

        var member = await f.Store.GetAsync(owner, members[0].Quest.Id, default);

        var parent = await f.Store.GetAsync(owner, root.Quest.Id, default);

        Assert.True(member.IsSuccess, member.Error.Message);

        Assert.True(parent.IsSuccess, parent.Error.Message);

        var applied = await f.Store.ApplyResultAsync(owner,
            new(new(Guid.NewGuid(), Convert.ToHexString(SHA256.HashData("stream cohort apply"u8))), root.Quest.Id,
                member.Value.Quest.Id, member.Value.Quest.ResultReceiptId!.Value, member.Value.Quest.ResultRef!.Digest,
                parent.Value.Quest.Revision), default);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Assert.True((await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default)).IsSuccess);

        CohortConvergenceDto? convergence = null;

        for (int turn = 0; turn < 40 && convergence?.State != GuildCohortState.Completed; turn++)
        {
            Result<bool> stopped = await f.Store.RefreshCancellationConvergenceAsync(owner, members[0].Quest.Id, default);

            Assert.True(stopped.IsSuccess, stopped.Error.Message);

            var advanced = await cohorts.ReconcileAsync(owner, cohort.Id, default);

            Assert.True(advanced.IsSuccess || advanced.Error.Code == ErrorCodes.Guild.MaintenanceDeferred, advanced.Error.Message);

            if (advanced.IsSuccess) convergence = advanced.Value;
        }

        Assert.Equal(GuildCohortState.Completed, convergence?.State);

        await CommitConvergenceResultAsync(f, owner, root, "root result", retainedRun: issuer);

        GuildPortalStore portal = new(f.Store, f.Authority, f.Policy, TimeProvider.System);

        Result<int> drained = await portal.DrainOutboxAsync(owner, root.Quest.Id, default);

        Assert.True(drained.IsSuccess, drained.Error.Message);

        Result<GuildCohortSnapshotDto> snapshot = await portal.CaptureCohortSnapshotAsync(owner, cohort.Id, cohorts, default);

        Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

        Result<GuildTerminalEventDto?> terminal = await portal.ReadCohortTerminalAsync(owner, root.Quest.Id, cohort.Id,
            snapshot.Value.ResumeCursor, default);

        Assert.True(terminal.IsSuccess, terminal.Error.Message);

        Assert.NotNull(terminal.Value);

        Assert.Equal(QuestState.Completed, terminal.Value.State);

        Assert.Equal(GuildAccountingState.Settled, terminal.Value.AccountingState);
    }

}
