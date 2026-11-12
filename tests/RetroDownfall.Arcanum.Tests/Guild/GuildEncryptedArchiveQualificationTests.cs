using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Schema;
using RetroDownfall.Arcanum.Tests.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreServiceTests
{
    [Fact]
    public async Task Encrypted_archive_restore_retires_authority_and_purges_goal_but_retains_uncertain_dispatch()
    {
        Fixture fixture = await CreateFixtureAsync();

        await using (SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            fixture.DatabasePath, fixture.GrimoireSecret, false, CancellationToken.None))
        {
            byte[] digest = SHA256.HashData([]);

            await GuildQuestSchemaTests.SeedQuestAsync(connection, goalDigest: digest);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                UPDATE covenant_authority_state SET InstallationIdentity='12345678-1234-4234-8234-123456789ABC' WHERE StateKey=1;
                INSERT INTO guild_content(ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,ExactGenerationIds,CreatedAtUtc)
                VALUES('CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',NULL,NULL,1,'11111111-1111-1111-1111-111111111111',1,$digest,'text/plain',X'',1,1,$generations,'2000-01-01T00:00:00.0000000Z');
                INSERT INTO guild_signets(SignetId,AgentId,RunId,QuestId,RootQuestId,GrantJson,IssuedAtUtc,OriginPrincipalId,
                    InstallationGeneration,AuthorityEpoch,SessionId,CampaignId,Revision,CredentialGeneration,TokenDigest,RevocationGeneration,Revoked,RestoredInert,Retired)
                SELECT 'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB','88888888-8888-8888-8888-888888888888','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                    '11111111-1111-1111-1111-111111111111','11111111-1111-1111-1111-111111111111',$grant,'2026-11-06T00:00:00.0000000Z',
                    '44444444-4444-4444-4444-444444444444','33333333-3333-3333-3333-333333333333',Epoch,NULL,NULL,1,1,zeroblob(32),0,0,0,0
                FROM guild_signet_authority_state WHERE StateKey=1;
                UPDATE LongRunningOperations SET LeaseOwner='archive-host',LeaseExpiresAt='2099-01-01T00:00:00.0000000Z' WHERE Kind='guild-quest';
                UPDATE guild_quests SET AccountingStateCode=2,Revision=Revision+1;
                INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc)
                VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',zeroblob(32),0,'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,1,'2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:00.0000000Z');
                """;

            GenerationProvenance provenance = GenerationProvenance.Create([CovenantOperationGateFixture.DatasetGeneration]);

            command.Parameters.AddWithValue("$digest", digest);

            command.Parameters.AddWithValue("$generations", provenance.ToCanonicalExactBytes());

            command.Parameters.AddWithValue("$grant", GuildSignetGrantJson.Encode(new([], [], [],
                new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null)).Value);

            _ = await command.ExecuteNonQueryAsync();

            var labelled = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction,
                new(SensitiveArtifactKind.GuildContent, Guid.Parse("CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC"), null, null, null,
                    1, new(digest), ContentSensitivity.CovenantDerived, provenance), CancellationToken.None);

            Assert.True(labelled.IsSuccess, labelled.Error.Message);

            await transaction.CommitAsync();
        }

        byte[] header = new byte[16];

        await using (FileStream file = File.OpenRead(fixture.DatabasePath))
        {
            await file.ReadExactlyAsync(header);
        }

        Assert.NotEqual("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(header));

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            string wrongSecret = Convert.ToBase64String(new byte[32]);

            await using SqliteConnection wrong = await BackupRestoreDatabaseWorker.OpenAsync(
                fixture.DatabasePath, wrongSecret, true, CancellationToken.None);

            await using SqliteCommand query = wrong.CreateCommand();

            query.CommandText = "SELECT count(*) FROM sqlite_master";

            _ = await query.ExecuteScalarAsync();
        });

        string archive = await fixture.CreateBackupAsync("guild-protected.arcbackup");

        InMemoryOsCredentialStore credentials = new();

        BackupRestoreServiceOptions options = new()
        {
            RestoreStaging = new CovenantRestoreStagingServices(
                new CovenantRestoreStagingTests.RecordingExclusiveGate(),
                new CovenantRestoreStagingTests.RecordingRestoreMarkerLifecycle(),
                new BackupRestoreJournalAnchorStore(credentials, new BackupRestoreJournalKeyProvider(credentials),
                    new BackupRestoreJournalInstallationIdentityProvider(credentials)),
                new BackupRestoreJournalInstallationIdentityProvider(credentials),
                new BackupRestoreJournalKeyProvider(credentials),
                new BackupRestoreEffectDigestCalculator()),
        };

        using BackupRestoreServiceTests clean = new();

        BackupRestoreResult cleanResult = await clean.Restore(new RecordingSecretStore(), options).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false,
                ProtectedStateMode: BackupProtectedStateMode.PurgeProtectedState, ProtectedStateConfirmed: true),
            Passphrase.AsMemory(), CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, cleanResult.Status);

        Assert.Contains(cleanResult.Issues, issue => issue.Code == "Covenant.ManualRecoveryRequired");

        BackupRestoreResult result = await Restore(new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret }, options).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false,
                ProtectedStateMode: BackupProtectedStateMode.PurgeProtectedState, ProtectedStateConfirmed: true),
            Passphrase.AsMemory(), CancellationToken.None);

        Assert.True(result.Status == BackupRestoreStatus.Completed,
            $"Restore status {result.Status}: {string.Join("; ", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"))}");

        await using SqliteConnection restored = await BackupRestoreDatabaseWorker.OpenAsync(
            fixture.DatabasePath, fixture.GrimoireSecret, false, CancellationToken.None);

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(restored, CancellationToken.None)).IsSuccess);

        await using (SqliteTransaction transaction = restored.BeginTransaction(deferred: false))
        {
            var signet = await GuildSignetStore.ReadAsync(restored, transaction,
                Guid.Parse("11111111-1111-1111-1111-111111111111"), true, CancellationToken.None);

            Assert.True(signet.IsSuccess, signet.Error.Message);

            var rotation = await GuildSignetStore.RotateAsync(restored, transaction, signet.Value,
                new byte[32], CancellationToken.None);

            Assert.True(rotation.IsFailure);

            await transaction.RollbackAsync();
        }

        GuildGrant grant = new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null);

        GuildSignetAuthorityTests.HostPolicy policy = new(grant);

        GuildRecoveryContext recovery = new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"), 2);

        policy.Recoveries.Add(recovery);

        FixtureOrdinaryConnectionFactory connections = new(restored.ConnectionString);

        using SignetAuthority authority = new(new(connections), new GrimoireConnectionAdmissionGate(TimeProvider.System), policy, TimeProvider.System);

        var rebound = await authority.RebindAsync(recovery, CancellationToken.None);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, rebound.Error.Code);

        Assert.Equal("1", await ScalarAsync(restored, "SELECT COUNT(*) FROM guild_signets WHERE RestoredInert=1"));

        Assert.Equal("0", await ScalarAsync(restored, "SELECT COUNT(*) FROM guild_content WHERE ContentKindCode=1"));

        Assert.Equal("1", await ScalarAsync(restored, "SELECT COUNT(*) FROM guild_content_erasure_receipts WHERE ContentKindCode=1"));

        Assert.Equal("1", await ScalarAsync(restored, "SELECT COUNT(*) FROM guild_dispatches WHERE EvidenceCode=2"));

        Assert.Equal("1", await ScalarAsync(restored, "SELECT COUNT(*) FROM guild_quests WHERE Name IS NULL AND AccountingStateCode=2"));

        Assert.Equal("1", await ScalarAsync(restored, "SELECT COUNT(*) FROM LongRunningOperations o JOIN guild_quests q ON q.OperationId=o.Id WHERE o.State=0 AND o.LeaseOwner IS NULL AND o.LeaseExpiresAt IS NULL AND o.CheckpointVersion=1 AND length(o.CheckpointPayload)>0"));
    }
}
