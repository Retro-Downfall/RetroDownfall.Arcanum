using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed partial class LongRestSweepSchedulingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_caller_linked_request_is_refused_without_reassociation_or_retirement(bool previousBoot)
    {
        await using SchedulingHarness h = await SchedulingHarness.CreateAsync();

        LongRestConfigurationStamp oldStamp = h.F.Epoch.Capture().Stamp;

        LongRunningOperationCreateRequest request = new(LongRunningOperationKinds.LongRestSweep,
            LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint, "Caller-linked Long Rest request.", h.F.Clock.GetUtcNow());

        LongRunningOperationRequestIdentity identity = new(Guid.NewGuid(), oldStamp.ValueDigest, oldStamp.ValueDigest);

        LongRunningOperationRequestIdentityResult created = await h.F.Operations.ResolveOrCreateAsync(request, identity);

        Assert.Equal(LongRunningOperationRequestIdentityOutcome.Created, created.Outcome);

        Assert.NotNull(created.Operation);

        Guid operationId = created.Operation!.Id;

        LongRestSweepScope partition = LongRestSweepHarness.Require(LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null)));

        ILongRestSweepStore sweeps = new LongRestSweepStore(h.F.H.Saga.Context, h.F.Clock);

        LongRestSweepHarness.Require(await sweeps.BeginOrResumeAsync(operationId, partition, oldStamp, CancellationToken.None));

        Assert.Equal(identity, await h.F.Operations.FindRequestIdentityAsync(operationId));

        if (previousBoot)
        {
            await h.F.RebootAsync();

            // RebootAsync expires every fixture lease; this operation has never held one.
            await h.F.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = NULL WHERE Id = $operation;",
                ("$operation", operationId.ToString("N")));

            h.RecreateWorker();

            Assert.NotEqual(oldStamp.BootId, h.F.Epoch.Capture().Stamp.BootId);
        }
        else
        {
            ChangeGateAndRestore(h);

            Assert.Equal(oldStamp.BootId, h.F.Epoch.Capture().Stamp.BootId);

            Assert.NotEqual(oldStamp.Epoch, h.F.Epoch.Capture().Stamp.Epoch);
        }

        Assert.Equal(oldStamp.ValueDigest, h.F.Epoch.Capture().Stamp.ValueDigest);

        LongRunningOperation before = await h.AssertPendingAsync(operationId);

        Assert.Null(before.LeaseExpiresAt);

        Assert.Null(before.CheckpointReference);

        Assert.Null(before.StartedAt);

        Assert.Null(before.HeartbeatAt);

        Assert.Null(before.CompletedAt);

        Assert.Null(before.TerminalErrorCode);

        var scopeBefore = LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(operationId, CancellationToken.None));

        Assert.Equal(oldStamp, scopeBefore.Configuration);

        string evidenceBefore = await ReadLinkedRequestEvidenceAsync(h, operationId);

        long writes = await h.CountAsync("SELECT total_changes();");

        var refused = await h.Coordinator.RequestAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None);

        Assert.Equal(writes, await h.CountAsync("SELECT total_changes();"));

        Assert.True(refused.IsFailure);

        Assert.Equal(evidenceBefore, await ReadLinkedRequestEvidenceAsync(h, operationId));

        Assert.Equal(before, await h.ReadAsync(operationId));

        Assert.Equal(scopeBefore, LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(operationId, CancellationToken.None)));

        Assert.Equal(identity, await h.F.Operations.FindRequestIdentityAsync(operationId));

        Assert.Equal(1L, await h.CountAsync("SELECT count(*) FROM long_running_operation_request_identities;"));

        Assert.Equal(1L, await h.CountAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));

        Assert.Equal(1L, await h.CountAsync("SELECT count(*) FROM long_rest_sweeps;"));

        Assert.Equal(1L, await h.CountAsync("SELECT count(*) FROM long_rest_sweeps WHERE PhaseCode = 1;"));

        Assert.Equal(0L, await h.CountAsync("SELECT count(*) FROM LongRunningOperations o LEFT JOIN long_rest_sweeps s ON s.OperationId = o.Id WHERE o.Kind = 'long-rest-sweep' AND s.OperationId IS NULL;"));

        Assert.Null(LongRestSweepHarness.Require(await h.F.H.Store.ReadGenerationMetadataAsync(operationId, CancellationToken.None)));

        LongRunningOperationRequestIdentityResult replay = await h.F.Operations.ResolveOrCreateAsync(request, identity);

        Assert.Equal(LongRunningOperationRequestIdentityOutcome.Replayed, replay.Outcome);

        Assert.Equal(before, replay.Operation);

        Assert.False(h.Ownership.IsClaimed(operationId));

        Assert.True(h.Ownership.TryClaim(operationId, out Guid token));

        try
        {
            Assert.True(h.Ownership.IsClaimedBy(operationId, token));
        }
        finally
        {
            Assert.True(h.Ownership.Release(operationId, token));
        }

        Assert.Equal(0, h.F.Handler.SendCount);
    }

    private static async Task<string> ReadLinkedRequestEvidenceAsync(SchedulingHarness h, Guid operationId)
    {
        await using SqliteCommand command = h.F.H.Connection.CreateCommand();

        // Snapshot every stored column, including exact text and blob bytes, rather than only decoded DTO facts.
        command.CommandText = """
            SELECT json_array(
                quote(o.Id), quote(o.Kind), quote(o.State), quote(o.RecoveryPolicy), quote(o.RootOperationId),
                quote(o.ParentOperationId), quote(o.SessionId), quote(o.RunId), quote(o.InferenceRunId),
                quote(o.BudgetReservationId), quote(o.IdempotencyClaimId), quote(o.CreatedAt), quote(o.StartedAt),
                quote(o.HeartbeatAt), quote(o.CompletedAt), quote(o.LeaseOwner), quote(o.LeaseExpiresAt),
                quote(o.AttemptCount), quote(o.CheckpointVersion), quote(o.CheckpointPayload), quote(o.CheckpointReference),
                quote(o.PublicSummary), quote(o.TerminalErrorCode), quote(o.Revision),
                quote(s.OperationId), quote(s.GenerationId), quote(s.ScopeKindCode), quote(s.CampaignId),
                quote(s.PartitionDigest), quote(s.ConfigurationJson), quote(s.PhaseCode), quote(s.Revision),
                quote(s.CommittedItemCursor), quote(s.CommittedWorkDigest), quote(s.UpdatedAtUtc),
                quote(i.OperationId), quote(i.RequestedOperationId), quote(i.ApplyRequestDigest),
                quote(i.EffectDigest), quote(i.CreatedAtUtc))
            FROM LongRunningOperations o
            JOIN long_rest_sweeps s ON s.OperationId = o.Id
            JOIN long_running_operation_request_identities i ON i.OperationId = o.Id
            WHERE o.Id = $operation;
            """;

        command.Parameters.AddWithValue("$operation", operationId.ToString("N"));

        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
}
