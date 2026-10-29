using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed partial class GrimoireDatabaseBootstrapperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocking_quota_transition_finishes_before_actual_bootstrap_readiness_and_requires_lock(bool locked)
    {
        _secretStore.SetApiKey("quota-bootstrap-test-key");

        string database = Path.Combine(_tempDir, "arcanum.db");

        await GrimoireDatabaseBootstrapper.EnsureInitializedWithoutInstallationLockForTestsAsync(
            _secretStore, _passphraseSource, _scopeFactory, database, _tempDir, CancellationToken.None);

        RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDbReadiness readiness = new();

        BarrierBackfill barrier = new(readiness);

        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        GrimoireSchemaVersionChain upgraded = new(core.HeadManifest with { Version = core.HeadVersion + 1 }, core.HeadObjects,
            [.. core.Steps, new(core.Family, core.TransactionTier, core.HeadVersion, core.HeadVersion + 1,
                core.HeadManifest.SourceDefinitionFingerprint, [], barrier)]);

        GrimoireSchemaVersionChainSet chains = new([upgraded,
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator)]);

        IServiceScopeFactory scopes = CreateScopeFactory(_credentialStore, readiness: readiness, chains: chains);

        if (locked)
        {
            using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(_tempDir).BorrowAcquiredLock();

            await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(_secretStore, _passphraseSource, scopes,
                database, _tempDir, held, null, CancellationToken.None);

            Assert.Equal(3, barrier.Pages);

            Assert.True(readiness.IsReady);
        }
        else
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                GrimoireDatabaseBootstrapper.EnsureInitializedWithoutInstallationLockForTestsAsync(
                    _secretStore, _passphraseSource, scopes, database, _tempDir, CancellationToken.None));

            Assert.False(readiness.IsReady);

            Assert.Equal(0, barrier.Pages);
        }
    }

    private sealed class BarrierBackfill(IGrimoireDbReadiness readiness) : IGrimoireQuiescentBackfill
    {
        public string Name => "bootstrap-quota-barrier-test";

        public int MaxRowsPerBatch => 1;

        internal int Pages { get; private set; }

        public Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(SqliteConnection connection,
            SqliteTransaction transaction, string? cursor, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An ordinary execution cannot census quota owners.");

        public Task<GrimoireSchemaBackfillBatch> AdvanceQuiescentBatchAsync(SqliteConnection connection,
            SqliteTransaction transaction, string? cursor, GrimoireSchemaQuiescentExecution execution,
            CancellationToken cancellationToken)
        {
            execution.Require(connection);

            Assert.False(readiness.IsReady);

            Pages++;

            // Synthetic phase-only progress tests readiness ordering without pretending to process a source row.
            return Task.FromResult(new GrimoireSchemaBackfillBatch(Pages.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, Pages == 3));
        }
    }
}
