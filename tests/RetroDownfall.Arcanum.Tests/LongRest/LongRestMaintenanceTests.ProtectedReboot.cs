using System.Net;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    // G1c: a protected sweep parked before a restart must not strand its scope. Parked-Is-Active returns it unchanged through the
    // real request entry whichever boot parked it, and only a fresh capability readmits it.
    [Fact]
    public async Task A_protected_sweep_parked_before_a_reboot_is_returned_unchanged_by_the_real_request_and_a_fresh_capability_finishes_it()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        List<string> bodies = [];

        f.Handler.InsideSend = () =>
        {
            bodies.Add(f.Handler.LastBody!);

            // The summary is accepted; the embedding that follows fails with an unknown transport result.
            f.Handler.Status = f.Handler.LastBody!.Contains("long_rest_summary", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.InternalServerError;

            f.Handler.ChatText = "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        LongRestSweepProgress parked = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None));

        Assert.Equal(LongRestParkCodes.AuthorizedRequestRequired, parked.DeferredReason);

        string original = (string)(await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"))!;

        Assert.Equal(2, f.Handler.SendCount);

        await f.RebootAsync();

        string before = await ParkedSnapshotAsync(f);

        long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Result<LongRunningOperationDto> requested = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error.Code : null);

        Assert.Equal(f.Operation, requested.Value.Id);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, requested.Value.State);

        // Returned unchanged: the request wrote nothing, and sent nothing.
        Assert.Equal(before, await ParkedSnapshotAsync(f));

        Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);

        Assert.Equal(2, f.Handler.SendCount);

        // A fresh capability readmits it on the new boot; the original paid summary is reused and only the embedding is sent again.
        f.Handler.InsideSend = () =>
        {
            bodies.Add(f.Handler.LastBody!);

            f.Handler.Status = HttpStatusCode.OK;

            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        f.Handler.Status = HttpStatusCode.OK;

        LongRestSweepProgress completed = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, completed.Phase);

        Assert.Equal(original, await f.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"));

        Assert.Single(bodies, static body => body.Contains("long_rest_summary", StringComparison.Ordinal));
    }
}
