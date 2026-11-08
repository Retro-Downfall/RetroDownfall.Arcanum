using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseStoreTests
{
    [Fact]
    public async Task Acquire_keeps_original_acceptance_snapshot_while_exact_replay_observes_current_terminal_lease()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord original = Row();

        _ = await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, original, default);

        var accepted = await GuildWorkspaceLeaseStore.FindMutationReplayWithinAsync(connection, transaction, original.Owner,
            original.IdempotencyKey, original.RequestDigest, GuildAuthorityAction.LeaseAcquire, original.Id, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        Assert.Equal(original, accepted.Value.Result);

        Assert.Equal(original.HeartbeatAt, accepted.Value.AcceptedAt);

        GuildWorkspaceLeaseRecord renewed = (await GuildWorkspaceLeaseStore.RenewWithinAsync(connection, transaction,
            original, original.HeartbeatAt.AddMinutes(5), original.ExpiresAt.AddMinutes(5), default)).Value;

        _ = await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, renewed, "released", default);

        var current = await GuildWorkspaceLeaseStore.FindReplayWithinAsync(connection, transaction, original.Owner,
            original.IdempotencyKey, original.RequestDigest, default);

        Assert.Equal(GuildWorkspaceLeaseState.Released, current.Value.State);

        var replay = await GuildWorkspaceLeaseStore.FindMutationReplayWithinAsync(connection, transaction, original.Owner,
            original.IdempotencyKey, original.RequestDigest, GuildAuthorityAction.LeaseAcquire, original.Id, default);

        Assert.Equal(accepted.Value, replay.Value);

        Assert.Equal(1, replay.Value.Result.Revision);

        Assert.Equal(original.ExpiresAt, replay.Value.Result.ExpiresAt);
    }

    [Theory]
    [InlineData("heartbeatAt")]
    [InlineData("inFlightMutationCount")]
    public async Task Missing_persisted_snapshot_members_refuse_replay_and_integrity(string member)
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord row = Row();

        _ = await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default);

        row = (await GuildWorkspaceLeaseStore.RenewWithinAsync(connection, transaction, row,
            row.HeartbeatAt.AddMinutes(1), row.ExpiresAt.AddMinutes(1), default)).Value;

        GuildMutationIdentity mutation = new(Guid.NewGuid(), new string('B', 64));

        var receipt = await GuildWorkspaceLeaseStore.RecordMutationWithinAsync(connection, transaction, row, mutation,
            GuildAuthorityAction.LeaseRenew, row.HeartbeatAt, default);

        Assert.True(receipt.IsSuccess, receipt.Error.Message);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT SnapshotJson FROM guild_workspace_lease_mutations WHERE Id=$id";

        command.Parameters.AddWithValue("$id", receipt.Value.Id.ToString("D").ToUpperInvariant());

        JsonObject snapshot = JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!.AsObject();

        Assert.True(snapshot.Remove(member));

        command.CommandText = "DROP TRIGGER guild_workspace_lease_mutations_guard_update";

        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE guild_workspace_lease_mutations SET SnapshotJson=$snapshot WHERE Id=$id";

        command.Parameters.AddWithValue("$snapshot", snapshot.ToJsonString());

        _ = await command.ExecuteNonQueryAsync();

        var replay = await GuildWorkspaceLeaseStore.FindMutationReplayWithinAsync(connection, transaction, row.Owner,
            mutation.IdempotencyKey, mutation.CanonicalRequestDigest, GuildAuthorityAction.LeaseRenew, row.Id, default);

        Assert.Equal(ErrorCodes.Guild.Unavailable, replay.Error.Code);

        Assert.True((await GuildWorkspaceLeaseStore.ValidateMutationReceiptsWithinAsync(connection, transaction, default)).IsFailure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Host_stop_proof_never_erases_inflight_or_uncertain_managed_artifacts(int disposition)
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord row = Row();

        _ = await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default);

        row = (await GuildWorkspaceLeaseStore.AdmitPermitWithinAsync(connection, transaction, row, row.HeartbeatAt, default)).Value;

        if (disposition == 1)
            row = (await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, row, "expired", default)).Value;

        if (disposition == 2)
            row = (await GuildWorkspaceLeaseStore.FinishPermitWithinAsync(connection, transaction, row.Id, row.Epoch, row.HostInstanceId, true, default)).Value;

        if (disposition == 3)
            row = (await GuildWorkspaceLeaseStore.RetainRecoveryWithinAsync(connection, transaction, row, default)).Value;

        string directory = Path.Combine(Path.GetTempPath(), "arcanum-lease-artifacts-" + Guid.NewGuid().ToString("N"));

        try
        {
            using GuildWorkspaceLeaseHostLock host = GuildWorkspaceLeaseHostLock.TryAcquire(Path.Combine(directory, "broker.lock")).Value;

            var proof = host.ProvePriorManagedHostStopped().Value;

            using IDisposable pin = proof.Pin().Value;

            var recovered = await GuildWorkspaceLeaseStore.RecoverWithinAsync(connection, transaction, row, proof, default);

            Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, recovered.Error.Code);

            Assert.Equal(row, (await GuildWorkspaceLeaseStore.ReadWithinAsync(connection, transaction, row.Id, default)).Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
