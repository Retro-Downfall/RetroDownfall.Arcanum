using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class CampaignRollupStoreTests
{
    [Fact]
    public async Task A_native_read_lock_is_unavailable_and_the_same_read_succeeds_after_release()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        fixture.Connection.DefaultTimeout = 1;

        await fixture.ExecuteAsync("PRAGMA journal_mode=DELETE;");

        await using Microsoft.Data.Sqlite.SqliteConnection holder = new(fixture.Connection.ConnectionString);

        await holder.OpenAsync();

        await using var held = holder.CreateCommand();

        held.CommandText = "BEGIN EXCLUSIVE;";

        await held.ExecuteNonQueryAsync();

        try
        {
            var refused = await fixture.Store.ReadStatusAsync(Campaign, CancellationToken.None);

            Assert.True(refused.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);

            Assert.Contains("SQLite=5", refused.Error.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("write", refused.Error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            held.CommandText = "ROLLBACK;";

            await held.ExecuteNonQueryAsync();
        }

        Assert.True((await fixture.Store.ReadStatusAsync(Campaign, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task A_failed_deferred_commit_returns_a_structural_Campaign_refusal_without_publication()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        var prepared = await fixture.PrepareAsync();

        await fixture.ExecuteAsync("CREATE TABLE parent_probe(Id INTEGER PRIMARY KEY); CREATE TABLE child_probe(Id INTEGER REFERENCES parent_probe(Id) DEFERRABLE INITIALLY DEFERRED); CREATE TRIGGER deferred_probe AFTER INSERT ON campaign_contribution_artifacts BEGIN INSERT INTO child_probe VALUES(42); END;");

        var result = await fixture.Store.PublishContributionAsync(prepared, "decision", null, null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM child_probe;"));
    }

    [Fact]
    public async Task An_engine_rollback_preserves_the_Campaign_refusal_and_all_publication_frontiers()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        var prepared = await fixture.PrepareAsync();

        await fixture.ExecuteAsync("CREATE TRIGGER refuse_publication BEFORE INSERT ON campaign_contribution_artifacts BEGIN SELECT RAISE(ROLLBACK, 'PRIVATE-rollback-canary'); END;");

        var result = await fixture.Store.PublishContributionAsync(prepared, "decision", null, null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.DoesNotContain("PRIVATE", result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_state WHERE Revision > 0;"));
    }

    [Fact]
    public async Task A_native_busy_begin_refuses_Campaign_preparation_without_advancing_state()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        fixture.Connection.DefaultTimeout = 1;

        await using Microsoft.Data.Sqlite.SqliteConnection holder = new(fixture.Connection.ConnectionString);

        await holder.OpenAsync();

        await using Microsoft.Data.Sqlite.SqliteTransaction held = holder.BeginTransaction(deferred: false);

        var refused = await fixture.Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_state;"));

        await held.RollbackAsync();

        Assert.True((await fixture.Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None)).IsSuccess);
    }

    [Theory]
    [InlineData(-1, ErrorCodes.Covenant.Unavailable)]
    [InlineData(-2, ErrorCodes.Covenant.Unavailable)]
    [InlineData(5, ErrorCodes.Covenant.Unavailable)]
    [InlineData(14, ErrorCodes.Covenant.Unavailable)]
    [InlineData(1, ErrorCodes.Covenant.IntegrityFailure)]
    public async Task Campaign_read_acquisition_failure_is_contextual_and_classified(int code, string expected)
    {
        CampaignRollupStore store = new(new FaultingCovenantConnectionSource(code), CovenantSqliteConnectionInitializer.Instance);

        Result<CampaignRollupStatus> result = await store.ReadStatusAsync(Campaign, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(expected, result.Error.Code);

        Assert.DoesNotContain("PRIVATE", result.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("erasure inventory", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_Campaign_table_is_a_structural_read_refusal()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        await fixture.ExecuteAsync("ALTER TABLE campaign_rollup_state RENAME TO PRIVATE_missing_table;");

        Result<CampaignRollupStatus> result = await fixture.Store.ReadStatusAsync(Campaign, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.DoesNotContain("PRIVATE", result.Error.Message, StringComparison.Ordinal);
    }
}
