using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GrimoireSchemaReadinessDrainTests
{
    static GrimoireSchemaReadinessDrainTests() => SqliteNativeRuntime.Instance.Initialize();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_predecessors_of_a_declared_barrier_drain_and_resume_before_readiness(bool interrupt)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        GrimoireSchemaVersionChainSet original = GrimoireSchemaEvolutionFixture.OneVersionChainSet();

        await GrimoireSchemaTestInstaller.InstallAsync(connection, original, 1536, CancellationToken.None);

        List<string> calls = [];

        PhaseBackfill predecessor = new("predecessor", calls) { InterruptSecondPage = interrupt };

        BlockingPhaseBackfill blocking = new(calls);

        PhaseBackfill optional = new("optional", calls);

        GrimoireSchemaVersionChain core = original.ForTier(GrimoireSchemaTransactionTier.Core);

        GrimoireSchemaVersionStep Step(int from, IGrimoireSchemaBackfill backfill) =>
            new(core.Family, core.TransactionTier, from, from + 1, core.HeadManifest.SourceDefinitionFingerprint, [], backfill);

        GrimoireSchemaVersionChainSet chains = new([
            new(core.HeadManifest with { Version = 4 }, core.HeadObjects,
                [Step(1, predecessor), Step(2, blocking), Step(3, optional)]),
            original.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            original.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator)]);

        GrimoireSchemaInstaller installer = GrimoireSchemaTestInstaller.Create(chains);

        GrimoireSchemaInstallResult installed = await installer.InstallAsync(connection, 1536,
            GrimoireSchemaTestInstaller.CreateContext(), CancellationToken.None);

        string root = Path.GetDirectoryName(new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource)!;

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock();

        using GrimoireSchemaQuiescentExecution execution = GrimoireSchemaQuiescentExecution.ForStartup(connection, held, root, new GrimoireDbReadiness());

        Task<GrimoireSchemaInstallResult> DrainAsync() => GrimoireSchemaReadinessDrain.DrainAsync(connection,
            installer, new(installer, TimeProvider.System), 1536, GrimoireSchemaTestInstaller.CreateContext(), execution,
            installed, CancellationToken.None);

        if (interrupt)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(DrainAsync);

            GrimoireSchemaTransitionJournalRow pending = (await GrimoireSchemaTransitionJournal.ReadAsync(connection,
                null, core.TransactionTier, CancellationToken.None))!;

            Assert.Equal("first-page", pending.BackfillCursor);

            Assert.True(await GrimoireSchemaReadinessDrain.RequiredAsync(connection, chains, CancellationToken.None));

            predecessor.InterruptSecondPage = false;
        }

        GrimoireSchemaInstallResult drained = await DrainAsync();

        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, drained.Core.Health);

        Assert.False(await GrimoireSchemaReadinessDrain.RequiredAsync(connection, chains, CancellationToken.None));

        Assert.DoesNotContain("optional", calls);

        Assert.Equal(2, calls.Count(static value => value == "blocking"));

        GrimoireSchemaTransitionJournalRow after = (await GrimoireSchemaTransitionJournal.ReadAsync(connection,
            null, core.TransactionTier, CancellationToken.None))!;

        Assert.Equal(3, after.CompletedThroughVersion);

        Assert.Equal("optional", after.BackfillName);

        Assert.Equal(1, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));
    }

    [Theory]
    [InlineData("sql-in-data-step")]
    [InlineData("sql-suffix")]
    [InlineData("mismatched-fingerprint-suffix")]
    public async Task Physical_head_match_does_not_admit_a_forged_or_mixed_data_only_suffix(string invalid)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        GrimoireSchemaVersionChainSet original = GrimoireSchemaEvolutionFixture.OneVersionChainSet();

        await GrimoireSchemaTestInstaller.InstallAsync(connection, original, 1536, CancellationToken.None);

        GrimoireSchemaVersionChain core = original.ForTier(GrimoireSchemaTransactionTier.Core);

        List<string> calls = [];

        GrimoireSchemaTransitionStatement sql = new("Injected", 1, "Injected", "INSERT INTO evolution_source VALUES(1,'must not execute')");

        GrimoireSchemaVersionStep first = new(core.Family, core.TransactionTier, 1, 2,
            core.HeadManifest.SourceDefinitionFingerprint, invalid == "sql-in-data-step" ? [sql] : [], new PhaseBackfill("first", calls));

        GrimoireSchemaVersionStep last = new(core.Family, core.TransactionTier, 2, 3,
            invalid == "mismatched-fingerprint-suffix" ? new string('A', 64) : core.HeadManifest.SourceDefinitionFingerprint,
            invalid == "sql-suffix" ? [sql] : [], new PhaseBackfill("last", calls));

        GrimoireSchemaVersionChainSet chains = new([
            new(core.HeadManifest with { Version = 3 }, core.HeadObjects, [first, last]),
            original.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            original.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator)]);

        GrimoireSchemaRefusedException refused = await Assert.ThrowsAsync<GrimoireSchemaRefusedException>(() =>
            GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 1536, CancellationToken.None));

        Assert.Contains("MixedCatalogVersions", refused.Message, StringComparison.Ordinal);

        Assert.Null(await GrimoireSchemaTransitionJournal.ReadAsync(connection, null, core.TransactionTier, CancellationToken.None));

        Assert.Equal(1, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        Assert.Empty(calls);
    }

    private class PhaseBackfill(string name, List<string> calls) : IGrimoireSchemaBackfill
    {
        public string Name => name;

        public int MaxRowsPerBatch => 1;

        internal bool InterruptSecondPage { get; set; }

        public Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(SqliteConnection connection,
            SqliteTransaction transaction, string? cursor, CancellationToken cancellationToken)
        {
            calls.Add(name);

            if (cursor is not null && InterruptSecondPage) throw new InvalidOperationException("interrupted predecessor");

            return Task.FromResult(new GrimoireSchemaBackfillBatch("first-page", 0, cursor is not null));
        }
    }

    private sealed class BlockingPhaseBackfill(List<string> calls) : PhaseBackfill("blocking", calls), IGrimoireQuiescentBackfill
    {
        public Task<GrimoireSchemaBackfillBatch> AdvanceQuiescentBatchAsync(SqliteConnection connection,
            SqliteTransaction transaction, string? cursor, GrimoireSchemaQuiescentExecution execution,
            CancellationToken cancellationToken)
        {
            execution.Require(connection);

            return AdvanceBatchAsync(connection, transaction, cursor, cancellationToken);
        }
    }
}
