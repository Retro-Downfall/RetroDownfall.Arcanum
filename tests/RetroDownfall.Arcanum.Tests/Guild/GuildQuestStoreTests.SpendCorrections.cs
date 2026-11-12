using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Remote_spend_reads_preserve_the_aggregate_without_inventing_token_dimensions()
    {
        await using Fixture f = await Fixture.CreateAsync();
        GuildBudgetLedger ledger = new();
        f.UseBudgetParticipant(ledger);
        GuildQuestStored quest = await SeedExternalRemoteSpendAsync(f, ledger, 1.25m, 40);
        var read = await f.Store.ReadOperatorSpendEffectAsync(f.Caller, quest.Quest.Id, quest.Quest.Id, default);
        Assert.True(read.IsSuccess, read.Error.Message);
        Assert.Equal(GuildSpendEffectKind.Remote, read.Value.Kind);
        Assert.Equal(1.25m, read.Value.Facts.KnownUsd);
        Assert.Equal(40L, read.Value.Facts.KnownTokens);
        Assert.Null(read.Value.Facts.LocalTokens);
        Assert.Equal("remoteEffect", read.Value.OperatorIneligibility);
        var list = await f.Store.ListOperatorSpendEffectsAsync(f.Caller, quest.Quest.Id, null, 10, default);
        Assert.True(list.IsSuccess, list.Error.Message);
        Assert.Null(Assert.Single(list.Value.Items).Facts.LocalTokens);
        GuildSpendCorrectionRequest correction = new(Guid.NewGuid(), read.Value.EvidenceRevision, GuildSpendOrigin.OperatorAttested,
            new(41, 0, 0, 0, 1.30m), new string('A', 64));
        Assert.True((await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, quest.Quest.Id, correction, default)).IsFailure);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT ReceiptId,AppliedRevision,Digest FROM guild_budget_evidence WHERE EffectId=$effect";

        command.Parameters.AddWithValue("$effect", quest.Quest.Id.ToString("D").ToUpperInvariant());

        string evidenceId;
        long revision;
        byte[] digest;

        await using (SqliteDataReader evidence = await command.ExecuteReaderAsync())
        {
            Assert.True(await evidence.ReadAsync());
            evidenceId = evidence.GetString(0);
            revision = evidence.GetInt64(1);
            digest = (byte[])evidence.GetValue(2);
        }

        Guid receiptId = Guid.NewGuid();

        DateTimeOffset acceptedAt = DateTimeOffset.UtcNow;

        GuildSpendReceiptDto receipt = new(receiptId, quest.Quest.Id, quest.Quest.Id, GuildSpendOrigin.RemoteReport,
            null, new(1.25m, 40, false, false, true, null), revision, 1, acceptedAt);

        command.CommandText = """
            INSERT INTO guild_spend_receipts(ReceiptId,EffectId,EvidenceReceiptId,EvidenceRevision,OriginCode,RequestIdentity,
                RequestDigest,OriginalResult,OriginalEventSequence,AcceptedAtUtc)
            VALUES($receipt,$effect,$evidence,$revision,1,'remote-evidence-test',$digest,$result,1,$accepted)
            """;

        command.Parameters.AddWithValue("$receipt", receiptId.ToString("D").ToUpperInvariant());
        command.Parameters.AddWithValue("$evidence", evidenceId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$result", JsonSerializer.SerializeToUtf8Bytes(receipt, GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto));
        command.Parameters.AddWithValue("$accepted", UtcInstantText.Format(acceptedAt));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        var validReceipt = await f.Store.ReadOperatorSpendReceiptAsync(f.Caller, quest.Quest.Id, quest.Quest.Id, receiptId, default);
        Assert.True(validReceipt.IsSuccess, validReceipt.Error.Message);
        Assert.Null(validReceipt.Value.Facts.LocalTokens);

        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='guild_budget_evidence_guard_update'";
        string guard = (string)(await command.ExecuteScalarAsync())!;
        command.CommandText = "DROP TRIGGER guild_budget_evidence_guard_update";
        await command.ExecuteNonQueryAsync();
        byte[] forged = (byte[])digest.Clone();
        forged[0] ^= 1;
        command.CommandText = "UPDATE guild_budget_evidence SET Digest=$forged WHERE ReceiptId=$evidence";
        command.Parameters.AddWithValue("$forged", forged);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        command.CommandText = guard;
        await command.ExecuteNonQueryAsync();

        var corrupt = await f.Store.ReadOperatorSpendReceiptAsync(f.Caller, quest.Quest.Id, quest.Quest.Id, receiptId, default);
        Assert.True(corrupt.IsFailure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Correction_preserves_daily_disposition_and_midnight_attribution(bool uncertain)
    {
        await using Fixture f = await Fixture.CreateAsync();
        await using SqliteConnection connection = await f.OpenAsync();
        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);
        ArcanumSettings settings = new() { Cost = new() { Budget = new() { Enabled = true, DailyLimitUsd = 10m } } };
        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));
        f.UseBudgetParticipant(new GuildBudgetLedger(daily));
        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;
        GuildQuestDriver driver = await f.DriverAsync(quest);
        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("hold"), quest.Quest.RunId, false, default)).Value;
        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);
        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 1000);
        Assert.True((await f.Store.ExecuteProviderEffectAsync(execution, daily, effect, _ => Task.FromResult(Result<string>.Success("answer")), default)).IsSuccess);
        DateTimeOffset started = new(2026, 1, 2, 23, 59, 59, TimeSpan.Zero);
        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            started, started.AddSeconds(2), 100, 20, 5, 40, "{}", .25m, BillableOperationStatus.Completed, "request-id");
        var initial = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);
        Assert.True(initial.IsSuccess, initial.Error.Message);
        Assert.NotNull(initial.Value.Request.ReservationId);
        await using SqliteCommand reservation = connection.CreateCommand();
        reservation.CommandText = "UPDATE BudgetReservations SET Status=4 WHERE Id=$id";
        reservation.Parameters.AddWithValue("$id", initial.Value.Request.ReservationId.Value.ToString("N"));
        if (uncertain) Assert.Equal(1, await reservation.ExecuteNonQueryAsync());
        GuildSpendCorrectionRequest request = new(Guid.NewGuid(), initial.Value.Revision, GuildSpendOrigin.OperatorAttested,
            new(110, 25, 45, 6, .30m), new string('B', 64));
        await f.ExecuteAsync("CREATE TRIGGER fixture_abort_daily BEFORE UPDATE ON BudgetReservations BEGIN SELECT RAISE(ABORT,'injected'); END");
        var failed = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, request, default);
        Assert.True(failed.IsFailure);
        Assert.Equal(1L, await f.CountAsync("guild_spend_receipts"));
        Assert.Equal(1L, await f.CountAsync("guild_budget_evidence"));
        await f.ExecuteAsync("DROP TRIGGER fixture_abort_daily");
        var result = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, request, default);
        Assert.True(result.IsSuccess, result.Error.Message);
        reservation.CommandText = "SELECT Status,ReconciledUsd FROM BudgetReservations WHERE Id=$id";
        await using (SqliteDataReader row = await reservation.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            Assert.Equal((long)(uncertain ? BudgetReservationStatus.UncertainPaid : BudgetReservationStatus.Reconciled), row.GetInt64(0));
            Assert.Equal(.30m, ExactUsdText.Read(row, 1));
        }
        daily.UtcNowForTesting = () => new DateTimeOffset(2026, 1, 2, 23, 59, 59, TimeSpan.Zero);
        Assert.Equal(0m, await daily.GetTodayCommittedSpendAsync());
        Assert.Equal(uncertain ? .70m : 0m, await daily.GetTodayOutstandingReservationsAsync());
        daily.UtcNowForTesting = () => new DateTimeOffset(2026, 1, 3, 0, 0, 5, TimeSpan.Zero);
        Assert.Equal(.30m, await daily.GetTodayCommittedSpendAsync());
        Assert.Equal(uncertain ? .70m : 0m, await daily.GetTodayOutstandingReservationsAsync());
    }

    [Fact]
    public async Task Operator_correction_updates_the_original_bill_and_replays_after_pruning()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await using SqliteConnection connection = await f.OpenAsync();
        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);
        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(new()));
        f.UseBudgetParticipant(new GuildBudgetLedger(daily));
        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;
        GuildQuestDriver rootDriver = await f.DriverAsync(root);
        GuildQuestStored quest = await f.ChildAsync(root, rootDriver);
        Assert.NotEqual(quest.Quest.Id, quest.Quest.RootQuestId);
        GuildQuestDriver driver = await f.DriverAsync(quest);
        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("correction"), quest.Quest.RunId, false, default)).Value;
        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);
        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);
        Assert.True((await f.Store.ExecuteProviderEffectAsync(execution, daily, effect, _ => Task.FromResult(Result<string>.Success("answer")), default)).IsSuccess);
        DateTimeOffset started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            started, started.AddSeconds(2), 100, 20, 5, 40, "{}", .25m, BillableOperationStatus.Completed, "request-id");
        var initial = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);
        Assert.True(initial.IsSuccess, initial.Error.Message);
        GuildSpendCorrectionRequest request = new(Guid.NewGuid(), initial.Value.Revision, GuildSpendOrigin.OperatorAttested,
            new(110, 25, 45, 6, .30m), new string('A', 64));
        await using (SqliteCommand corruptDimensions = connection.CreateCommand())
        {
            corruptDimensions.CommandText = "UPDATE BillableOperations SET InputTokens=99,OutputTokens=21 WHERE Id=$id";
            corruptDimensions.Parameters.AddWithValue("$id", effect.SourceId.ToString("N"));
            Assert.Equal(1, await corruptDimensions.ExecuteNonQueryAsync());
            var unproved = await f.Store.ReadOperatorSpendEffectAsync(f.Caller, quest.Quest.Id, effect.EffectId, default);
            Assert.True(unproved.IsFailure);
            var refused = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, request, default);
            Assert.True(refused.IsFailure);
            Assert.Equal(1L, await f.CountAsync("guild_spend_receipts"));
            corruptDimensions.CommandText = "UPDATE BillableOperations SET InputTokens=100,OutputTokens=20 WHERE Id=$id";
            Assert.Equal(1, await corruptDimensions.ExecuteNonQueryAsync());
        }
        await using (SqliteCommand corrupt = connection.CreateCommand())
        {
            corrupt.CommandText = "UPDATE BillableOperations SET InputTokens=9223372036854775807 WHERE Id=$id";
            corrupt.Parameters.AddWithValue("$id", effect.SourceId.ToString("N"));
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
            var refused = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, request, default);
            Assert.True(refused.IsFailure);
            Assert.Equal(1L, await f.CountAsync("guild_spend_receipts"));
            corrupt.CommandText = "UPDATE BillableOperations SET InputTokens=100 WHERE Id=$id";
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }
        await using (SqliteCommand epoch = connection.CreateCommand())
        {
            epoch.CommandText = "SELECT Epoch FROM guild_signet_authority_state WHERE StateKey=1";
            string originalEpoch = (string)(await epoch.ExecuteScalarAsync())!;
            epoch.CommandText = "UPDATE guild_signet_authority_state SET Epoch=$epoch WHERE StateKey=1";
            epoch.Parameters.AddWithValue("$epoch", Guid.NewGuid().ToString("D").ToUpperInvariant());
            Assert.Equal(1, await epoch.ExecuteNonQueryAsync());
            var retired = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, request, default);
            Assert.True(retired.IsFailure);
            Assert.Equal(1L, await f.CountAsync("guild_spend_receipts"));
            epoch.Parameters["$epoch"].Value = originalEpoch;
            Assert.Equal(1, await epoch.ExecuteNonQueryAsync());
        }
        await using (SqliteCommand alias = connection.CreateCommand())
        {
            alias.CommandText = """
                INSERT INTO BillableOperations(Id,RunId,OperationType,Provider,Model,Purpose,StartedAt,CompletedAt,InputTokens,
                    OutputTokens,ReasoningTokens,CachedTokens,PricingSnapshotJson,Status,ProviderRequestId,ActualCostUsd)
                SELECT $alias,RunId,OperationType,Provider,Model,Purpose,StartedAt,CompletedAt,InputTokens,
                    OutputTokens,ReasoningTokens,CachedTokens,PricingSnapshotJson,Status,ProviderRequestId,ActualCostUsd
                FROM BillableOperations WHERE Id=$id
                """;
            alias.Parameters.AddWithValue("$alias", effect.SourceId.ToString("D").ToUpperInvariant());
            alias.Parameters.AddWithValue("$id", effect.SourceId.ToString("N"));
            Assert.Equal(1, await alias.ExecuteNonQueryAsync());
            var collision = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, request, default);
            Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, collision.Error.Code);
            Assert.Equal(1L, await f.CountAsync("guild_spend_receipts"));
            alias.CommandText = "DELETE FROM BillableOperations WHERE Id=$alias";
            Assert.Equal(1, await alias.ExecuteNonQueryAsync());
        }
        foreach (string target in new[] { "UPDATE ON BillableOperations", "INSERT ON guild_budget_evidence", "UPDATE ON guild_budget_effects",
            "UPDATE ON guild_quests", "INSERT ON guild_events", "INSERT ON guild_spend_receipts" })
        {
            await f.ExecuteAsync($"CREATE TRIGGER fixture_abort_spend BEFORE {target} BEGIN SELECT RAISE(ABORT,'injected'); END");
            var failed = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, request, default);
            Assert.True(failed.IsFailure, target);
            Assert.Equal(1L, await f.CountAsync("guild_spend_receipts"));
            Assert.Equal(1L, await f.CountAsync("guild_budget_evidence"));
            await using SqliteCommand check = connection.CreateCommand();
            check.CommandText = "SELECT ActualCostUsd,InputTokens,OutputTokens FROM BillableOperations";
            await using (SqliteDataReader row = await check.ExecuteReaderAsync())
            {
                Assert.True(await row.ReadAsync());
                Assert.Equal(.25m, ExactUsdText.Read(row, 0));
                Assert.Equal(100L, row.GetInt64(1));
                Assert.Equal(20L, row.GetInt64(2));
            }
            await f.ExecuteAsync("DROP TRIGGER fixture_abort_spend");
        }
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Result<GuildSpendCorrectionResultDto>> Compete()
        {
            await start.Task;
            return await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, request, default);
        }
        Task<Result<GuildSpendCorrectionResultDto>>[] racers = Enumerable.Range(0, 4).Select(_ => Compete()).ToArray();
        start.SetResult();
        Result<GuildSpendCorrectionResultDto>[] results = await Task.WhenAll(racers);
        Assert.All(results, value => Assert.True(value.IsSuccess, value.Error.Message));
        Assert.Single(results, value => !value.Value.Replayed);
        Result<GuildSpendCorrectionResultDto> result = results.Single(value => !value.Value.Replayed);
        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(.30m, result.Value.Receipt.Facts.KnownUsd);
        Assert.Equal(135L, result.Value.Receipt.Facts.KnownTokens);
        Assert.True(result.Value.Receipt.Facts.UnknownUsd);
        Assert.False(result.Value.Receipt.Facts.UnknownTokens);
        Assert.True(result.Value.Receipt.Facts.Complete);
        await using (SqliteCommand corruptReceipt = connection.CreateCommand())
        {
            corruptReceipt.CommandText = "SELECT OriginalResult FROM guild_spend_receipts WHERE ReceiptId=$receipt";
            corruptReceipt.Parameters.AddWithValue("$receipt", result.Value.Receipt.ReceiptId.ToString("D").ToUpperInvariant());
            byte[] original = (byte[])(await corruptReceipt.ExecuteScalarAsync())!;
            corruptReceipt.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='guild_spend_receipts_guard_update'";
            string guard = (string)(await corruptReceipt.ExecuteScalarAsync())!;
            corruptReceipt.CommandText = "DROP TRIGGER guild_spend_receipts_guard_update";
            await corruptReceipt.ExecuteNonQueryAsync();
            byte[] forged = JsonSerializer.SerializeToUtf8Bytes(result.Value.Receipt with { Facts = result.Value.Receipt.Facts with { KnownUsd = 999m } },
                GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto);
            corruptReceipt.CommandText = "UPDATE guild_spend_receipts SET OriginalResult=$forged WHERE ReceiptId=$receipt";
            corruptReceipt.Parameters.AddWithValue("$forged", forged);
            Assert.Equal(1, await corruptReceipt.ExecuteNonQueryAsync());
            corruptReceipt.CommandText = guard;
            await corruptReceipt.ExecuteNonQueryAsync();
            var badRead = await f.Store.ReadOperatorSpendReceiptAsync(f.Caller, quest.Quest.Id, effect.EffectId, result.Value.Receipt.ReceiptId, default);
            Assert.True(badRead.IsFailure);
            var badReplay = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, request, default);
            Assert.True(badReplay.IsFailure);
            corruptReceipt.CommandText = "DROP TRIGGER guild_spend_receipts_guard_update";
            await corruptReceipt.ExecuteNonQueryAsync();
            corruptReceipt.Parameters["$forged"].Value = JsonSerializer.SerializeToUtf8Bytes(
                result.Value.Receipt with { EvidenceDigest = new string('F', 64) }, GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto);
            corruptReceipt.CommandText = "UPDATE guild_spend_receipts SET OriginalResult=$forged WHERE ReceiptId=$receipt";
            Assert.Equal(1, await corruptReceipt.ExecuteNonQueryAsync());
            corruptReceipt.CommandText = guard;
            await corruptReceipt.ExecuteNonQueryAsync();
            var badDigestRead = await f.Store.ReadOperatorSpendReceiptAsync(f.Caller, quest.Quest.Id, effect.EffectId, result.Value.Receipt.ReceiptId, default);
            Assert.True(badDigestRead.IsFailure);
            var badDigest = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, request, default);
            Assert.True(badDigest.IsFailure);
            corruptReceipt.CommandText = "DROP TRIGGER guild_spend_receipts_guard_update";
            await corruptReceipt.ExecuteNonQueryAsync();
            corruptReceipt.CommandText = "UPDATE guild_spend_receipts SET OriginalResult=$original WHERE ReceiptId=$receipt";
            corruptReceipt.Parameters.AddWithValue("$original", original);
            Assert.Equal(1, await corruptReceipt.ExecuteNonQueryAsync());
            corruptReceipt.CommandText = guard;
            await corruptReceipt.ExecuteNonQueryAsync();
        }
        await using (SqliteCommand corruptEvidence = connection.CreateCommand())
        {
            corruptEvidence.CommandText = "SELECT Digest FROM guild_budget_evidence WHERE ReceiptId=$receipt";
            corruptEvidence.Parameters.AddWithValue("$receipt", result.Value.Receipt.ReceiptId.ToString("D").ToUpperInvariant());
            byte[] original = (byte[])(await corruptEvidence.ExecuteScalarAsync())!;
            byte[] forged = (byte[])original.Clone();
            forged[0] ^= 1;
            corruptEvidence.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='guild_budget_evidence_guard_update'";
            string guard = (string)(await corruptEvidence.ExecuteScalarAsync())!;
            corruptEvidence.CommandText = "DROP TRIGGER guild_budget_evidence_guard_update";
            await corruptEvidence.ExecuteNonQueryAsync();
            corruptEvidence.CommandText = "UPDATE guild_budget_evidence SET Digest=$forged WHERE ReceiptId=$receipt";
            corruptEvidence.Parameters.AddWithValue("$forged", forged);
            Assert.Equal(1, await corruptEvidence.ExecuteNonQueryAsync());
            corruptEvidence.CommandText = guard;
            await corruptEvidence.ExecuteNonQueryAsync();
            var badRead = await f.Store.ReadOperatorSpendReceiptAsync(f.Caller, quest.Quest.Id, effect.EffectId, result.Value.Receipt.ReceiptId, default);
            var badReplay = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, request, default);
            Assert.True(badRead.IsFailure && badReplay.IsFailure, $"Read refused: {badRead.IsFailure}; replay refused: {badReplay.IsFailure}.");
            corruptEvidence.CommandText = "DROP TRIGGER guild_budget_evidence_guard_update";
            await corruptEvidence.ExecuteNonQueryAsync();
            corruptEvidence.CommandText = "UPDATE guild_budget_evidence SET Digest=$original WHERE ReceiptId=$receipt";
            corruptEvidence.Parameters.AddWithValue("$original", original);
            Assert.Equal(1, await corruptEvidence.ExecuteNonQueryAsync());
            corruptEvidence.CommandText = guard;
            await corruptEvidence.ExecuteNonQueryAsync();
        }
        await using (SqliteCommand physical = connection.CreateCommand())
        {
            physical.CommandText = "SELECT InputTokens,OutputTokens,CachedTokens,ReasoningTokens,ActualCostUsd,CompletedAt,PricingSnapshotJson,Status FROM BillableOperations";
            await using SqliteDataReader row = await physical.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(110L, row.GetInt64(0));
            Assert.Equal(25L, row.GetInt64(1));
            Assert.Equal(45L, row.GetInt64(2));
            Assert.Equal(6L, row.GetInt64(3));
            Assert.Equal(.30m, ExactUsdText.Read(row, 4));
            Assert.Equal(started.AddSeconds(2), UtcInstantText.Parse(row.GetString(5)));
            Assert.Equal("{}", row.GetString(6));
            Assert.Equal((int)BillableOperationStatus.Completed, row.GetInt32(7));
        }
        Assert.True((await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, Guid.NewGuid(), request, default)).IsFailure);
        Assert.True((await f.Store.CorrectOperatorSpendAsync(f.Caller, Guid.NewGuid(), effect.EffectId, request, default)).IsFailure);
        Assert.Equal(1L, await f.CountAsync("BillableOperations"));
        Assert.Equal(2L, await f.CountAsync("guild_spend_receipts"));
        Assert.Equal(0L, await f.CountAsync("BudgetReservations"));
        Assert.Equal(2L, await f.CountAsync("guild_budget_evidence"));
        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, (await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id,
            effect.EffectId, request with { Amounts = request.Amounts! with { Usd = .31m } }, default)).Error.Code);
        GuildSpendCorrectionRequest next = new(Guid.NewGuid(), result.Value.Receipt.EvidenceRevision, GuildSpendOrigin.OperatorAttested,
            new(120, 30, 50, 7, .35m), new string('C', 64));
        GuildSpendCorrectionRequest competing = next with { IdempotencyKey = Guid.NewGuid() };
        TaskCompletionSource secondStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Result<GuildSpendCorrectionResultDto>> CompeteNext(GuildSpendCorrectionRequest candidate)
        {
            await secondStart.Task;
            return await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, candidate, default);
        }
        Task<Result<GuildSpendCorrectionResultDto>> firstNext = CompeteNext(next);
        Task<Result<GuildSpendCorrectionResultDto>> secondNext = CompeteNext(competing);
        secondStart.SetResult();
        Result<GuildSpendCorrectionResultDto>[] nextResults = await Task.WhenAll(firstNext, secondNext);
        Assert.Single(nextResults, value => value.IsSuccess);
        Assert.Equal(ErrorCodes.Guild.RevisionConflict, Assert.Single(nextResults, value => value.IsFailure).Error.Code);
        var later = Assert.Single(nextResults, value => value.IsSuccess);
        Assert.True(later.IsSuccess, later.Error.Message);
        Assert.Equal(3L, await f.CountAsync("guild_spend_receipts"));
        Assert.Equal(3L, await f.CountAsync("guild_budget_evidence"));
        await using (SqliteCommand checkEvent = connection.CreateCommand())
        {
            checkEvent.CommandText = "SELECT SpendEffectId,SpendReceiptId,SpendEvidenceRevision,SpendOriginCode FROM guild_events WHERE EventSequence=$sequence AND RootQuestId=$root";
            checkEvent.Parameters.AddWithValue("$sequence", result.Value.Receipt.EventSequence);
            checkEvent.Parameters.AddWithValue("$root", quest.Quest.RootQuestId.ToString("D").ToUpperInvariant());
            await using SqliteDataReader row = await checkEvent.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(effect.EffectId.ToString("D").ToUpperInvariant(), row.GetString(0));
            Assert.Equal(result.Value.Receipt.ReceiptId.ToString("D").ToUpperInvariant(), row.GetString(1));
            Assert.Equal(result.Value.Receipt.EvidenceRevision, row.GetInt64(2));
            Assert.Equal((int)GuildSpendOrigin.OperatorAttested, row.GetInt32(3));
            Assert.False(await row.ReadAsync());
        }
        var effectRead = await f.Store.ReadOperatorSpendEffectAsync(f.Caller, quest.Quest.Id, effect.EffectId, default);
        Assert.True(effectRead.IsSuccess, effectRead.Error.Message);
        Assert.Equal(.35m, effectRead.Value.Facts.KnownUsd);
        Assert.Equal(150L, effectRead.Value.Facts.KnownTokens);
        Assert.Equal(120L, effectRead.Value.Facts.LocalTokens!.InputTokens);
        var exact = await f.Store.ReadOperatorSpendReceiptAsync(f.Caller, quest.Quest.Id, effect.EffectId, result.Value.Receipt.ReceiptId, default);
        Assert.True(exact.IsSuccess, exact.Error.Message);
        Assert.Equal(.30m, exact.Value.Facts.KnownUsd);
        var page = await f.Store.ListOperatorSpendReceiptsAsync(f.Caller, quest.Quest.Id, effect.EffectId, null, 1, default);
        Assert.True(page.IsSuccess, page.Error.Message);
        Assert.Single(page.Value.Items);
        Assert.NotNull(page.Value.NextCursor);
        var page2 = await f.Store.ListOperatorSpendReceiptsAsync(f.Caller, quest.Quest.Id, effect.EffectId, page.Value.NextCursor, 1, default);
        Assert.True(page2.IsSuccess, page2.Error.Message);
        Assert.Single(page2.Value.Items);
        Assert.NotEqual(page.Value.Items[0].ReceiptId, page2.Value.Items[0].ReceiptId);
        var wrongScope = await f.Store.ListOperatorSpendReceiptsAsync(f.Caller, quest.Quest.Id, Guid.NewGuid(), page.Value.NextCursor, 1, default);
        Assert.True(wrongScope.IsFailure);
        var effects = await f.Store.ListOperatorSpendEffectsAsync(f.Caller, quest.Quest.Id, null, 1, default);
        Assert.True(effects.IsSuccess, effects.Error.Message);
        Assert.Single(effects.Value.Items);
        await f.ExecuteAsync("DELETE FROM guild_spend_sources");
        var snapshotless = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId,
            new(Guid.NewGuid(), later.Value.Receipt.EvidenceRevision, GuildSpendOrigin.OperatorAttested,
                new(130, 35, 55, 8, .40m), new string('D', 64)), default);
        Assert.Equal("Guild.InitialEvidenceUnavailable", snapshotless.Error.Code);
        Assert.Equal(3L, await f.CountAsync("guild_spend_receipts"));
        await f.ExecuteAsync("DELETE FROM guild_events; DELETE FROM BillableOperations");
        var expired = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId,
            new(Guid.NewGuid(), later.Value.Receipt.EvidenceRevision, GuildSpendOrigin.OperatorAttested,
                new(130, 35, 55, 8, .40m), new string('D', 64)), default);
        Assert.Equal("Guild.SourceExpired", expired.Error.Code);
        f.Restart();
        var retained = await f.Store.ReadOperatorSpendReceiptAsync(f.Caller, quest.Quest.Id, effect.EffectId, result.Value.Receipt.ReceiptId, default);
        Assert.True(retained.IsSuccess, retained.Error.Message);
        Assert.Equal(.30m, retained.Value.Facts.KnownUsd);
        Result<GuildSpendCorrectionResultDto> replay = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, request, default);
        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.True(replay.Value.Replayed);
        Assert.Equal(result.Value.Receipt.EventSequence, replay.Value.Receipt.EventSequence);
        Assert.Equal(.30m, replay.Value.Receipt.Facts.KnownUsd);
    }
}
