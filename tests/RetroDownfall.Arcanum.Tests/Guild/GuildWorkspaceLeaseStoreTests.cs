using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseStoreTests
{
    [Fact]
    public async Task Exact_replay_survives_release_and_changed_digest_refuses()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord row = Row();

        Assert.True((await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default)).IsSuccess);

        var released = await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, row, "released", default);

        Assert.True(released.IsSuccess, released.Error.Message);

        Assert.Equal(GuildWorkspaceLeaseState.Released, released.Value.State);

        var replay = await GuildWorkspaceLeaseStore.FindReplayWithinAsync(connection, transaction, row.Owner, row.IdempotencyKey, row.RequestDigest, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(released.Value, replay.Value);

        var changed = await GuildWorkspaceLeaseStore.FindReplayWithinAsync(connection, transaction, row.Owner, row.IdempotencyKey, new string('B', 64), default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, changed.Error.Code);
    }

    [Fact]
    public async Task Expiry_and_release_never_reassign_live_admitted_writer_and_stale_epoch_cannot_finish()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord row = Row();

        Assert.True((await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default)).IsSuccess);

        var admitted = await GuildWorkspaceLeaseStore.AdmitPermitWithinAsync(connection, transaction, row, row.HeartbeatAt, default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        var drained = await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, admitted.Value, "expired", default);

        Assert.True(drained.IsSuccess, drained.Error.Message);

        Assert.Equal(GuildWorkspaceLeaseState.Draining, drained.Value.State);

        Assert.Equal(1, drained.Value.InFlightMutationCount);

        Assert.True((await GuildWorkspaceLeaseStore.RenewWithinAsync(connection, transaction, drained.Value, row.ExpiresAt, row.ExpiresAt.AddMinutes(15), default)).IsFailure);

        Assert.True((await GuildWorkspaceLeaseStore.FinishPermitWithinAsync(connection, transaction, row.Id, row.Epoch + 1, row.HostInstanceId, false, default)).IsFailure);

        var finished = await GuildWorkspaceLeaseStore.FinishPermitWithinAsync(connection, transaction, row.Id, row.Epoch, row.HostInstanceId, false, default);

        Assert.True(finished.IsSuccess, finished.Error.Message);

        Assert.Equal(GuildWorkspaceLeaseState.Released, finished.Value.State);

        Assert.Equal(0, finished.Value.InFlightMutationCount);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        return connection;
    }

    internal static GuildWorkspaceLeaseRecord Row()
    {
        DateTimeOffset now = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

        return new(Guid.NewGuid(), new(GuildWorkspaceLeaseOwnerKind.Operator, Guid.NewGuid(), null, null,
            null, null, Guid.NewGuid(), Guid.NewGuid()), "/workspace", "{}", new string('A', 64), Guid.NewGuid(),
            1, 1, GuildWorkspaceLeaseState.Held, now, now.AddMinutes(15), 0,
            GuildWorkspaceLeaseEffectKind.Managed, null, Guid.NewGuid(), new string('A', 64));
    }

    [Fact]
    public async Task Owner_and_epoch_and_revision_cannot_be_borrowed_for_renewal_or_release()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord row = Row();

        Assert.True((await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default)).IsSuccess);

        foreach (GuildWorkspaceLeaseRecord forged in new[]
        {
            row with { Epoch = row.Epoch + 1 },
            row with { Revision = row.Revision + 1 },
            row with { Owner = row.Owner with { PrincipalId = Guid.NewGuid() } },
            row with { Owner = row.Owner with { SessionId = Guid.NewGuid() } },
            row with { Owner = row.Owner with { CampaignId = Guid.NewGuid() } },
            row with { IdentityDigest = new string('B', 64) },
            row with { HostInstanceId = Guid.NewGuid() },
        })
        {
            Assert.True((await GuildWorkspaceLeaseStore.RenewWithinAsync(connection, transaction, forged, row.HeartbeatAt, row.ExpiresAt.AddMinutes(15), default)).IsFailure);

            Assert.True((await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, forged, "released", default)).IsFailure);
        }

        Assert.Equal(row, (await GuildWorkspaceLeaseStore.ReadWithinAsync(connection, transaction, row.Id, default)).Value);
    }

    [Fact]
    public async Task Quiescent_old_host_managed_recovery_rotates_epoch_but_process_is_never_guessed_dead()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        foreach (GuildWorkspaceLeaseEffectKind kind in Enum.GetValues<GuildWorkspaceLeaseEffectKind>())
        {
            GuildWorkspaceLeaseRecord row = Row() with { EffectKind = kind };

            Assert.True((await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default)).IsSuccess);

            string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-lease-recovery-" + Guid.NewGuid().ToString("N"), "broker.lock");

            using GuildWorkspaceLeaseHostLock staleHost = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath).Value;

            var staleProof = staleHost.ProvePriorManagedHostStopped().Value;

            staleHost.Dispose();

            Assert.True((await GuildWorkspaceLeaseStore.RecoverWithinAsync(connection, transaction, row, staleProof, default)).IsFailure);

            using GuildWorkspaceLeaseHostLock liveHost = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath).Value;

            var liveProof = liveHost.ProvePriorManagedHostStopped().Value;

            using IDisposable pin = liveProof.Pin().Value;

            var recovered = await GuildWorkspaceLeaseStore.RecoverWithinAsync(connection, transaction, row, liveProof, default);

            Assert.Equal(kind == GuildWorkspaceLeaseEffectKind.Managed, recovered.IsSuccess);

            if (recovered.IsSuccess)
            {
                Assert.Equal(row.Epoch + 1, recovered.Value.Epoch);

                Assert.Equal(GuildWorkspaceLeaseState.Released, recovered.Value.State);

                Assert.True((await GuildWorkspaceLeaseStore.FinishPermitWithinAsync(connection, transaction, row.Id, row.Epoch, row.HostInstanceId, false, default)).IsFailure);
            }
            else Assert.Equal(0, (await GuildWorkspaceLeaseStore.ReadWithinAsync(connection, transaction, row.Id, default)).Value.InFlightMutationCount);
        }
    }

    [Fact]
    public async Task Native_operator_principal_is_stable_and_never_adopts_another_installation_generation()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Guid generation = Guid.NewGuid();

        var first = await GuildWorkspaceLeaseStore.GetNativeOperatorPrincipalWithinAsync(connection, transaction, generation, default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(first.Value, (await GuildWorkspaceLeaseStore.GetNativeOperatorPrincipalWithinAsync(connection, transaction, generation, default)).Value);

        Assert.True((await GuildWorkspaceLeaseStore.GetNativeOperatorPrincipalWithinAsync(connection, transaction, Guid.NewGuid(), default)).IsFailure);
    }

    [Fact]
    public async Task Uncertain_effect_stays_protected_even_after_its_local_permit_has_drained()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord row = Row();

        _ = await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default);

        row = (await GuildWorkspaceLeaseStore.AdmitPermitWithinAsync(connection, transaction, row, row.HeartbeatAt, default)).Value;

        var finished = await GuildWorkspaceLeaseStore.FinishPermitWithinAsync(connection, transaction, row.Id, row.Epoch, row.HostInstanceId, true, default);

        Assert.Equal(GuildWorkspaceLeaseState.ReconciliationRequired, finished.Value.State);

        Assert.Equal(0, finished.Value.InFlightMutationCount);

        List<Guid> protectedIds = [];

        Assert.True((await GuildWorkspaceLeaseStore.VisitProtectedWithinAsync(connection, transaction, (item, _) =>
        {
            protectedIds.Add(item.Id);

            return Task.FromResult(Result.Success());
        }, default)).IsSuccess);

        Assert.Equal([row.Id], protectedIds);
    }

    [Fact]
    public async Task Owner_cleanup_seals_live_writer_then_retains_replay_evidence_after_drain()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord row = Row();

        Guid campaign = Guid.NewGuid();

        row = row with { Owner = row.Owner with { CampaignId = campaign } };

        _ = await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default);

        row = (await GuildWorkspaceLeaseStore.AdmitPermitWithinAsync(connection, transaction, row, row.HeartbeatAt, default)).Value;

        GuildWorkspaceLeaseCleanupParticipant participant = new(new FixtureOrdinaryConnectionFactory());

        GuildCleanupOwner owner = new(1, campaign, null);

        Assert.True((await participant.SealAsync(owner, connection, transaction, default)).IsSuccess);

        Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, (await participant.DeleteAsync(owner, connection, transaction, default)).Error.Code);

        var held = await GuildWorkspaceLeaseStore.ReadWithinAsync(connection, transaction, row.Id, default);

        Assert.Equal(GuildWorkspaceLeaseState.Draining, held.Value.State);

        _ = await GuildWorkspaceLeaseStore.FinishPermitWithinAsync(connection, transaction, row.Id, row.Epoch, row.HostInstanceId, false, default);

        Assert.True((await participant.DeleteAsync(owner, connection, transaction, default)).IsSuccess);

        Assert.Equal(GuildWorkspaceLeaseState.Released, (await GuildWorkspaceLeaseStore.FindReplayWithinAsync(connection, transaction, row.Owner, row.IdempotencyKey, row.RequestDigest, default)).Value.State);
    }

    [Fact]
    public async Task Unavailable_schema_never_runs_the_mutation_callback_or_claims_empty_ownership()
    {
        await using GuildSignetAuthorityTests.Fixture fixture = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using (SqliteConnection connection = new(fixture.ConnectionString))
        {
            await connection.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "DROP TABLE guild_workspace_leases";

            _ = await command.ExecuteNonQueryAsync();
        }

        GuildWorkspaceLeaseStore store = new(new FixtureOrdinaryConnectionFactory(fixture.ConnectionString));

        bool invoked = false;

        var result = await store.InTransactionAsync<bool>((_, _, _) =>
        {
            invoked = true;

            return Task.FromResult(Result<bool>.Success(true));
        }, default);

        Assert.Equal(ErrorCodes.Guild.Unavailable, result.Error.Code);

        Assert.False(invoked);
    }

    [Fact]
    public async Task Restore_keeps_outstanding_workspace_ownership_inactive_and_retains_its_uncertainty()
    {
        await using GuildSignetAuthorityTests.Fixture fixture = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = new(fixture.ConnectionString);

        await connection.OpenAsync();

        GuildWorkspaceLeaseRecord row = Row();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            _ = await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default);

            row = (await GuildWorkspaceLeaseStore.AdmitPermitWithinAsync(connection, transaction, row, row.HeartbeatAt, default)).Value;

            await transaction.CommitAsync();
        }

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(connection, default)).IsSuccess);

        await using SqliteTransaction read = connection.BeginTransaction(deferred: true);

        var restored = await GuildWorkspaceLeaseStore.ReadWithinAsync(connection, read, row.Id, default);

        Assert.Equal(GuildWorkspaceLeaseState.ReconciliationRequired, restored.Value.State);

        Assert.Equal(1, restored.Value.InFlightMutationCount);

        Assert.Equal(row.Epoch, restored.Value.Epoch);

        Assert.True((await GuildWorkspaceLeaseStore.RenewWithinAsync(connection, read, restored.Value, row.HeartbeatAt, row.ExpiresAt.AddMinutes(15), default)).IsFailure);
    }

    [Fact]
    public async Task Released_denial_and_replay_evidence_survives_owner_disappearance_and_reopen_integrity()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord row = Row();

        _ = await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default);

        _ = await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, row, "owner-erased", default);

        // No live owner/principal row remains. Its terminal denial and exact request replay must
        // survive while bootstrap still rejects dangling protected ownership.
        await GuildStorageIntegrity.ValidateAsync(connection, transaction, default, 8);

        Assert.Equal(GuildWorkspaceLeaseState.Released, (await GuildWorkspaceLeaseStore.FindReplayWithinAsync(connection,
            transaction, row.Owner, row.IdempotencyKey, row.RequestDigest, default)).Value.State);
    }

    [Fact]
    public async Task Renew_and_release_replay_the_original_accepted_snapshot_and_share_the_acquire_key_namespace()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord row = Row();

        _ = await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, (await GuildWorkspaceLeaseStore.FindMutationReplayWithinAsync(
            connection, transaction, row.Owner, row.IdempotencyKey, row.RequestDigest, GuildAuthorityAction.LeaseRenew, row.Id, default)).Error.Code);

        GuildMutationIdentity renew = new(Guid.NewGuid(), new string('B', 64));

        row = (await GuildWorkspaceLeaseStore.RenewWithinAsync(connection, transaction, row, row.HeartbeatAt.AddMinutes(1), row.ExpiresAt.AddMinutes(1), default)).Value;

        var accepted = await GuildWorkspaceLeaseStore.RecordMutationWithinAsync(connection, transaction, row, renew, GuildAuthorityAction.LeaseRenew, row.HeartbeatAt, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        GuildWorkspaceLeaseRecord released = (await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, row, "released", default)).Value;

        var replay = await GuildWorkspaceLeaseStore.FindMutationReplayWithinAsync(connection, transaction, row.Owner,
            renew.IdempotencyKey, renew.CanonicalRequestDigest, GuildAuthorityAction.LeaseRenew, row.Id, default);

        Assert.Equal(accepted.Value, replay.Value);

        Assert.Equal(GuildWorkspaceLeaseState.Held, replay.Value.Result.State);

        Assert.Equal(GuildWorkspaceLeaseState.Released, released.State);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, (await GuildWorkspaceLeaseStore.FindMutationReplayWithinAsync(connection,
            transaction, row.Owner, renew.IdempotencyKey, renew.CanonicalRequestDigest, GuildAuthorityAction.LeaseRelease, row.Id, default)).Error.Code);

        GuildWorkspaceLeaseRecord reacquireWithUsedKey = Row() with { Owner = row.Owner, IdempotencyKey = renew.IdempotencyKey };

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, (await GuildWorkspaceLeaseStore.InsertWithinAsync(connection,
            transaction, reacquireWithUsedKey, default)).Error.Code);
    }
}
