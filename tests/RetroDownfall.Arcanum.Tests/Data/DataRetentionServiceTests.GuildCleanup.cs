using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task Guild_cleanup_failure_rolls_back_every_participant_and_keeps_family_cursor()
    {
        RequireSqlCipher();

        await ExecuteAsync("CREATE TABLE guild_cleanup_test_rows (Id INTEGER PRIMARY KEY); INSERT INTO guild_cleanup_test_rows VALUES (1), (2);");

        _ = await SeedSessionAsync(pinned: false);

        await DeleteGuildTestOwnerAsync();

        var cleanup = GuildCleanup(new[] { new CleanupParticipant(1, false), new CleanupParticipant(2, true) });

        var result = await cleanup.RunBatchAsync(8, 8, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(2, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_cleanup_test_rows;"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 2;"));

        var retried = await GuildCleanup(new[] { new CleanupParticipant(1, false), new CleanupParticipant(2, false) })
            .RunBatchAsync(8, 8, CancellationToken.None);

        Assert.True(retried.IsSuccess, retried.Error.Message);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_cleanup_test_rows;"));

        Assert.True(await CampaignScalarCountAsync("SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 2;") > 0);
    }

    [SkippableFact]
    public async Task Guild_cleanup_unavailable_family_defers_with_unchanged_cursor()
    {
        RequireSqlCipher();

        _ = await SeedSessionAsync(pinned: false);

        await DeleteGuildTestOwnerAsync();

        await ExecuteAsync("UPDATE grimoire_feature_schemas SET HealthCode = 99 WHERE FamilyCode = 2;");

        var result = await GuildCleanup([]).RunBatchAsync(8, 8, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 2;"));
    }

    [SkippableFact]
    public async Task Guild_cleanup_blocked_protected_purge_preserves_content_and_cursor_then_retries()
    {
        RequireSqlCipher();

        (Guid owner, _) = await SeedSessionAsync(pinned: false);

        Guid content = Guid.NewGuid();

        await SeedCleanupContentAsync(content, owner);

        await DeleteGuildTestOwnerAsync();

        var purger = new CleanupBlockedPurger();

        var result = await new GuildOwnerCleanupCoordinator(FixtureOrdinaryConnectionFactory.For(_db!),
            new GrimoireConnectionAdmissionGate(TimeProvider.System), purger, FixtureLabeledArtifactGuard.For(_db!), [], TimeProvider.System)
            .RunBatchAsync(8, 8, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content;"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 2;"));

        var retry = await GuildCleanup([]).RunBatchAsync(8, 8, CancellationToken.None);

        Assert.True(retry.IsSuccess, retry.Error.Message);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content;"));
    }

    [SkippableFact]
    public async Task Guild_orphan_sweep_is_bounded_and_materializes_resumable_owner_work()
    {
        RequireSqlCipher();

        await SeedCleanupContentAsync(Guid.NewGuid(), Guid.NewGuid());

        await SeedCleanupContentAsync(Guid.NewGuid(), Guid.NewGuid());

        var failed = await new GuildOwnerCleanupCoordinator(FixtureOrdinaryConnectionFactory.For(_db!),
            new GrimoireConnectionAdmissionGate(TimeProvider.System), new CleanupBlockedPurger(), FixtureLabeledArtifactGuard.For(_db!), [], TimeProvider.System)
            .RunBatchAsync(8, 1, CancellationToken.None);

        Assert.True(failed.IsFailure);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_owner_cleanup;"));

        var retried = await GuildCleanup([]).RunBatchAsync(8, 1, CancellationToken.None);

        Assert.True(retried.IsSuccess, retried.Error.Message);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content;"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 2;"));
    }

    [SkippableFact]
    public async Task Guild_cleanup_rechecks_a_label_written_after_the_unlabeled_purge_result()
    {
        RequireSqlCipher();

        (Guid owner, _) = await SeedSessionAsync(pinned: false);

        Guid content = Guid.NewGuid();

        await SeedCleanupContentAsync(content, owner);

        await DeleteGuildTestOwnerAsync();

        CleanupUnlabeledPurger purger = new(async () =>
        {
            SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            var written = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction,
                new DerivedArtifactWrite(SensitiveArtifactKind.GuildContent, content, null, null, null, 1,
                    new CovenantDigest(SHA256.HashData(new byte[] { 97, 98 })), ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Guid.NewGuid()])), CancellationToken.None);

            Assert.True(written.IsSuccess, written.Error.Message);

            await transaction.CommitAsync();
        });

        var result = await new GuildOwnerCleanupCoordinator(FixtureOrdinaryConnectionFactory.For(_db!),
            new GrimoireConnectionAdmissionGate(TimeProvider.System), purger, FixtureLabeledArtifactGuard.For(_db!), [], TimeProvider.System)
            .RunBatchAsync(8, 8, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content;"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode=17;"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode=2;"));
    }

    private async Task DeleteGuildTestOwnerAsync()
    {
        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        using var scope = CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention);

        await ExecuteAsync("DELETE FROM Sessions;");
    }

    private Task SeedCleanupContentAsync(Guid content, Guid owner) => ExecuteAsync(
        """
        INSERT INTO guild_content(ContentId,SessionId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
        VALUES(@content,@owner,0,@content,1,zeroblob(32),'text/plain',X'6162',0,0,'2000-01-01T00:00:00.0000000Z');
        """, ("@content", content.ToString("D").ToUpperInvariant()), ("@owner", owner.ToString("D").ToUpperInvariant()));

    private sealed class CleanupBlockedPurger : ICovenantSensitiveArtifactPurger
    {
        public ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(IReadOnlyList<CovenantSensitivePurgeTarget> targets, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<CovenantSensitivePurgeOutcome>.Success(new(targets.Select(target =>
                new CovenantSensitivePurgeResult(target.ArtifactId, target.Kind, CovenantSensitivePurgeDisposition.Blocked, CovenantErasureBlocker.StorageUnavailable)).ToArray(), CovenantArtifactErasureProgress.Empty)));
    }

    private GuildOwnerCleanupCoordinator GuildCleanup(IReadOnlyList<IGuildOwnerCleanupParticipant> participants) =>
        new(FixtureOrdinaryConnectionFactory.For(_db!), new GrimoireConnectionAdmissionGate(TimeProvider.System),
            new CleanupUnlabeledPurger(), FixtureLabeledArtifactGuard.For(_db!), participants, TimeProvider.System);

    private sealed class CleanupParticipant(int id, bool fails) : IGuildOwnerCleanupParticipant
    {
        public ValueTask<Result> SealAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> ReconcileAsync(GuildCleanupOwner owner, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public async ValueTask<Result> DeleteAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = "DELETE FROM guild_cleanup_test_rows WHERE Id = $id;";

            command.Parameters.AddWithValue("$id", id);

            await command.ExecuteNonQueryAsync(cancellationToken);

            return fails ? Result.Failure(new Error(ErrorCodes.Guild.Unavailable, "Participant deferred.")) : Result.Success();
        }
    }

    private sealed class CleanupUnlabeledPurger(Func<Task>? after = null) : ICovenantSensitiveArtifactPurger
    {
        public async ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(IReadOnlyList<CovenantSensitivePurgeTarget> targets, CancellationToken cancellationToken = default)
        {
            if (after is not null)
            {
                await after();
            }

            return Result<CovenantSensitivePurgeOutcome>.Success(new(targets.Select(target =>
                new CovenantSensitivePurgeResult(target.ArtifactId, target.Kind, CovenantSensitivePurgeDisposition.Unlabeled, CovenantErasureBlocker.None)).ToArray(), CovenantArtifactErasureProgress.Empty));
        }
    }
}
