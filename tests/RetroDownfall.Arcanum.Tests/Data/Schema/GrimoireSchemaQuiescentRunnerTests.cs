using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GrimoireSchemaQuiescentRunnerTests
{
    static GrimoireSchemaQuiescentRunnerTests() => SqliteNativeRuntime.Instance.Initialize();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_declared_blocking_sweep_requires_the_exact_quiescent_execution(bool admitted)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection,
            GrimoireSchemaEvolutionFixture.OneVersionChainSet(), 1536, CancellationToken.None);

        BlockingBackfill backfill = new();

        GrimoireSchemaVersionChainSet chains = GrimoireSchemaEvolutionFixture.TwoVersionChainSet(backfill);

        GrimoireSchemaInstaller installer = GrimoireSchemaTestInstaller.Create(chains);

        await installer.InstallAsync(connection, 1536, GrimoireSchemaTestInstaller.CreateContext(), CancellationToken.None);

        GrimoireSchemaTransitionJournalRow journal = (await GrimoireSchemaTransitionJournal.ReadAsync(
            connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None))!;

        string root = Path.GetDirectoryName(new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource)!;

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock();

        using GrimoireSchemaQuiescentExecution? execution = admitted
            ? GrimoireSchemaQuiescentExecution.ForStartup(connection, held, root, new GrimoireDbReadiness())
            : null;

        GrimoireSchemaBackfillRunner runner = new(installer, TimeProvider.System);

        Task<GrimoireSchemaBackfillProgress> advance = runner.AdvanceAsync(connection,
            chains.ForTier(GrimoireSchemaTransactionTier.Core), journal,
            GrimoireSchemaTestInstaller.CreateContext(), 1, CancellationToken.None, execution);

        if (admitted)
        {
            Assert.True((await advance).StepComplete);

            Assert.Equal(1, backfill.QuiescentCalls);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => advance);

            Assert.Equal(journal, await GrimoireSchemaTransitionJournal.ReadAsync(
                connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None));
        }

        Assert.Equal(0, backfill.OrdinaryCalls);
    }

    private sealed class BlockingBackfill : IGrimoireQuiescentBackfill
    {
        public string Name => "blocking-test";

        public int MaxRowsPerBatch => 1;

        internal int OrdinaryCalls { get; private set; }

        internal int QuiescentCalls { get; private set; }

        public Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(SqliteConnection connection,
            SqliteTransaction transaction, string? cursor, CancellationToken cancellationToken)
        {
            OrdinaryCalls++;

            return Task.FromResult(new GrimoireSchemaBackfillBatch(null, 0, true));
        }

        public Task<GrimoireSchemaBackfillBatch> AdvanceQuiescentBatchAsync(SqliteConnection connection,
            SqliteTransaction transaction, string? cursor, GrimoireSchemaQuiescentExecution execution,
            CancellationToken cancellationToken)
        {
            execution.Require(connection);

            QuiescentCalls++;

            return Task.FromResult(new GrimoireSchemaBackfillBatch(null, 0, true));
        }
    }
}
