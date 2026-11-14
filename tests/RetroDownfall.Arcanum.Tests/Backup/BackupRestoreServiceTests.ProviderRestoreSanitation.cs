using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Guild;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreServiceTests
{
    [Fact]
    public async Task Encrypted_archive_retires_captured_provider_source_and_preserves_receipts()
    {
        Fixture fixture = await CreateFixtureAsync();

        GuildQuestStoreTests.RestoreCapture capture;

        string keyId;

        string epoch;

        string key;

        string source;

        string receipts;

        await using (SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            fixture.DatabasePath, fixture.GrimoireSecret, false, default))
        {
            capture = await GuildQuestStoreTests.CaptureForEncryptedRestoreAsync(connection.ConnectionString);

            keyId = await RestoreEvidenceAsync(connection, "SELECT KeyId FROM guild_spend_authority_state");

            epoch = await RestoreEvidenceAsync(connection, "SELECT AuthorityEpoch FROM guild_spend_authority_state");

            key = await RestoreEvidenceAsync(connection, "SELECT hex(CredentialKey) FROM guild_spend_authority_state");

            source = await RestoreEvidenceAsync(connection, "SELECT EffectId || SourceId || AuthorityKeyId || AuthorityEpoch || hex(CredentialFingerprint) || hex(InitialSnapshot) || hex(InitialDigest) || hex(InitialOutcome) || InitialEventSequence FROM guild_spend_sources");

            receipts = await RestoreEvidenceAsync(connection, "SELECT group_concat(ReceiptId || EffectId || EvidenceReceiptId || hex(RequestDigest) || hex(OriginalResult) || OriginalEventSequence, '|') FROM (SELECT * FROM guild_spend_receipts ORDER BY ReceiptId)");
        }

        string archive = await fixture.CreateBackupAsync("provider-restore.arcbackup");

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

        BackupRestoreResult restored = await Restore(new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret }, options).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false,
                ProtectedStateMode: BackupProtectedStateMode.PurgeProtectedState, ProtectedStateConfirmed: true),
            Passphrase.AsMemory(), default);

        Assert.True(restored.Status == BackupRestoreStatus.Completed,
            $"Restore status {restored.Status}: {string.Join("; ", restored.Issues.Select(issue => $"{issue.Code}: {issue.Message}"))}");

        await using SqliteConnection reopened = await BackupRestoreDatabaseWorker.OpenAsync(
            fixture.DatabasePath, fixture.GrimoireSecret, false, default);

        Assert.NotEqual(keyId, await RestoreEvidenceAsync(reopened, "SELECT KeyId FROM guild_spend_authority_state"));

        Assert.NotEqual(epoch, await RestoreEvidenceAsync(reopened, "SELECT AuthorityEpoch FROM guild_spend_authority_state"));

        Assert.NotEqual(key, await RestoreEvidenceAsync(reopened, "SELECT hex(CredentialKey) FROM guild_spend_authority_state"));

        Assert.Equal(source, await RestoreEvidenceAsync(reopened, "SELECT EffectId || SourceId || AuthorityKeyId || AuthorityEpoch || hex(CredentialFingerprint) || hex(InitialSnapshot) || hex(InitialDigest) || hex(InitialOutcome) || InitialEventSequence FROM guild_spend_sources"));

        Assert.Equal(receipts, await RestoreEvidenceAsync(reopened, "SELECT group_concat(ReceiptId || EffectId || EvidenceReceiptId || hex(RequestDigest) || hex(OriginalResult) || OriginalEventSequence, '|') FROM (SELECT * FROM guild_spend_receipts ORDER BY ReceiptId)"));

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(reopened, default)).IsSuccess);

        await using SqliteTransaction transaction = reopened.BeginTransaction();

        var verified = await capture.Credential.VerifyWithinAsync(reopened, transaction, capture.EffectId, capture.InstallationGeneration, default);

        Assert.True(verified.IsFailure);

        Assert.Equal("Guild.SpendAuthorityRetired", verified.Error.Code);
    }

    internal static async Task<string> RestoreEvidenceAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
}
