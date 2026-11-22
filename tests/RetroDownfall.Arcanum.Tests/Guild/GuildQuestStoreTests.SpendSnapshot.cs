using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_provider_settlement_replays_its_saved_outcome_after_later_bill_and_effect_changes(bool suppliedLegacyEstimates)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db);

        GuildBudgetLedger ledger = new(daily);

        f.UseBudgetParticipant(ledger);

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("snapshot"), quest.Quest.RunId, false, default)).Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, suppliedLegacyEstimates ? 1m : null,
            suppliedLegacyEstimates ? 100 : null);

        Assert.True((await f.Store.ExecuteProviderEffectAsync(execution, daily, effect,
            _ => Task.FromResult(Result<string>.Success("answer")), default)).IsSuccess);

        DateTimeOffset started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            started, started.AddSeconds(2), 7, 3, 1, 2, "{}", 0.125m, BillableOperationStatus.Completed, "request-id");

        Result<GuildBudgetEffect> initial = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(initial.IsSuccess, initial.Error.Message);

        Assert.Null(initial.Value.Request.ReservationId);

        await using (SqliteTransaction tx = connection.BeginTransaction())
        {
            await using SqliteCommand update = connection.CreateCommand();

            update.Transaction = tx;

            update.CommandText = "UPDATE BillableOperations SET InputTokens=9,ActualCostUsd='0.250' WHERE Id=$id";

            update.Parameters.AddWithValue("$id", effect.SourceId.ToString("N"));

            Assert.Equal(1, await update.ExecuteNonQueryAsync());

            GuildBudgetEvidence later = new(Guid.NewGuid(), new string('A', 64), 0.250m, 12, initial.Value.UnknownUsd, false, true);

            Assert.True((await ledger.ApplyEvidenceWithinAsync(connection, tx, effect.EffectId, later, default)).IsSuccess);

            await tx.CommitAsync();
        }

        Result<GuildBudgetEffect> replay = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(initial.Value, replay.Value);

        Assert.Equal(1L, await f.CountAsync("BillableOperations"));

        await using SqliteCommand snapshots = connection.CreateCommand();

        snapshots.CommandText = "SELECT count(*) FROM guild_spend_sources WHERE InitialSnapshot IS NOT NULL AND InitialEventSequence IS NOT NULL";

        Assert.Equal(1L, await snapshots.ExecuteScalarAsync());

        snapshots.CommandText = "UPDATE guild_spend_sources SET InitialOutcome=X'01'";

        await Assert.ThrowsAsync<SqliteException>(() => snapshots.ExecuteNonQueryAsync());

        snapshots.CommandText = "DELETE FROM guild_events";

        await snapshots.ExecuteNonQueryAsync();

        Result<GuildBudgetEffect> afterPrune = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(afterPrune.IsSuccess, afterPrune.Error.Message);

        Assert.Equal(initial.Value, afterPrune.Value);

        await using (SqliteTransaction read = connection.BeginTransaction())
        {
            Result<GuildBudgetEffect?> retained = await GuildSpendSourceStore.ReadInitialWithinAsync(connection, read, effect, bill, default);

            Assert.True(retained.IsSuccess, retained.Error.Message);

            Assert.Equal(initial.Value, retained.Value);

            Result<GuildStoredInitialSpend?> loaded = await GuildSpendSourceStore.LoadInitialWithinAsync(connection, read, effect.EffectId, default);

            Assert.True(loaded.IsSuccess, loaded.Error.Message);

            Assert.NotNull(loaded.Value);

            Assert.Equal("request-id", loaded.Value.Snapshot.ProviderRequestId);

            Assert.Equal("0.125", loaded.Value.Snapshot.ActualCostUsd);

            Assert.True(loaded.Value.EventSequence > 0);

            await read.RollbackAsync();
        }

        snapshots.CommandText = """
            INSERT INTO guild_spend_receipts(ReceiptId,EffectId,EvidenceReceiptId,EvidenceRevision,OriginCode,
                RequestIdentity,RequestDigest,OriginalResult,OriginalEventSequence,AcceptedAtUtc)
            VALUES($receipt,$effect,$evidence,999,2,'wrong-revision',zeroblob(32),X'01',1,'2026-01-01T00:00:00.0000000Z')
            """;

        snapshots.Parameters.AddWithValue("$receipt", Guid.NewGuid().ToString("D").ToUpperInvariant());

        snapshots.Parameters.AddWithValue("$effect", effect.EffectId.ToString("D").ToUpperInvariant());

        snapshots.Parameters.AddWithValue("$evidence", effect.SourceId.ToString("D").ToUpperInvariant());

        SqliteException invalidRevision = await Assert.ThrowsAsync<SqliteException>(() => snapshots.ExecuteNonQueryAsync());

        Assert.Contains("Guild spend receipt requires its exact physical evidence", invalidRevision.Message);

        snapshots.CommandText = snapshots.CommandText.Replace("999", initial.Value.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));

        SqliteException duplicateRevision = await Assert.ThrowsAsync<SqliteException>(() => snapshots.ExecuteNonQueryAsync());

        Assert.Contains("UNIQUE constraint failed: guild_spend_receipts.EffectId, guild_spend_receipts.EvidenceRevision", duplicateRevision.Message);

        snapshots.CommandText = "SELECT count(*) FROM guild_spend_receipts WHERE EffectId=$effect AND EvidenceReceiptId=$evidence AND EvidenceRevision=$revision";

        snapshots.Parameters.AddWithValue("$revision", initial.Value.Revision);

        Assert.Equal(1L, await snapshots.ExecuteScalarAsync());

        snapshots.CommandText = "UPDATE guild_spend_receipts SET OriginalResult=X'02'";

        await Assert.ThrowsAsync<SqliteException>(() => snapshots.ExecuteNonQueryAsync());

        snapshots.CommandText = """
            INSERT INTO guild_spend_refreshes(RefreshId,EffectId,OperationId,RequestDigest)
            VALUES('66666666-6666-6666-6666-666666666666',$effect,'77777777-7777-7777-7777-777777777777',zeroblob(32))
            """;

        await Assert.ThrowsAsync<SqliteException>(() => snapshots.ExecuteNonQueryAsync());

        snapshots.CommandText = "SELECT count(*) FROM guild_operator_sequences WHERE KindCode=5 AND ScopeId=$effect";

        Assert.Equal(1L, await snapshots.ExecuteScalarAsync());

        snapshots.CommandText = "DELETE FROM BillableOperations";

        Assert.Equal(1, await snapshots.ExecuteNonQueryAsync());

        Result<GuildBudgetEffect> afterBillPrune = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(afterBillPrune.IsSuccess, afterBillPrune.Error.Message);

        Assert.Equal(initial.Value, afterBillPrune.Value);

        Assert.Equal(0L, await f.CountAsync("BillableOperations"));

        f.Restart();

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsFailure);

        await using SqliteConnection reopened = await f.OpenAsync();

        await using SqliteTransaction reopenedRead = reopened.BeginTransaction();

        Result<GuildStoredInitialSpend?> afterRestart = await GuildSpendSourceStore.LoadInitialWithinAsync(reopened, reopenedRead, effect.EffectId, default);

        Assert.True(afterRestart.IsSuccess, afterRestart.Error.Message);

        Assert.Equal(initial.Value, afterRestart.Value!.Outcome);
    }

    [Fact]
    public async Task Provider_settlement_rolls_back_on_alias_or_snapshot_write_failure_and_does_not_invent_legacy_evidence()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db);

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("snapshot-fault"), quest.Quest.RunId, false, default)).Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        Assert.True((await f.Store.ExecuteProviderEffectAsync(execution, daily, effect,
            _ => Task.FromResult(Result<string>.Success("answer")), default)).IsSuccess);

        DateTimeOffset started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            started, started.AddSeconds(2), 7, 3, 1, 2, "{}", 0.125m, BillableOperationStatus.Completed, null);

        await using (SqliteTransaction tx = connection.BeginTransaction())
        {
            await TurnRunWriter.RecordBillableOperationWithinAsync(connection, tx, effect.SourceId, bill, default);

            await using SqliteCommand alias = connection.CreateCommand();

            alias.Transaction = tx;

            alias.CommandText = "UPDATE BillableOperations SET Id=$alias WHERE Id=$compact";

            alias.Parameters.AddWithValue("$alias", effect.SourceId.ToString("D").ToUpperInvariant());

            alias.Parameters.AddWithValue("$compact", effect.SourceId.ToString("N"));

            Assert.Equal(1, await alias.ExecuteNonQueryAsync());

            await tx.CommitAsync();
        }

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsFailure);

        Assert.Equal(1L, await f.CountAsync("BillableOperations"));

        Assert.Equal(0L, await f.CountAsync("guild_budget_evidence"));

        Assert.Equal(0L, await f.CountAsync("guild_spend_sources"));

        await using SqliteCommand fault = connection.CreateCommand();

        fault.CommandText = "DELETE FROM BillableOperations; CREATE TRIGGER fixture_spend_abort AFTER UPDATE ON guild_spend_sources BEGIN SELECT RAISE(ABORT,'injected'); END;";

        await fault.ExecuteNonQueryAsync();

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsFailure);

        Assert.Equal(0L, await f.CountAsync("BillableOperations"));

        Assert.Equal(0L, await f.CountAsync("guild_budget_evidence"));

        Assert.Equal(0L, await f.CountAsync("guild_spend_sources"));

        fault.CommandText = "SELECT count(*) FROM guild_events WHERE EventKindCode=12";

        Assert.Equal(0L, await fault.ExecuteScalarAsync());

        fault.CommandText = "DROP TRIGGER fixture_spend_abort";

        await fault.ExecuteNonQueryAsync();

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsSuccess);

        await using (SqliteTransaction integrity = connection.BeginTransaction())
        {
            await GuildStorageIntegrity.ValidateAsync(connection, integrity, default, 15);

            fault.Transaction = integrity;

            fault.CommandText = "DROP TRIGGER guild_operator_sequences_guard_delete; DELETE FROM guild_operator_sequences WHERE KindCode=4";

            await fault.ExecuteNonQueryAsync();

            await Assert.ThrowsAsync<GuildStorageIntegrityException>(() => GuildStorageIntegrity.ValidateAsync(connection, integrity, default, 15));

            await integrity.RollbackAsync();

            fault.Transaction = null;
        }

        fault.CommandText = "DELETE FROM guild_spend_sources; DELETE FROM BillableOperations";

        await fault.ExecuteNonQueryAsync();

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsFailure);

        Assert.Equal(0L, await f.CountAsync("BillableOperations"));

        Assert.Equal(0L, await f.CountAsync("guild_spend_sources"));

        fault.CommandText = "DELETE FROM guild_budget_evidence";

        SqliteException evidenceReferenced = await Assert.ThrowsAsync<SqliteException>(() => fault.ExecuteNonQueryAsync());

        Assert.Contains("FOREIGN KEY constraint failed", evidenceReferenced.Message);

        fault.CommandText = "DELETE FROM guild_spend_receipts";

        Assert.Equal(1, await fault.ExecuteNonQueryAsync());

        fault.CommandText = "DELETE FROM guild_budget_evidence";

        Assert.Equal(1, await fault.ExecuteNonQueryAsync());

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsFailure);

        Assert.Equal(0L, await f.CountAsync("BillableOperations"));

        Assert.Equal(0L, await f.CountAsync("guild_spend_sources"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("actual-generation")]
    public async Task Lost_authority_key_is_not_replaced_for_an_existing_provider_binding(string? actualGeneration)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db);

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("key-loss"), quest.Quest.RunId, false, default)).Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        Assert.True((await f.Store.ExecuteProviderEffectAsync(execution, daily, effect,
            _ => Task.FromResult(Result<string>.Success("answer")), default)).IsSuccess);

        await using (SqliteTransaction tx = connection.BeginTransaction())
        {
            await using SqliteCommand bind = connection.CreateCommand();

            bind.Transaction = tx;

            bind.CommandText = """
                INSERT INTO guild_spend_sources(EffectId,SourceId,SourceKindCode,SourceVersion,InstallationGeneration,
                    AuthorityEpoch,AuthorityKeyId,Endpoint,Provider,Model,PricingSnapshotJson,CredentialFingerprint)
                SELECT $effect,$source,1,1,$generation,AuthorityEpoch,KeyId,'https://openrouter.ai/api/v1',
                    'provider','model','{}',zeroblob(32) FROM guild_spend_authority_state WHERE StateKey=1
                """;

            bind.Parameters.AddWithValue("$effect", effect.EffectId.ToString("D").ToUpperInvariant());

            bind.Parameters.AddWithValue("$source", effect.SourceId.ToString("D").ToUpperInvariant());

            bind.Parameters.AddWithValue("$generation", f.Host.InstallationGeneration.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await bind.ExecuteNonQueryAsync());

            bind.CommandText = "UPDATE guild_spend_sources SET GenerationId='invented' WHERE EffectId=$effect";

            await Assert.ThrowsAsync<SqliteException>(() => bind.ExecuteNonQueryAsync());

            Result<GuildStoredInitialSpend?> before = await GuildSpendSourceStore.LoadInitialWithinAsync(connection, tx, effect.EffectId, default);

            Assert.True(before.IsSuccess);

            Assert.Null(before.Value);

            bind.CommandText = "DELETE FROM guild_spend_authority_state";

            Assert.Equal(1, await bind.ExecuteNonQueryAsync());

            await new GuildCanonicalSchemaDataInitializer().InitializeAsync(connection, tx, GrimoireSchemaTestInstaller.CreateContext(), default);

            bind.CommandText = "SELECT count(*) FROM guild_spend_authority_state";

            Assert.Equal(0L, await bind.ExecuteScalarAsync());

            await tx.CommitAsync();
        }

        DateTimeOffset now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            now, now.AddSeconds(1), 7, 3, 1, 2, "{}", 0.125m, BillableOperationStatus.Completed, actualGeneration);

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsSuccess);

        await using SqliteCommand check = connection.CreateCommand();

        check.CommandText = "SELECT GenerationId FROM guild_spend_sources WHERE EffectId=$effect";

        check.Parameters.AddWithValue("$effect", effect.EffectId.ToString("D").ToUpperInvariant());

        Assert.Equal(string.IsNullOrWhiteSpace(actualGeneration) ? DBNull.Value : actualGeneration, await check.ExecuteScalarAsync());

        check.CommandText = "UPDATE guild_spend_sources SET GenerationId='replacement' WHERE EffectId=$effect";

        await Assert.ThrowsAsync<SqliteException>(() => check.ExecuteNonQueryAsync());
    }
    [Fact]
    public async Task Provider_settlement_rejects_undefined_bill_operation_type_before_persisting_evidence()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db);

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("invalid-operation-type"), quest.Quest.RunId, false, default)).Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        Assert.True((await f.Store.ExecuteProviderEffectAsync(execution, daily, effect,
            _ => Task.FromResult(Result<string>.Success("answer")), default)).IsSuccess);

        DateTimeOffset now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        BillableOperationRecord bill = new(quest.Quest.RunId, (BillableOperationType)int.MaxValue, "provider", "model", "guild",
            now, now.AddSeconds(1), 7, 3, 1, 2, "{}", 0.125m, BillableOperationStatus.Completed, null);

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsFailure);

        Assert.Equal(0L, await f.CountAsync("BillableOperations"));

        Assert.Equal(0L, await f.CountAsync("guild_budget_evidence"));

        Assert.Equal(0L, await f.CountAsync("guild_spend_sources"));

        await using SqliteCommand events = connection.CreateCommand();

        events.CommandText = "SELECT count(*) FROM guild_events WHERE EventKindCode=12";

        Assert.Equal(0L, await events.ExecuteScalarAsync());

        Result<GuildBudgetEffect> accepted = await f.Store.SettleProviderEffectAsync(execution, daily, effect,
            bill with { OperationType = BillableOperationType.Chat }, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);
    }
}
