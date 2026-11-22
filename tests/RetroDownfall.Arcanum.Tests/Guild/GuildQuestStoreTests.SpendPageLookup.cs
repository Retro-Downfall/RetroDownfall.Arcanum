using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Spend_page_censuses_bill_identities_once_and_seeks_each_unique_bill()
    {
        SpendTrace? trace = null;

        await using Fixture f = await Fixture.CreateAsync(inner => trace = new SpendTrace(inner));

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db);

        GuildBudgetLedger ledger = new(daily);

        f.UseBudgetParticipant(ledger);

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("page"), quest.Quest.RunId, false, default)).Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);

        DateTimeOffset started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        List<GuildBudgetEffectRequest> effects = [];

        for (int i = 0; i < 101; i++)
        {
            GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
                GuildBudgetEffectKind.LocalProvider, i == 100 ? Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab") : Guid.NewGuid(), null, null, null);

            effects.Add(effect);

            var sent = await f.Store.ExecuteProviderEffectAsync(execution, daily, effect, _ => Task.FromResult(Result<string>.Success("answer")), default);

            Assert.True(sent.IsSuccess, sent.Error.Message);

            BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
                started, started.AddSeconds(2), 7, 3, 1, 2, "{}", .125m, BillableOperationStatus.Completed, "request-id");

            var settled = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

            Assert.True(settled.IsSuccess, settled.Error.Message);
        }

        Guid remoteId = Guid.NewGuid();

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            GuildBudgetEffectRequest remote = new(remoteId, quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
                GuildBudgetEffectKind.External, Guid.NewGuid(), null, null, null);

            var prepared = await ledger.PrepareEffectWithinAsync(connection, transaction, remote, default);

            Assert.True(prepared.IsSuccess, prepared.Error.Message);

            var exposed = await ledger.ExposeEffectWithinAsync(connection, transaction, remoteId, prepared.Value.Revision, default);

            Assert.True(exposed.IsSuccess, exposed.Error.Message);

            var applied = await ledger.ApplyEvidenceWithinAsync(connection, transaction, remoteId,
                new(Guid.NewGuid(), ReceiptDigest("remote-page"), .5m, 12, false, false, true), default);

            Assert.True(applied.IsSuccess, applied.Error.Message);

            await transaction.CommitAsync();
        }

        Guid lastSource = effects[^1].SourceId;

        string mixedSource = string.Concat(lastSource.ToString("D").Select((value, index) =>
            index % 2 == 0 ? char.ToUpperInvariant(value) : char.ToLowerInvariant(value)));

        await using (SqliteCommand alias = connection.CreateCommand())
        {
            alias.CommandText = "UPDATE BillableOperations SET Id=$mixed WHERE Id=$id";

            alias.Parameters.AddWithValue("$mixed", mixedSource);

            alias.Parameters.AddWithValue("$id", lastSource.ToString("N"));

            Assert.Equal(1, await alias.ExecuteNonQueryAsync());
        }

        trace!.Enabled = true;

        var page = await f.Store.ListOperatorSpendEffectsAsync(f.Caller, quest.Quest.Id, null, 100, default);

        trace.Enabled = false;

        Assert.True(page.IsSuccess, page.Error.Message);

        Assert.Equal(100, page.Value.Items.Length);

        Assert.True(trace.CensusCount == 1, $"census={trace.CensusCount}, seeks={trace.BillSeekCount}, other bill reads={trace.OtherBillReadCount}");

        Assert.Equal(100, trace.BillSeekCount);

        Assert.Equal(0, trace.OtherBillReadCount);

        Assert.NotNull(page.Value.NextCursor);

        await using (SqliteCommand plan = connection.CreateCommand())
        {
            plan.CommandText = "EXPLAIN QUERY PLAN " + trace.CensusSql;

            List<string> details = [];

            await using (SqliteDataReader reader = await plan.ExecuteReaderAsync())
                while (await reader.ReadAsync()) details.Add(reader.GetString(3));

            Assert.Contains(details, detail => detail.Contains("COVERING INDEX", StringComparison.OrdinalIgnoreCase));

            plan.CommandText = "EXPLAIN QUERY PLAN " + trace.SeekSql;

            details.Clear();

            await using (SqliteDataReader reader = await plan.ExecuteReaderAsync())
                while (await reader.ReadAsync()) details.Add(reader.GetString(3));

            Assert.Contains(details, detail => detail.Contains("SEARCH BillableOperations USING INDEX", StringComparison.OrdinalIgnoreCase));
        }

        trace.CensusCount = 0;

        trace.BillSeekCount = 0;

        trace.OtherBillReadCount = 0;

        trace.Enabled = true;

        var second = await f.Store.ListOperatorSpendEffectsAsync(f.Caller, quest.Quest.Id, page.Value.NextCursor, 100, default);

        trace.Enabled = false;

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Equal(1, trace.CensusCount);

        Assert.Equal(1, trace.BillSeekCount);

        Assert.Equal(0, trace.OtherBillReadCount);

        Assert.Equal(2, second.Value.Items.Length);

        Assert.Equal(effects[^1].EffectId, second.Value.Items[0].EffectId);

        Assert.Equal(remoteId, second.Value.Items[1].EffectId);

        Assert.Equal("remoteEffect", second.Value.Items[1].OperatorIneligibility);

        Assert.Null(second.Value.NextCursor);

        // A collision on a different physical run must not poison the earlier page or be missed on continuation.
        await using (SqliteCommand alias = connection.CreateCommand())
        {
            alias.CommandText = """
                INSERT INTO InferenceRuns(Id,RequestId,SessionId,Surface,Purpose,StartedAt,CompletedAt,Status,IdempotencyClaimId)
                SELECT $run,RequestId,SessionId,Surface,Purpose,StartedAt,CompletedAt,Status,NULL FROM InferenceRuns WHERE Id=$originalRun;
                INSERT INTO BillableOperations(Id,RunId,OperationType,Provider,Model,Purpose,StartedAt,CompletedAt,InputTokens,
                    OutputTokens,ReasoningTokens,CachedTokens,PricingSnapshotJson,Status,ProviderRequestId,ActualCostUsd)
                SELECT $alias,$run,OperationType,Provider,Model,Purpose,StartedAt,CompletedAt,InputTokens,
                    OutputTokens,ReasoningTokens,CachedTokens,PricingSnapshotJson,Status,ProviderRequestId,ActualCostUsd
                FROM BillableOperations WHERE Id=$original;
                """;

            alias.Parameters.AddWithValue("$run", Guid.NewGuid().ToString("N"));

            alias.Parameters.AddWithValue("$originalRun", quest.Quest.RunId.ToString("N"));

            alias.Parameters.AddWithValue("$alias", lastSource.ToString("N").ToUpperInvariant());

            alias.Parameters.AddWithValue("$original", mixedSource);

            Assert.Equal(2, await alias.ExecuteNonQueryAsync());
        }
        var firstWithLaterCollision = await f.Store.ListOperatorSpendEffectsAsync(f.Caller, quest.Quest.Id, null, 100, default);

        Assert.True(firstWithLaterCollision.IsSuccess, firstWithLaterCollision.Error.Message);

        var collision = await f.Store.ListOperatorSpendEffectsAsync(f.Caller, quest.Quest.Id, firstWithLaterCollision.Value.NextCursor, 100, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, collision.Error.Code);

        await using (SqliteCommand mutate = connection.CreateCommand())
        {
            mutate.Parameters.AddWithValue("$compact", lastSource.ToString("N").ToUpperInvariant());

            mutate.Parameters.AddWithValue("$dashed", mixedSource);

            mutate.Parameters.AddWithValue("$effect", effects[^1].EffectId.ToString("D").ToUpperInvariant());

            mutate.CommandText = "DELETE FROM BillableOperations WHERE Id=$compact";

            Assert.Equal(1, await mutate.ExecuteNonQueryAsync());

            mutate.CommandText = "UPDATE BillableOperations SET InputTokens=8 WHERE Id=$dashed";

            Assert.Equal(1, await mutate.ExecuteNonQueryAsync());

            var mismatch = await f.Store.ListOperatorSpendEffectsAsync(f.Caller, quest.Quest.Id, firstWithLaterCollision.Value.NextCursor, 100, default);

            Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, mismatch.Error.Code);

            mutate.CommandText = "UPDATE BillableOperations SET InputTokens=7 WHERE Id=$dashed; DELETE FROM guild_spend_sources WHERE EffectId=$effect";

            Assert.Equal(2, await mutate.ExecuteNonQueryAsync());

            var snapshotless = await f.Store.ListOperatorSpendEffectsAsync(f.Caller, quest.Quest.Id, firstWithLaterCollision.Value.NextCursor, 100, default);

            Assert.True(snapshotless.IsSuccess, snapshotless.Error.Message);

            Assert.Equal("initialEvidenceUnavailable", snapshotless.Value.Items[0].OperatorIneligibility);

            mutate.CommandText = "DELETE FROM BillableOperations WHERE Id=$dashed";

            Assert.Equal(1, await mutate.ExecuteNonQueryAsync());

            var expired = await f.Store.ListOperatorSpendEffectsAsync(f.Caller, quest.Quest.Id, firstWithLaterCollision.Value.NextCursor, 100, default);

            Assert.True(expired.IsSuccess, expired.Error.Message);

            Assert.Equal("sourceExpired", expired.Value.Items[0].OperatorIneligibility);
        }
    }

    private sealed class SpendTrace(FixtureOrdinaryConnectionFactory inner) : IGrimoireOrdinaryConnectionFactory
    {
        internal bool Enabled;

        internal int CensusCount;

        internal int BillSeekCount;

        internal int OtherBillReadCount;

        internal string? CensusSql;

        internal string? SeekSql;

        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection,
            CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) => inner.AcquireScopedAsync(connection, mode, cancellationToken);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind kind, CancellationToken cancellationToken)
        {
            var result = await inner.OpenFreshAsync(kind, cancellationToken);

            if (result.IsFailure) return result.Error;

            IGrimoireOrdinaryConnectionLease lease = result.Value;

            raw.sqlite3_trace(lease.Connection.Handle, (object? _, string sql) =>
            {
                if (!Enabled || !sql.Contains("FROM BillableOperations", StringComparison.OrdinalIgnoreCase)) return;

                if (sql.Contains("SELECT Id FROM BillableOperations", StringComparison.OrdinalIgnoreCase))
                {
                    CensusCount++;

                    CensusSql ??= sql;
                }
                else if (sql.Contains("COLLATE BINARY", StringComparison.OrdinalIgnoreCase))
                {
                    BillSeekCount++;

                    SeekSql ??= sql;
                }
                else OtherBillReadCount++;
            }, null);

            return Result<IGrimoireOrdinaryConnectionLease>.Success(new TraceLease(lease));
        }

        private sealed class TraceLease(IGrimoireOrdinaryConnectionLease innerLease) : IGrimoireOrdinaryConnectionLease
        {
            public SqliteConnection Connection => innerLease.Connection;

            public void Dispose()
            {
                raw.sqlite3_trace(Connection.Handle, (strdelegate_trace)null!, null);

                innerLease.Dispose();
            }
            public async ValueTask DisposeAsync()
            {
                raw.sqlite3_trace(Connection.Handle, (strdelegate_trace)null!, null);

                await innerLease.DisposeAsync();
            }
        }
    }
}
