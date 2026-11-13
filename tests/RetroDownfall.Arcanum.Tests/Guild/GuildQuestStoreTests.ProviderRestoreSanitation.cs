using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Restored_provider_source_is_retired_even_with_original_credential()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        Assert.True((await scenario.VerifyAsync(scenario.Credential())).IsSuccess);

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT KeyId,AuthorityEpoch,CredentialKey FROM guild_spend_authority_state WHERE StateKey=1";

        string keyId;

        string epoch;

        byte[] key;

        await using (SqliteDataReader row = await command.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());

            keyId = row.GetString(0);

            epoch = row.GetString(1);

            key = (byte[])row.GetValue(2);
        }

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(connection, default)).IsSuccess);

        await using (SqliteDataReader row = await command.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());

            Assert.NotEqual(keyId, row.GetString(0));

            Assert.NotEqual(epoch, row.GetString(1));

            Assert.NotEqual(key, (byte[])row.GetValue(2));
        }

        var result = await scenario.VerifyAsync(scenario.Credential());

        Assert.True(result.IsFailure);

        Assert.Equal("Guild.SpendAuthorityRetired", result.Error.Code);

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(connection, default)).IsSuccess);
    }

    [Fact]
    public async Task Committed_proof_rejects_either_matching_imported_authority_component()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(connection, default)).IsSuccess);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT KeyId,AuthorityEpoch FROM guild_spend_authority_state";

        string keyId;

        string epoch;

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());

            keyId = reader.GetString(0);

            epoch = reader.GetString(1);
        }

        command.CommandText = "UPDATE guild_spend_authority_state SET KeyId=(SELECT AuthorityKeyId FROM guild_spend_sources LIMIT 1)";

        await command.ExecuteNonQueryAsync();

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(connection, default)).IsFailure);

        command.CommandText = "UPDATE guild_spend_authority_state SET KeyId=$key,AuthorityEpoch=(SELECT AuthorityEpoch FROM guild_spend_sources LIMIT 1)";

        command.Parameters.AddWithValue("$key", keyId);

        await command.ExecuteNonQueryAsync();

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(connection, default)).IsFailure);

        command.CommandText = "UPDATE guild_spend_authority_state SET AuthorityEpoch=$epoch";

        command.Parameters.AddWithValue("$epoch", epoch);

        await command.ExecuteNonQueryAsync();

        await using SqliteConnection reopened = await scenario.Fixture.OpenAsync();

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(reopened, default)).IsSuccess);
    }

    [Fact]
    public async Task Failed_sanitation_rolls_back_spend_and_signet_authority_together()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT KeyId || AuthorityEpoch || hex(CredentialKey) || (SELECT Epoch FROM guild_signet_authority_state) FROM guild_spend_authority_state";

        object? before = await command.ExecuteScalarAsync();

        command.CommandText = "CREATE TEMP TRIGGER abort_restore BEFORE UPDATE ON guild_signets BEGIN SELECT RAISE(ABORT,'injected'); END";

        await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<SqliteException>(() => BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(connection, default));

        command.CommandText = "SELECT KeyId || AuthorityEpoch || hex(CredentialKey) || (SELECT Epoch FROM guild_signet_authority_state) FROM guild_spend_authority_state";

        Assert.Equal(before, await command.ExecuteScalarAsync());

        Assert.True((await scenario.VerifyAsync(scenario.Credential())).IsSuccess);
    }

    [Fact]
    public async Task Restore_preserves_captured_source_and_real_receipt_evidence()
    {
        await using RefreshApplicationScenario scenario = await RefreshApplicationScenario.CreateAsync();

        Assert.True((await scenario.ApplyAsync()).IsSuccess);

        await using SqliteCommand command = scenario.Connection.CreateCommand();

        command.CommandText = "SELECT hex(CredentialFingerprint) || hex(InitialSnapshot) || hex(InitialDigest) || hex(InitialOutcome) || InitialEventSequence || AuthorityKeyId || AuthorityEpoch FROM guild_spend_sources";

        object? source = await command.ExecuteScalarAsync();

        command.CommandText = "SELECT group_concat(ReceiptId || EffectId || EvidenceReceiptId || hex(RequestDigest) || hex(OriginalResult) || OriginalEventSequence, '|') FROM (SELECT * FROM guild_spend_receipts ORDER BY ReceiptId)";

        object? receipts = await command.ExecuteScalarAsync();

        Assert.NotNull(receipts);

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(scenario.Connection, default)).IsSuccess);

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(scenario.Connection, default)).IsSuccess);

        Assert.Equal(receipts, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT hex(CredentialFingerprint) || hex(InitialSnapshot) || hex(InitialDigest) || hex(InitialOutcome) || InitialEventSequence || AuthorityKeyId || AuthorityEpoch FROM guild_spend_sources";

        Assert.Equal(source, await command.ExecuteScalarAsync());

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(scenario.Connection, default)).IsSuccess);
    }

    [Fact]
    public async Task Missing_or_malformed_authority_with_provider_source_fails_closed()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(connection, default)).IsSuccess);

        command.CommandText = "DELETE FROM guild_spend_authority_state";

        await command.ExecuteNonQueryAsync();

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(connection, default)).IsFailure);

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(connection, default)).IsFailure);

        command.CommandText = "PRAGMA ignore_check_constraints=ON; INSERT INTO guild_spend_authority_state VALUES(1,'invalid','invalid',X'00'); PRAGMA ignore_check_constraints=OFF;";

        await command.ExecuteNonQueryAsync();

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(connection, default)).IsFailure);

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(connection, default)).IsFailure);
    }

    [Fact]
    public async Task Source_free_database_allows_absent_lazy_spend_authority()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await using SqliteConnection connection = await fixture.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "DELETE FROM guild_spend_authority_state";

        await command.ExecuteNonQueryAsync();

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(connection, default)).IsSuccess);

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(connection, default)).IsSuccess);

        command.CommandText = "SELECT COUNT(*) FROM guild_spend_authority_state";

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Restore_preserves_pending_refresh_and_its_accounting_evidence()
    {
        await using RefreshApplicationScenario scenario = await RefreshApplicationScenario.CreateAsync();

        await using SqliteCommand command = scenario.Connection.CreateCommand();

        command.CommandText = "SELECT RefreshId || EffectId || OperationId || hex(RequestDigest) FROM guild_spend_refreshes";

        object? request = await command.ExecuteScalarAsync();

        Assert.NotNull(request);

        command.CommandText = "SELECT group_concat(EffectId || SourceId || coalesce(ReservationId,''), '|') FROM (SELECT * FROM guild_budget_effects ORDER BY EffectId)";

        object? effects = await command.ExecuteScalarAsync();

        Assert.NotNull(effects);

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(scenario.Connection, default)).IsSuccess);

        Assert.Equal(effects, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT RefreshId || EffectId || OperationId || hex(RequestDigest) FROM guild_spend_refreshes";

        Assert.Equal(request, await command.ExecuteScalarAsync());

        var verification = await scenario.Scenario.VerifyAsync(scenario.Scenario.Credential());

        Assert.True(verification.IsFailure);

        Assert.Equal("Guild.SpendAuthorityRetired", verification.Error.Code);
    }

    internal sealed record RestoreCapture(GuildSpendSourceCredential Credential, Guid EffectId, Guid InstallationGeneration);

    internal static async Task<RestoreCapture> CaptureForEncryptedRestoreAsync(string connectionString)
    {
        Fixture fixture = Fixture.ForExistingDatabase(connectionString);

        await using SpendScenario scenario = await SpendScenario.CreateAsync(fixture: fixture);

        Assert.True((await scenario.VerifyAsync(scenario.Credential())).IsSuccess);

        return new(scenario.Credential(), scenario.EffectId, scenario.Fixture.Host.InstallationGeneration);
    }
}
